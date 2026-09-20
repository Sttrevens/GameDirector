using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GameDirector.Core.Capture;
using GameDirector.Core.Dsl;
using Newtonsoft.Json;
using UnityEngine;

namespace GameDirector.Unity
{
    /// <summary>
    /// Play-mode HTTP JSON bridge between an external director process (CLI /
    /// MCP server) and the running game. System.Net.HttpListener on a background
    /// thread; every Unity-API-touching request is marshalled to the main thread
    /// via a queue drained in Update.
    ///
    /// Routes and the default port come from GameDirector.Core's BridgeRoutes /
    /// BridgeDefaults, so this server and every client bind the same contract.
    ///
    /// Security posture: loopback only, play mode only, no auth. This is a local
    /// authoring tool, never ship it enabled in a release build — the component
    /// refuses to start outside the Editor unless explicitly allowed.
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    public sealed class DirectorBridgeServer : MonoBehaviour
    {
        // Bridge saturation bounds, named once: how much queued work one game
        // frame may drain, how much may wait, and how long a request waits.
        private const int MainThreadBudgetPerUpdate = 4;
        private const int MaxQueuedRequests = 32;
        private const int RequestTimeoutMs = 15000;
        private const int MaxBodyBytes = 1024 * 1024;

        [SerializeField] private int port = BridgeDefaults.UnityPort;
        [SerializeField] private bool allowOutsideEditor;

        private HttpListener _listener;
        private Thread _thread;
        private volatile bool _running;

        private sealed class Job
        {
            public System.Func<byte[]> Work;       // runs on main thread
            public string ContentType = "application/json";
            public readonly TaskCompletionSource<bool> Done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public int State; // 0 queued, 1 executing, 2 completed, -1 cancelled
            public byte[] Result;
            public string Error;
        }

        private readonly ConcurrentQueue<Job> _jobs = new ConcurrentQueue<Job>();
        private DirectorRuntime _runtime;
        private int _queued;

        public int Port => port;

        private void Awake()
        {
            if (!Application.isEditor && !allowOutsideEditor)
            {
                Debug.LogWarning("[GameDirector] Bridge disabled outside the Editor (set allowOutsideEditor to override).");
                enabled = false;
                return;
            }
            _runtime = gameObject.GetComponent<DirectorRuntime>() ?? gameObject.AddComponent<DirectorRuntime>();
        }

        private void OnEnable()
        {
            if (!enabled) return;
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                _listener.Prefixes.Add("http://localhost:" + port + "/");
                _listener.Start();
                _running = true;
                _thread = new Thread(ListenLoop) { IsBackground = true, Name = "GameDirectorBridge" };
                _thread.Start();
                Debug.Log("[GameDirector] Bridge listening on http://127.0.0.1:" + port + "/");
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[GameDirector] Bridge failed to start: " + ex.Message);
            }
        }

