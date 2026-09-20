namespace GameDirector.Core.Adapters
{
    /// <summary>Optional lifecycle for adapters with owned presentation state.
    /// Advance is split exactly at cue boundaries, in timeline seconds.</summary>
    public interface IDirectorSessionAdapter
    {
        void BeginSession();
        void AdvancePresentation(double deltaSeconds, double timelineTime);
        void EndSession();
    }
}
