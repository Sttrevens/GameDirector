using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading;
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
    /// Endpoints (default http://127.0.0.1:39777):
    ///   GET  /health          plain JSON, answered off-thread (no Unity API)
    ///   GET  /manifest        main thread; serializes the adapter manifest
    ///   POST /timeline/play   body = TimelineAsset JSON; compiles server-side
    ///   POST /timeline/stop
    ///   GET  /status
    ///   GET  /capture         PNG bytes from the director camera
    ///
    /// Security posture: loopback only, play mode only, no auth. This is a local
    /// authoring tool, never ship it enabled in a release build — the component
    /// refuses to start outside the Editor unless explicitly allowed.
    /// </summary>
    [DefaultExecutionOrder(-10000)]
    public sealed class DirectorBridgeServer : MonoBehaviour
    {
        [SerializeField] private int port = 39777;
        [SerializeField] private bool allowOutsideEditor;

        private HttpListener _listener;
        private Thread _thread;
        private volatile bool _running;

        private sealed class Job
        {
            public System.Func<byte[]> Work;       // runs on main thread
            public string ContentType = "application/json";
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public byte[] Result;
            public string Error;
        }

        private readonly ConcurrentQueue<Job> _jobs = new ConcurrentQueue<Job>();
        private DirectorRuntime _runtime;

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
        }

        private void Update()
        {
            while (_jobs.TryDequeue(out var job))
            {
                try { job.Result = job.Work(); }
                catch (System.Exception ex) { job.Error = ex.Message; }
                finally { job.Done.Set(); }
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

                if (method == "GET" && path == "/health")
                {
                    Respond(ctx, 200, "application/json", "{\"ok\":true}");
                    return;
                }

                byte[] body = null;
                if (method == "POST")
                {
                    using var ms = new System.IO.MemoryStream();
                    ctx.Request.InputStream.CopyTo(ms);
                    body = ms.ToArray();
                }

                var job = new Job { Work = () => RouteOnMainThread(method, path, body) };
                if (method == "GET" && path == "/capture") job.ContentType = "image/png";
                _jobs.Enqueue(job);
                if (!job.Done.Wait(15000)) { Respond(ctx, 504, "application/json", "{\"error\":\"main thread timeout\"}"); return; }
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
                case "GET /manifest":
                    return Json(_runtime.Manifest);
                case "POST /timeline/play":
                    return Json(_runtime.PlayFromJson(Encoding.UTF8.GetString(body ?? System.Array.Empty<byte>())));
                case "POST /timeline/stop":
                    _runtime.Stop();
                    return Json(new { ok = true });
                case "GET /status":
                    return Json(_runtime.GetStatusDto());
                case "GET /capture":
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