        private void OnDisable()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            _listener = null;
            while (_jobs.TryDequeue(out var job)) {
                Interlocked.Decrement(ref _queued);
                if (Interlocked.CompareExchange(ref job.State, -1, 0) == 0) {
                    job.Error = "bridge stopped";job.Done.TrySetResult(false);
                }
            }
        }

        private void Update()
        {
            int budget = MainThreadBudgetPerUpdate;
            while (budget-- > 0 && _jobs.TryDequeue(out var job))
            {
                Interlocked.Decrement(ref _queued);
                if (Interlocked.CompareExchange(ref job.State, 1, 0) != 0) continue;
                try { job.Result = job.Work(); }
                catch (System.Exception ex) { job.Error = ex.Message; }
                finally { Interlocked.Exchange(ref job.State, 2); job.Done.TrySetResult(true); }
            }
        }

        private void ListenLoop()
        {
            while (_running && _listener != null && _listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch (HttpListenerException) { break; }
                catch (System.ObjectDisposedException) { break; }
                catch { continue; }
                ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            try
            {
                string path = (ctx.Request.Url != null ? ctx.Request.Url.AbsolutePath : "/").TrimEnd('/');
                string method = ctx.Request.HttpMethod;
                if (!string.IsNullOrEmpty(ctx.Request.Headers["Origin"])) {
                    Respond(ctx, 403, "application/json", "{\"error\":\"browser-origin bridge requests are not allowed\"}");return;
                }
                if (method == "POST" && ctx.Request.HasEntityBody && !(ctx.Request.ContentType??"").StartsWith("application/json", System.StringComparison.OrdinalIgnoreCase)) {
                    Respond(ctx, 415, "application/json", "{\"error\":\"application/json required\"}");return;
                }

                if (method == "GET" && path == "/" + BridgeRoutes.Health)
                {
                    Respond(ctx, 200, "application/json", "{\"ok\":true}");
                    return;
                }

                byte[] body = null;
                if (method == "POST")
                {
                    using var ms = new System.IO.MemoryStream();
                    var buffer = new byte[8192]; int read;
                    while ((read = ctx.Request.InputStream.Read(buffer, 0, buffer.Length)) > 0) {
                        if (ms.Length + read > MaxBodyBytes) { Respond(ctx, 413, "application/json", "{\"error\":\"request body too large\"}");return; }
                        ms.Write(buffer,0,read);
                    }
                    body = ms.ToArray();
                }

                var job = new Job { Work = () => RouteOnMainThread(method, path, body) };
                if ((method == "GET" && path == "/" + BridgeRoutes.Capture) || (method == "POST" && path == "/" + BridgeRoutes.TakeFrame)) job.ContentType = "image/png";
                if (Interlocked.Increment(ref _queued) > MaxQueuedRequests || !_running) {
                    Interlocked.Decrement(ref _queued); Respond(ctx, 503, "application/json", "{\"error\":\"bridge busy or stopping\"}");return;
                }
                _jobs.Enqueue(job);
                if (!job.Done.Task.Wait(RequestTimeoutMs)) {
                    bool cancelled = Interlocked.CompareExchange(ref job.State, -1, 0) == 0;
                    Respond(ctx, 504, "application/json", JsonConvert.SerializeObject(new {error="main thread timeout", cancelledBeforeExecution=cancelled})); return;
                }
                if (job.Error != null) { Respond(ctx, 400, "application/json", JsonConvert.SerializeObject(new { error = job.Error })); return; }
                Respond(ctx, 200, job.ContentType, job.Result);
            }
            catch (System.Exception ex)
            {
                try { Respond(ctx, 500, "application/json", JsonConvert.SerializeObject(new { error = ex.Message })); } catch { }
            }
            finally
            {
                try { ctx.Response.OutputStream.Close(); } catch { }
            }
        }

        private byte[] RouteOnMainThread(string method, string path, byte[] body)
        {
            switch (method + " " + path)
            {
                case "GET /" + BridgeRoutes.Manifest:
                    return Json(_runtime.Manifest);
                case "POST /" + BridgeRoutes.TimelinePlay:
                    return Json(_runtime.PlayFromJson(Encoding.UTF8.GetString(body ?? System.Array.Empty<byte>())));
                case "POST /" + BridgeRoutes.TimelineStop:
                    _runtime.Stop();
                    return Json(new { ok = true });
                case "POST /" + BridgeRoutes.TakeStart:
                    return Json(_runtime.BeginTake(BridgeJson.Deserialize<TakeRequest>(Encoding.UTF8.GetString(body))));
                case "POST /" + BridgeRoutes.TakeFrame:
                    return _runtime.NextFrame(BridgeJson.Deserialize<FrameRequest>(Encoding.UTF8.GetString(body)));
                case "POST /" + BridgeRoutes.TakeStop:
                    _runtime.StopTake(BridgeJson.Deserialize<FrameRequest>(Encoding.UTF8.GetString(body)).TakeId);
                    return Json(new {ok=true});
                case "GET /" + BridgeRoutes.Take:
                    return Json(_runtime.GetTakeDto());
                case "GET /" + BridgeRoutes.Status:
                    return Json(_runtime.GetStatusDto());
                case "GET /" + BridgeRoutes.Capture:
                    return _runtime.CapturePng();
                default:
                    throw new System.InvalidOperationException("no route: " + method + " " + path);
            }
        }

        private static byte[] Json(object dto) => Encoding.UTF8.GetBytes(BridgeJson.Serialize(dto));

        private static void Respond(HttpListenerContext ctx, int status, string contentType, string text) =>
            Respond(ctx, status, contentType, Encoding.UTF8.GetBytes(text));

        private static void Respond(HttpListenerContext ctx, int status, string contentType, byte[] bytes)
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = contentType;
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }
    }
}
