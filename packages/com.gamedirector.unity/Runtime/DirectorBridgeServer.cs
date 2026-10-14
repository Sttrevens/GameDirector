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
    [DefaultExecutionOrder(-10000)] // engine-local: Unity callback priority, not a millisecond bridge budget
    public sealed class DirectorBridgeServer : MonoBehaviour
    {
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
            public string Code;
            public int Status = 200;
        }
        private static bool TakeRoute(string method, string path) =>
            method == "GET" && (path == "/" + BridgeRoutes.Health || path == "/" + BridgeRoutes.Take) ||
            method == "POST" && (path == "/" + BridgeRoutes.TakeStart || path == "/" + BridgeRoutes.TakeFrame || path == "/" + BridgeRoutes.TakeStop);

        private readonly ConcurrentQueue<Job> _jobs = new ConcurrentQueue<Job>();
        private DirectorRuntime _runtime;
        private int _queued;
        private bool _refusalLogged;
        private System.Func<string> _contractRefusal = UnityEngineContract.Refusal; // internal-by-convention: EditMode startup-refusal seam

        public int Port => port;

        private void Awake()
        {
            if (!Application.isEditor && !allowOutsideEditor)
            {
                Debug.LogWarning("[GameDirector] Bridge disabled outside the Editor (set allowOutsideEditor to override).");
                enabled = false;
                return;
            }
            if (!AcceptContract()) return;
            _runtime = gameObject.GetComponent<DirectorRuntime>() ?? gameObject.AddComponent<DirectorRuntime>();
        }

        private bool AcceptContract()
        {
            var why = _contractRefusal();
            if (why == null) return true;
            if (!_refusalLogged) {
                Debug.LogError("[GameDirector] Bridge refused: " + why);
                _refusalLogged = true;
            }
            enabled = false;
            return false;
        }

        private void OnEnable()
        {
            if (!enabled) return;
            if (!AcceptContract()) return;
            if (_runtime == null) { enabled = false; return; }
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add("http://" + BridgeDefaults.LoopbackHost + ":" + port + "/");
                _listener.Prefixes.Add("http://" + BridgeDefaults.LoopbackAlias + ":" + port + "/");
                _listener.Start();
                _running = true;
                _thread = new Thread(ListenLoop) { IsBackground = true, Name = "GameDirectorBridge" };
                _thread.Start();
                Debug.Log("[GameDirector] Bridge listening on http://" + BridgeDefaults.LoopbackHost + ":" + port + "/");
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
                    var stopped=Diagnose(new BridgeRequestException(BridgeErrors.Busy,"bridge stopped"));
                    job.Status=stopped.Status;job.Code=stopped.Code;job.Error=stopped.Body;job.Done.TrySetResult(false);
                }
            }
        }

        private void Update()
        {
            int budget = BridgeLimits.MainThreadJobsPerUpdate;
            while (budget-- > 0 && _jobs.TryDequeue(out var job))
            {
                Interlocked.Decrement(ref _queued);
                if (Interlocked.CompareExchange(ref job.State, 1, 0) != 0) continue;
                try { job.Result = job.Work(); }
                catch (System.Exception ex) {
                    var answer=Diagnose(ex);job.Status=answer.Status;job.Code=answer.Code;job.Error=answer.Body;
                    if(answer.Code==BridgeErrors.Internal.Code)Debug.LogException(ex);
                }
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
                var fetchSite = ctx.Request.Headers["Sec-Fetch-Site"];
                if (fetchSite != null && fetchSite != "none" && fetchSite != "same-origin") {
                    RespondError(ctx,BridgeErrors.Forbidden,"cross-site bridge requests are not allowed");return;
                }
                if (!string.IsNullOrEmpty(ctx.Request.Headers["Origin"])) {
                    RespondError(ctx,BridgeErrors.Forbidden,"browser-origin bridge requests are not allowed");return;
                }
                if (method == "POST" && ctx.Request.HasEntityBody && !(ctx.Request.ContentType??"").StartsWith("application/json", System.StringComparison.OrdinalIgnoreCase)) { // engine-local: missing HTTP header sentinel; wire policy is planned protocol work
                    RespondError(ctx,BridgeErrors.UnsupportedMediaType,"application/json required");return;
                }

                if (method == "GET" && path == "/" + BridgeRoutes.Health)
                {
                    Respond(ctx, 200, "application/json", "{\"ok\":true}");
                    return;
                }

                // Reject before reading a body or taking a queue slot. Fetch
                // Metadata blocks cross-site GETs even when Origin is absent.
                if (_runtime.IsCapturing && !TakeRoute(method, path)) {
                    RespondError(ctx,BridgeErrors.Busy,"bridge busy capturing a take");return;
                }
                byte[] body = null;
                if (method == "POST")
                {
                    using var ms = new System.IO.MemoryStream();
                    var buffer = new byte[8192]; int read;
                    while ((read = ctx.Request.InputStream.Read(buffer, 0, buffer.Length)) > 0) {
                        if (ms.Length + read > BridgeLimits.MaxRequestBytes) { RespondError(ctx,BridgeErrors.PayloadTooLarge,"request body too large");return; }
                        ms.Write(buffer,0,read);
                    }
                    body = ms.ToArray();
                }

                var job = new Job { Work = () => RouteOnMainThread(method, path, body) };
                if ((method == "GET" && path == "/" + BridgeRoutes.Capture) || (method == "POST" && path == "/" + BridgeRoutes.TakeFrame)) job.ContentType = "image/png";
                if (Interlocked.Increment(ref _queued) > BridgeLimits.MaxQueuedRequests || !_running) {
                    Interlocked.Decrement(ref _queued); RespondError(ctx,BridgeErrors.Busy,"bridge busy or stopping");return;
                }
                _jobs.Enqueue(job);
                if (!job.Done.Task.Wait(BridgeBudgets.EngineWaitSeconds * 1000)) { // engine-local: seconds-to-milliseconds conversion
                    Interlocked.CompareExchange(ref job.State,-1,0); // cancel queued work, never pretend executing work was rolled back
                    RespondError(ctx,BridgeErrors.Timeout,"main thread timeout");return;
                }
                if (job.Error != null) { Respond(ctx, job.Status, "application/json", job.Error); return; }
                // An advance-only frame intentionally has no PNG or body.
                Respond(ctx, job.Result == null ? 204 : 200, job.ContentType, job.Result);
            }
            catch (System.Exception ex)
            {
                var answer=Diagnose(ex);if(answer.Code==BridgeErrors.Internal.Code)Debug.LogException(ex);
                try { Respond(ctx,answer.Status,"application/json",answer.Body); } catch { }
            }
            finally
            {
                try { ctx.Response.OutputStream.Close(); } catch { }
            }
        }

        private byte[] RouteOnMainThread(string method, string path, byte[] body)
        {
            // Close the race with requests admitted just before start executed:
            // their queued work must not serialize/capture an active scene.
            if (_runtime.IsCapturing && !TakeRoute(method, path)) throw new BridgeRequestException(BridgeErrors.Busy,"bridge busy capturing a take");
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
                    return Json(_runtime.BeginTake(BridgeRequestException.Read<TakeRequest>(Encoding.UTF8.GetString(body ?? System.Array.Empty<byte>()))));
                case "POST /" + BridgeRoutes.TakeFrame:
                    return _runtime.NextFrame(BridgeRequestException.Read<FrameRequest>(Encoding.UTF8.GetString(body ?? System.Array.Empty<byte>())));
                case "POST /" + BridgeRoutes.TakeStop:
                    _runtime.StopTake(BridgeRequestException.Read<TakeStopRequest>(Encoding.UTF8.GetString(body ?? System.Array.Empty<byte>())));
                    return Json(new {ok=true});
                case "GET /" + BridgeRoutes.Take:
                    return Json(_runtime.GetTakeDto());
                case "GET /" + BridgeRoutes.Status:
                    return Json(_runtime.GetStatusDto());
                case "GET /" + BridgeRoutes.Capture:
                    return _runtime.CapturePng();
                default:
                    throw new BridgeRequestException(BridgeErrors.NotFound,"no route: " + method + " " + path);
            }
        }

        private static byte[] Json(object dto) => Encoding.UTF8.GetBytes(BridgeJson.Serialize(dto));
        // The one translation from a thrown failure to the wire answer: status,
        // code and the {error, code} body. Deliberate sites classify through
        // BridgeRequestException; anything else is an internal engine fault and
        // never a caller mistake. Pure so the job path is testable directly.
        internal sealed class Answer { public int Status; public string Code; public string Body; }
        internal static Answer Diagnose(System.Exception ex) {
            var error=BridgeRequestException.Classify(ex);
            return new Answer{Status=error.Status,Code=error.Code,Body=ErrorBody(ex.Message,error.Code)};
        }
        private static string ErrorBody(string message,string code) => JsonConvert.SerializeObject(new{error=message,code});
        private static void RespondError(HttpListenerContext ctx,BridgeErrorDefinition error,string message) =>
            Respond(ctx,error.Status,"application/json",ErrorBody(message,error.Code));

        private static void Respond(HttpListenerContext ctx, int status, string contentType, string text) =>
            Respond(ctx, status, contentType, Encoding.UTF8.GetBytes(text));

        private static void Respond(HttpListenerContext ctx, int status, string contentType, byte[] bytes)
        {
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = contentType;
            bytes ??= System.Array.Empty<byte>();
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }
    }
}
