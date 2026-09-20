using System;

namespace GameDirector.Unity
{
    public enum DirectorPresentationPhase { BeforeAnimation, AfterAnimation }

    /// <summary>Explicit opt-in for script-driven visuals. Unity callbacks stay
    /// disabled during capture. Implementations must work on inactive clones,
    /// avoid gameplay/network side effects, and use only the supplied clock.
    /// CaptureState must not mutate; its restore must also handle partial Prepare.</summary>
    public interface IDirectorPresentationParticipant
    {
        DirectorPresentationPhase Phase { get; }
        Action CaptureState();
        void PreparePresentation();
        void EvaluatePresentation(double deltaSeconds, double presentationTime);
    }
}
