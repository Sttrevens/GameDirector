using System.Collections.Generic;
using GameDirector.Core.Capture;

namespace GameDirector.Core.Dsl
{
    public sealed class ContractVersion
    {
        internal ContractVersion(int major,int minor){Major=major;Minor=minor;}
        public int Major{get;} public int Minor{get;}
        /// <summary>Null when an engine implementing (major,minor) may run.</summary>
        public string Refusal(int major,int minor)=>Major==major&&Minor>=minor?null:
            "engine implements engine contract "+major+"."+minor+" but GameDirector.Core provides "+this+"; install matching GameDirector packages";
        public override string ToString()=>Major+"."+Minor;
    }
    /// <summary>The versioned engine-facing graph. Sections project declarations;
    /// construction never reads another class's derived static state.</summary>
    public sealed class EngineContract
    {
        public const string SchemaId="gamedirector.engine-contract";
        public static readonly EngineContract Current=new EngineContract();
        private EngineContract(){}
        public string Schema=>SchemaId;
        public ContractVersion Version{get;}=new ContractVersion(1,2);
        public CaptureSpec Capture{get;}=new CaptureSpec();
        public FrameClockSpec FrameClock{get;}=new FrameClockSpec();
        public CameraSpec Camera{get;}=new CameraSpec();
        public ReviewSpec Review{get;}=new ReviewSpec();
        public MeasureSpec Measure{get;}=new MeasureSpec();
        public BridgeSpec Bridge{get;}=new BridgeSpec();
        public TakeSpec Take{get;}=new TakeSpec();
        public IReadOnlyList<CapabilitySpec> Capabilities=>CapabilitySpecs.All;
    }
    public sealed class FormulaSpec
    {
        internal FormulaSpec(string id,string expression){Id=id;Expression=expression;}
        public string Id{get;} public string Expression{get;}
    }
    public sealed class CaptureSpec
    {
        public int MinFrameRate=>CaptureContract.MinFrameRate; public int MaxFrameRate=>CaptureContract.MaxFrameRate;
        public int MinDimension=>CaptureContract.MinDimension; public int MaxWidth=>CaptureContract.MaxWidth; public int MaxHeight=>CaptureContract.MaxHeight;
        public double MaxTakeSeconds=>CaptureContract.MaxTakeSeconds; public int IdleLeaseSeconds=>CaptureContract.IdleLeaseSeconds;
    }
    public sealed class FrameClockSpec
    {
        public double AlignmentToleranceFrames=>Dsl.FrameClock.AlignmentToleranceFrames; public double CueEpsilonSeconds=>Dsl.FrameClock.CueEpsilonSeconds;
    }
    public sealed class ReviewSpec
    {
        public int IntervalSeconds=>ReviewPolicy.IntervalSeconds;
        public string Rule=>"a rendered (not advance-only) timeline frame i is reviewed iff i % (fps*intervalSeconds) == 0, counted from timeline frame 0";
        public IReadOnlyList<string> Required=>ReviewPolicy.Required; public IReadOnlyList<string> Optional=>ReviewPolicy.Optional;
        public IReadOnlyList<CadenceSample> Samples=>ReviewPolicy.Samples;
        public RoleReviewSpec Roles{get;}=new RoleReviewSpec();
    }
    public sealed class CadenceSample
    {
        internal CadenceSample(int fps,int frameCount,int captureFromFrame,params int[] reviewed){Fps=fps;FrameCount=frameCount;CaptureFromFrame=captureFromFrame;Reviewed=reviewed;}
        public int Fps{get;} public int FrameCount{get;} public int CaptureFromFrame{get;} public IReadOnlyList<int> Reviewed{get;}
    }
    public sealed class BridgeSpec
    {
        public string LoopbackHost=>BridgeDefaults.LoopbackHost; public string LoopbackAlias=>BridgeDefaults.LoopbackAlias;
        public BridgePortsSpec Ports{get;}=new BridgePortsSpec();
        public IReadOnlyDictionary<string,string> Routes=>BridgeRoutes.All;
        public BridgeLimitsSpec Limits{get;}=new BridgeLimitsSpec(); public BridgeBudgetsSpec Budgets{get;}=new BridgeBudgetsSpec();
        public IReadOnlyDictionary<string,BridgeErrorDefinition> Errors=>BridgeErrors.Protocol;
    }
    public sealed class BridgePortsSpec{public int Unity=>BridgeDefaults.UnityPort; public int Three=>BridgeDefaults.ThreePort;}
    public sealed class BridgeLimitsSpec
    {
        public int MaxRequestBytes=>BridgeLimits.MaxRequestBytes; public int MaxQueuedRequests=>BridgeLimits.MaxQueuedRequests;
        public int MainThreadJobsPerUpdate=>BridgeLimits.MainThreadJobsPerUpdate;
    }
    public sealed class BridgeBudgetsSpec
    {
        public int EngineWaitSeconds=>BridgeBudgets.EngineWaitSeconds; public int DeliverySlackSeconds=>BridgeBudgets.DeliverySlackSeconds;
        public int RequestTimeoutSeconds=>BridgeDefaults.RequestTimeoutSeconds; public int CompileSeconds=>BridgeBudgets.CompileSeconds;
        public int PageLoadSeconds=>BridgeBudgets.PageLoadSeconds; public int StageReadySeconds=>BridgeBudgets.StageReadySeconds;
        public int StartSlackSeconds=>BridgeBudgets.StartSlackSeconds; public int TakeStartTimeoutSeconds=>BridgeDefaults.TakeStartTimeoutSeconds;
    }
    public sealed class TakeSpec
    {
        public IReadOnlyDictionary<string,string> States=>TakeStates.Protocol;
        public string SilentPictureAudio=>TakeReceipt.SilentPictureAudio;
        public RequestDefaultsSpec RequestDefaults{get;}=new RequestDefaultsSpec();
    }
    public sealed class RequestDefaultsSpec
    {
        private static readonly TakeRequest T=new TakeRequest(); private static readonly FrameRequest F=new FrameRequest();
        public int CaptureFromFrame=>T.CaptureFromFrame; public bool AdvanceOnly=>F.AdvanceOnly; public bool Measure=>F.Measure;
    }
}
