using System;
using System.Collections.Generic;

namespace GameDirector.Core.Dsl
{
    /// <summary>
    /// The single source of truth for the GameDirector wire and policy contract.
    /// Every participant — Unity bridge, CLI, MCP, Workbench, web UI — derives
    /// endpoints, capability keys and validation bounds from these definitions
    /// instead of restating literals. Pure netstandard2.1: ships inside Unity.
    /// </summary>
    public static class CapabilityKeys
    {
        /// <summary>Adapter declares how the director may drive the game.</summary>
        public const string DirectorMode = "director.mode";
        /// <summary>Absolute path of the connected game source project.</summary>
        public const string ProjectSourceRoot = "project.sourceRoot";
        /// <summary>Live visual/source identity; a take is valid only against it.</summary>
        public const string PresentationSourceFingerprint = "presentation.sourceFingerprint";

        /// <summary>"supported" | "unsupported". Undeclared = unrestricted;
        /// "unsupported" makes every actor.spawn cue a compile error.</summary>
        public const string ActorSpawn = "actor.spawn";
        /// <summary>"crossfade" | "cut-only". Undeclared = unrestricted;
        /// "cut-only" makes every actor.anim with fade > 0 a compile error.</summary>
        public const string AnimationFade = "actor.animation.fade";
        /// <summary>Comma-separated shot parameter names this adapter implements.
        /// Declared = the complete, authoritative set, built-in or custom names alike
        /// (a custom shot type may bring its own params); undeclared = the built-in
        /// table (ShotVocabulary.Params) so typos still fail. An authored param
        /// outside the allowed set is a compile error.</summary>
        public const string CameraParams = "camera.params";
        public const string Engine = "engine";
        public const string CaptureAudio = "capture.audio";
        public const string AudioPostProduction = "post-production";
        public const string Measure = "measure";
        public const string MeasureV1 = "measure-v1";

        // Declared values for the keys above; compiler, grammar and adapters bind
        // to these constants instead of restating the literals.
        public const string SpawnSupported = "supported";
        public const string SpawnUnsupported = "unsupported";
        public const string FadeCrossfade = "crossfade";
        public const string FadeCutOnly = "cut-only";

        /// <summary>Capability keys excluded from manifest vocabulary identity
        /// (catalog stamps, decision evidence): session/machine-volatile values and
        /// read-only inspection surfaces that do not change what the game can perform. The full manifest,
        /// fingerprint included, still keys take caches — pixels depend on the live source.</summary>
        public static IReadOnlyList<string> NonIdentity => CapabilitySpecs.NonIdentity;
        public static IReadOnlyList<string> MachineLocal => CapabilitySpecs.MachineLocal;
    }

    public static class DirectorModes
    {
        /// <summary>Fully reversible, clocked presentation owned by the director.</summary>
        public const string OfflineSandbox = "offline-sandbox";
    }

    /// <summary>HTTP routes of the in-game bridge. Client and server both bind
    /// from these constants, so a route can never drift on one side only.</summary>
    public static class BridgeRoutes
    {
        public const string Health = "health";
        public const string Manifest = "manifest";
        public const string TimelinePlay = "timeline/play";
        public const string TimelineStop = "timeline/stop";
        public const string TakeStart = "take/start";
        public const string TakeFrame = "take/frame";
        public const string TakeStop = "take/stop";
        public const string Take = "take";
        public const string Status = "status";
        public const string Capture = "capture";
        public const string Contract = "contract";
        public const string Stage = "stage";
        public static readonly IReadOnlyDictionary<string,string> All = new Dictionary<string,string> {
            {"health",Health},{"manifest",Manifest},{"timelinePlay",TimelinePlay},{"timelineStop",TimelineStop},{"takeStart",TakeStart},{"takeFrame",TakeFrame},
            {"takeStop",TakeStop},{"take",Take},{"status",Status},{"capture",Capture},{"contract",Contract},{"stage",Stage}
        };
    }

