using System;
using System.Collections.Generic;
using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;
using GameDirector.Core.Playback;
using GameDirector.Core.Capture;
using UnityEngine;

namespace GameDirector.Unity
{
    public sealed class DirectorRuntime : MonoBehaviour
    {
        [SerializeField] private GameDirectorAdapterBase adapter;
        private TimelinePlayer _player;
        private TakeReceipt _take;
        private byte[] _lastFrame;
        private int _nextFrame; // timeline index, including advance-only steps
        private float _previousTimeScale;
        private bool _frozen;
        // Published by the main thread; HTTP admission never touches Unity APIs
        // or reads a mutable receipt from another thread.
        private volatile bool _capturing;
        public bool IsCapturing => _capturing;
        private double _lastRequest;
        private readonly List<object> _reviews = new List<object>();
        public CapabilityManifest Manifest => adapter?.Manifest;
        public PlayerState? State => _player?.State;
        public double Time => _player?.Time??0; // engine-local: no player has no elapsed time
        public double Duration => _player?.Duration??0; // engine-local: no player has no timeline duration
        private CinematicCameraRig Rig => GetComponent<CinematicCameraRig>();
        private void Awake() {if(adapter==null)adapter=GetComponent<GameDirectorAdapterBase>();}
        private void Update()
        {
            if(_take?.State==TakeStates.Capturing) {
                if(UnityEngine.Time.realtimeSinceStartupAsDouble-_lastRequest>CaptureContract.IdleLeaseSeconds)Stop();
                return;
            }
            if(_player?.State==PlayerState.Playing)_player.Tick(UnityEngine.Time.unscaledDeltaTime);
        }
        private void OnDisable()=>Stop();
        private CompileResult Prepare(TimelineAsset asset)
        {
            var manifest=Manifest;
            var validation=TimelineCompiler.ValidateManifest(manifest);
            if(validation.HasErrors)throw new InvalidOperationException("invalid engine manifest: "+string.Join("\n",validation.Diagnostics));
            var result=TimelineCompiler.Compile(asset,manifest);
            if(result.HasErrors)throw new BridgeRequestException(BridgeErrors.InvalidRequest,string.Join("\n",result.Diagnostics));
            adapter.Preflight(asset);return result;
        }
        public object PlayFromJson(string json)
        {
            UnityEngineContract.Require();
            var asset=BridgeRequestException.Read<TimelineAsset>(json);var result=Prepare(asset);
            Stop();_take=null;_player=new TimelinePlayer(result.Timeline,adapter);_player.Play();
            if(_player.State==PlayerState.Failed)throw new InvalidOperationException(_player.Failure);
            return new {ok=true,id=result.Timeline.Id,cues=result.Timeline.OrderedCues.Count,duration=result.Timeline.Duration,diagnostics=result.Diagnostics};
        }
        public TakeReceipt BeginTake(TakeRequest request)
        {
            UnityEngineContract.Require();
            if(_take?.State==TakeStates.Capturing)throw new BridgeRequestException(BridgeErrors.StateConflict,"a take is already capturing; stop it explicitly");
            if(request==null)throw new BridgeRequestException(BridgeErrors.InvalidRequest,"take request is required");
            var admission=TakeAdmission.Admit(request.Timeline,Manifest,request.FrameRate,request.Width,request.Height,request.CaptureFromFrame);
            if(admission.ManifestValidation.HasErrors)throw new InvalidOperationException("invalid engine manifest: "+string.Join("\n",admission.ManifestValidation.Diagnostics));
            if(!admission.Ok)throw new BridgeRequestException(BridgeErrors.InvalidRequest,string.Join("\n",admission.Errors));
            adapter.Preflight(request.Timeline);
            // Audit clocks before identity: autonomous motion must name its
            // object, not be masked by a generic stale-fingerprint refusal.
            if (!string.IsNullOrEmpty(request.SourceFingerprint) && (Manifest.Capabilities == null || !Manifest.Capabilities.TryGetValue(CapabilityKeys.PresentationSourceFingerprint,out var identity) || identity != request.SourceFingerprint))
                throw new BridgeRequestException(BridgeErrors.StateConflict,"source fingerprint changed before take start");
            Stop();_reviews.Clear();_lastFrame=null;_nextFrame=0;
            _take=new TakeReceipt{TakeId=Guid.NewGuid().ToString("N"),TimelineId=admission.Compiled.Timeline.Id,State=TakeStates.Capturing,FrameRate=request.FrameRate,Width=request.Width,Height=request.Height,
                FrameCount=admission.FrameCount,CaptureFromFrame=request.CaptureFromFrame};
            _capturing=true;
            _previousTimeScale=UnityEngine.Time.timeScale;_frozen=true;UnityEngine.Time.timeScale=0;
            _lastRequest=UnityEngine.Time.realtimeSinceStartupAsDouble;
            _player=new TimelinePlayer(admission.Compiled.Timeline,adapter);_player.Play();
            if(_player.State==PlayerState.Failed){FailTake(_player.Failure);throw new InvalidOperationException(_player.Failure);}
            return _take;
        }
        public byte[] NextFrame(FrameRequest request)
        {
            if(request==null)throw new BridgeRequestException(BridgeErrors.InvalidRequest,"frame request is required");
            if(_take==null || request.TakeId!=_take.TakeId)throw new BridgeRequestException(BridgeErrors.StateConflict,"take identity mismatch");
            _lastRequest=UnityEngine.Time.realtimeSinceStartupAsDouble;
            if(request.FrameIndex<0 || request.FrameIndex>=_take.FrameCount || request.AdvanceOnly!=(request.FrameIndex<_take.CaptureFromFrame))throw new BridgeRequestException(BridgeErrors.StateConflict,"frame mode/index mismatch");
            if(_nextFrame>0 && request.FrameIndex==_nextFrame-1)return _lastFrame;
            if(_take.State!=TakeStates.Capturing || request.FrameIndex!=_nextFrame)throw new BridgeRequestException(BridgeErrors.StateConflict,"frame index mismatch; expected "+_nextFrame);
            try {
                byte[] bytes=null;
                if(!request.AdvanceOnly) {
                    Rig.DirectorCamera.aspect=(float)_take.Width/_take.Height;
                    if(ReviewPolicy.IsReviewFrame(request.FrameIndex,_take.FrameRate))_reviews.Add(new{frame=request.FrameIndex,time=Time,composition=Rig.InspectFrame()});
                    bytes=FrameCapture.Capture(Rig.DirectorCamera,_take.Width,_take.Height);
                }
                _player.AdvanceTo(FrameClock.TimeOf(request.FrameIndex+1,_take.FrameRate));
                if(_player.State==PlayerState.Failed)throw new InvalidOperationException(_player.Failure);
                _lastFrame=bytes;_nextFrame++;if(!request.AdvanceOnly)_take.CapturedFrames++;
                if(_nextFrame==_take.FrameCount){
                    // Fractional duration rounding must still complete the core timeline.
                    if(_player.State==PlayerState.Playing)_player.AdvanceTo(Duration);
                    if(_player.State!=PlayerState.Finished)throw new InvalidOperationException("timeline did not finish");
                    _take.State=TakeStates.Completed;Unfreeze();_capturing=false;
                }
                return bytes;
            } catch(Exception ex){FailTake(ex.Message);throw;}
        }
        private void FailTake(string error){if(_take!=null){_take.State=TakeStates.Failed;_take.Error=error;}_player?.Stop();Unfreeze();_capturing=false;}
        private void Unfreeze(){if(_frozen){UnityEngine.Time.timeScale=_previousTimeScale;_frozen=false;}}
        public void StopTake(TakeStopRequest request){if(request==null)throw new BridgeRequestException(BridgeErrors.InvalidRequest,"stop request is required");if(_take==null || _take.TakeId!=request.TakeId)throw new BridgeRequestException(BridgeErrors.StateConflict,"take identity mismatch");Stop();}
        public void Stop(){_player?.Stop();if(_take?.State==TakeStates.Capturing)_take.State=TakeStates.Cancelled;Unfreeze();_capturing=false;}
        public byte[] CapturePng()=>FrameCapture.Capture(Rig.DirectorCamera,FilmDefaults.Width,FilmDefaults.Height);
        public object GetTakeDto()=>new{take=_take,events=_player?.Events,reviews=_reviews};
        public object GetStatusDto()=>new{state=(_player?.State??PlayerState.Idle).ToString(),time=Time,duration=Duration,error=_player?.Failure,take=_take,recentEvents=_player?.Events};
    }
}
