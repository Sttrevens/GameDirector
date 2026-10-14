using System.Collections.Generic;
using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;

namespace GameDirector.Core.Capture
{
    /// <summary>Verdict of <see cref="TakeAdmission.Admit"/>: independent engine
    /// manifest validation, then the caller's compilation and take-gate failures.</summary>
    public sealed class AdmissionResult
    {
        /// <summary>Engine verdict, separate from caller Errors/GateErrors.
        /// An invalid manifest prevents caller validation altogether.</summary>
        public CompileResult ManifestValidation;
        public CompileResult Compiled;
        /// <summary>Timeline frames the engine must replay; 0 when admission failed.</summary>
        public int FrameCount;
        public readonly List<string> Errors = new List<string>();
        /// <summary>The subset of <see cref="Errors"/> that is not a compiler
        /// diagnostic (geometry, opening shot, duration, audio): callers that
        /// rewrite compiler diagnostics into their own coordinates keep these as-is.</summary>
        public readonly List<string> GateErrors = new List<string>();
        public bool Ok => ManifestValidation != null && !ManifestValidation.HasErrors && Compiled != null && !Compiled.HasErrors && Errors.Count == 0;
    }

    /// <summary>
    /// The one admission decision: "can this timeline be shot on an engine with
    /// this manifest at this geometry?" Pure function of its inputs, computed in
    /// Core so the CLI, the recorder, the film pipeline and every engine answer
    /// identically BEFORE a frame is rendered. The engine adds only what only it
    /// can know (its live source fingerprint) and then executes.
    /// </summary>
    public static class TakeAdmission
    {
        public static AdmissionResult Admit(TimelineAsset timeline, CapabilityManifest manifest, int fps, int width, int height, int captureFromFrame = 0, bool explicitSize = true)
        {
            var result = new AdmissionResult { ManifestValidation = TimelineCompiler.ValidateManifest(manifest) };
            if (result.ManifestValidation.HasErrors) return result;
            var geometry = CaptureContract.CheckGeometry(fps, width, height);
            if (geometry != null) Gate(result, geometry);
            var compiled = TimelineCompiler.Compile(timeline, manifest, explicitSize && width > 0 && height > 0 ? (double?)width / height : null);
            result.Compiled = compiled;
            if (compiled.HasErrors)
            {
                foreach (var d in compiled.Diagnostics)
                    if (d.Severity == DiagnosticSeverity.Error) result.Errors.Add(d.ToString());
                return result;
            }
            var duration = compiled.Timeline.Duration;
            var durationError=DurationError(duration);
            if(durationError!=null)Gate(result,durationError);
            var frames = geometry == null && durationError == null ? FrameClock.FrameCount(duration, fps) : 0;
            if (captureFromFrame < 0 || frames > 0 && captureFromFrame >= frames)
                Gate(result, "CaptureFromFrame must be in [0, FrameCount)");
            var firstCaptureTime = fps > 0 && captureFromFrame >= 0 ? FrameClock.TimeOf(captureFromFrame, fps) : 0;
            var cues = compiled.Timeline.OrderedCues;
            bool openingShot = false, audio = false;
            for (int i = 0; i < cues.Count; i++)
            {
                if (cues[i].Type == CueTypes.CameraShot && cues[i].T <= firstCaptureTime + FrameClock.CueEpsilonSeconds) openingShot = true;
                if (cues[i].Type == CueTypes.AudioPlay || cues[i].Type == CueTypes.AudioStop) audio = true;
            }
            if (!openingShot) Gate(result, "a picture take needs a camera shot at or before the first captured frame time");
            if (audio)
                Gate(result, "frame-stepped picture capture requires audio to be mixed in the edit; live audio cues cannot be recorded synchronously");
            if (result.Errors.Count == 0)
                result.FrameCount = frames;
            return result;
        }

        /// <summary>Shared pre-FrameCount budget verdict; keeps invalid durations out of range parsing.</summary>
        public static string DurationError(double duration)=>duration<=0||duration>CaptureContract.MaxTakeSeconds?
            "take duration must be >0 and <="+(int)CaptureContract.MaxTakeSeconds+" seconds":null;
        private static void Gate(AdmissionResult result, string error)
        {
            result.Errors.Add(error);
            result.GateErrors.Add(error);
        }
    }
}