    /// <summary>The immutable status/code pair of a deliberate bridge refusal.</summary>
    public sealed class BridgeErrorDefinition
    {
        public int Status { get; }
        public string Code { get; }
        internal string Key { get; }
        internal BridgeErrorDefinition(string key, int status, string code) { Key=key;Status=status;Code=code; }
    }
    /// <summary>An answering bridge deliberately diagnoses its own boundary.
    /// Unknown exceptions are Internal, never inferred from BCL exception types.
    /// Validity rule: 400 invalid_request marks parsing, compilation or Core
    /// admission failures — judged against the manifest. Everything Preflight
    /// refuses runs AFTER admission (undeclared mode, unbound role/location/
    /// audio/animation binding, unowned stage clocks, adapter stage refusals):
    /// a manifest-valid request the stage cannot execute right now is 409
    /// state_conflict. Runtime execution failures remain unclassified/Internal.</summary>
    public static class BridgeErrors
    {
        public static readonly BridgeErrorDefinition InvalidRequest = new BridgeErrorDefinition("invalidRequest",400,"invalid_request");
        public static readonly BridgeErrorDefinition StateConflict = new BridgeErrorDefinition("stateConflict",409,"state_conflict");
        public static readonly BridgeErrorDefinition Busy = new BridgeErrorDefinition("busy",503,"busy");
        public static readonly BridgeErrorDefinition Timeout = new BridgeErrorDefinition("timeout",504,"timeout");
        public static readonly BridgeErrorDefinition Internal = new BridgeErrorDefinition("internal",500,"internal");
        // Existing HTTP admission guards retain their specific status; these
        // are not take-state conflicts or transient reachability diagnoses.
        public static readonly BridgeErrorDefinition Forbidden = new BridgeErrorDefinition("forbidden",403,"forbidden");
        public static readonly BridgeErrorDefinition NotFound = new BridgeErrorDefinition("notFound",404,"not_found");
        public static readonly BridgeErrorDefinition MethodNotAllowed = new BridgeErrorDefinition("methodNotAllowed",405,"method_not_allowed");
        public static readonly BridgeErrorDefinition UnsupportedMediaType = new BridgeErrorDefinition("unsupportedMediaType",415,"unsupported_media_type");
        public static readonly BridgeErrorDefinition PayloadTooLarge = new BridgeErrorDefinition("payloadTooLarge",413,"payload_too_large");
        public static readonly IReadOnlyList<BridgeErrorDefinition> All = new[]{InvalidRequest,StateConflict,Busy,Timeout,Internal,Forbidden,NotFound,MethodNotAllowed,UnsupportedMediaType,PayloadTooLarge};
        public static readonly IReadOnlyDictionary<string,BridgeErrorDefinition> Protocol = BuildProtocol();
        private static IReadOnlyDictionary<string,BridgeErrorDefinition> BuildProtocol()
        {var map=new Dictionary<string,BridgeErrorDefinition>();foreach(var error in All)map.Add(error.Key,error);return map;}
    }

    public static class BridgeLimits
    {
        public const int MaxRequestBytes=1024*1024, MaxQueuedRequests=32, MainThreadJobsPerUpdate=4;
    }
    public static class BridgeBudgets
    {
        public const int EngineWaitSeconds=15, DeliverySlackSeconds=15, CompileSeconds=30, PageLoadSeconds=30, StageReadySeconds=30, StartSlackSeconds=10;
    }

    /// <summary>Loopback networking defaults. Ports are conventions, not
    /// configuration: every surface accepts an override, but all surfaces must
    /// agree on the same defaults or first-connect breaks.</summary>
    public static class BridgeDefaults
    {
        public const string LoopbackHost = "127.0.0.1";
        public const string LoopbackAlias = "localhost";
        public const int UnityPort = 39777;
        public const int ThreePort = 39778;
        public const int WorkbenchPort = 39800;
        // Unity's main-thread queue waits 15s; another 15s allows delivery of
        // its explicit 504/body rather than racing the engine's own deadline.
        public const int RequestTimeoutSeconds = BridgeBudgets.EngineWaitSeconds + BridgeBudgets.DeliverySlackSeconds;
        // Slowest start: Three gd compile (30s), page.goto (30s), gdReady
        // (30s), plus 10s for fingerprinting/stage setup and receipt delivery.
        // Start is a mutation, never automatically retried after this budget.
        public const int TakeStartTimeoutSeconds = BridgeBudgets.CompileSeconds + BridgeBudgets.PageLoadSeconds + BridgeBudgets.StageReadySeconds + BridgeBudgets.StartSlackSeconds;
        public const int FrameRetryCount = 3;
        public const int FrameRetryInitialDelayMs = 250;
        public const int FrameRetryMaxDelayMs = 1000;
        public static string LoopbackEndpoint(int port) => "http://" + LoopbackHost + ":" + port;
        public static readonly string UnityEndpoint = LoopbackEndpoint(UnityPort);
        public static readonly string ThreeEndpoint = LoopbackEndpoint(ThreePort);
        public static readonly string WorkbenchEndpoint = LoopbackEndpoint(WorkbenchPort);
    }

