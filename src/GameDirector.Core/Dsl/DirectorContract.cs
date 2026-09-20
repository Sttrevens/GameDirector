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
    }

    /// <summary>Loopback networking defaults. Ports are conventions, not
    /// configuration: every surface accepts an override, but all surfaces must
    /// agree on the same defaults or first-connect breaks.</summary>
    public static class BridgeDefaults
    {
        public const string LoopbackHost = "127.0.0.1";
        public const int UnityPort = 39777;
        public const int ThreePort = 39778;
        public const int WorkbenchPort = 39800;
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