    /// <summary>Authored-film defaults shared by the document model, the web UI
    /// and the Unity Inspector, so a new film looks identical from every entry.</summary>
    public static class FilmDefaults
    {
        public const int FrameRate = 24;
        public const int Width = 1280;
        public const int Height = 720;
    }

    /// <summary>Capture/take geometry bounds. One definition; validated on the
    /// client (take recorder), the workbench (film compiler) and the engine
    /// (bridge take start), because each layer can be reached independently.</summary>
    public static class CaptureContract
    {
        public const int MinFrameRate = 1;
        public const int MaxFrameRate = 60;
        public const int MinDimension = 64;
        public const int MaxWidth = 3840;
        public const int MaxHeight = 2160;
        public const double MaxTakeSeconds = 600;
        /// <summary>A take with no frame request for this long is abandoned: the
        /// stage must return to interactive presentation on its own.</summary>
        public const int IdleLeaseSeconds = 60;

        /// <summary>Frames in a take; capture and editing share FrameClock's
        /// boundary tolerance and fractional-duration ceiling.</summary>
        public static int FrameCount(double duration, int fps) => FrameClock.FrameCount(duration,fps);

        /// <summary>Returns null when the geometry is legal, else the reason.</summary>
        public static string CheckGeometry(int frameRate, int width, int height)
        {
            if (frameRate < MinFrameRate || frameRate > MaxFrameRate)
                return "frame rate must be within " + MinFrameRate + ".." + MaxFrameRate;
            if (width < MinDimension || width > MaxWidth || height < MinDimension || height > MaxHeight)
                return "dimensions must be within " + MinDimension + ".." + MaxWidth + " x " + MinDimension + ".." + MaxHeight;
            if (width % 2 != 0 || height % 2 != 0)
                return "dimensions must be even";
            return null;
        }

        public static void RequireGeometry(int frameRate, int width, int height)
        {
            var error = CheckGeometry(frameRate, width, height);
            if (error != null) throw new ArgumentException(error);
        }
    }

    /// <summary>Authored-film size bounds. The workbench enforces them on save
    /// and on produce; the web UI mirrors them from /api/health for early UX.</summary>
    public static class FilmLimits
    {
        public const int MaxScenes = 32;
        public const int MaxShots = 120;
        public const int MaxPerformanceCues = 5000;
        public const int MaxAudioCues = 64;
        public const int MaxSubtitles = 300;
        public const int MaxSubtitleLength = 500;
        public const double MaxOutputSeconds = 600;
        /// <summary>Total REPLAYED seconds (sum of shot ends, including non-rendered pre-roll).</summary>
        public const double MaxCaptureSeconds = 3600;
        public const double MaxVolume = 2;
        /// <summary>Imported sound ceiling per file; base64 transport and request
        /// body limits derive from this number, never from a second literal.</summary>
        public const int MaxAudioImportBytes = 5 * 1024 * 1024;
        /// <summary>Largest film script accepted through the import UI.</summary>
        public const int MaxScriptBytes = 2 * 1024 * 1024;
    }

    /// <summary>Preview renditions derive from the authored size: half scale,
    /// even-rounded, never below the capture floor. No fixed preview literal.</summary>
    public static class PreviewPolicy
    {
        public static void Derive(int width, int height, out int previewWidth, out int previewHeight)
        {
            previewWidth = Even(Math.Max(CaptureContract.MinDimension, width / 2));
            previewHeight = Even(Math.Max(CaptureContract.MinDimension, height / 2));
        }
        private static int Even(int value) => value % 2 == 0 ? value : value + 1;
    }
}
