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
        /// <summary>A take with no frame request for this long is abandoned: the
        /// stage must return to interactive presentation on its own.</summary>
        private const double TakeIdleTimeoutSeconds = 60;
        [SerializeField] private GameDirectorAdapterBase adapter;
        private TimelinePlayer _player;
        private TakeReceipt _take;
        private byte[] _lastFrame;
        private float _previousTimeScale;
        private bool _frozen;
        private double _lastRequest;
        private readonly List<object> _reviews = new List<object>();
        public CapabilityManifest Manifest => adapter?.Manifest;
        public PlayerState? State => _player?.State;
        public double Time => _player?.Time??0;
        public double Duration => _player?.Duration??0;
        private CinematicCameraRig Rig => GetComponent<CinematicCameraRig>();
        private void Awake() {if(adapter==null)adapter=GetComponent<GameDirectorAdapterBase>();}
        private void Update()
        {
            if(_take?.State=="Capturing") {
                if(UnityEngine.Time.realtimeSinceStartupAsDouble-_lastRequest>TakeIdleTimeoutSeconds)Stop();
                return;
            }
            if(_player?.State==PlayerState.Playing)_player.Tick(UnityEngine.Time.unscaledDeltaTime);
        }
        private void OnDisable()=>Stop();
        private CompileResult Prepare(TimelineAsset asset)
        {
            var result=TimelineCompiler.Compile(asset,Manifest);
            if(result.HasErrors)throw new InvalidOperationException(string.Join("\n",result.Diagnostics));
            foreach(var d in result.Diagnostics)if(d.Code==TimelineCompiler.WRoleNotPresent)
                throw new InvalidOperationException("Unexecutable timeline: "+d);
            adapter.Preflight(asset);return result;
        }
        public object PlayFromJson(string json)
        {
            var asset=BridgeJson.Deserialize<TimelineAsset>(json);var result=Prepare(asset);
            Stop();_take=null;_player=new TimelinePlayer(result.Timeline,adapter);_player.Play();
            if(_player.State==PlayerState.Failed)throw new InvalidOperationException(_player.Failure);
            return new {ok=true,id=result.Timeline.Id,cues=result.Timeline.OrderedCues.Count,duration=result.Timeline.Duration,diagnostics=result.Diagnostics};
        }
        public TakeReceipt BeginTake(TakeRequest request)
        {
            if(_take?.State=="Capturing")throw new InvalidOperationException("a take is already capturing; stop it explicitly");
            if(request==null || CaptureContract.CheckGeometry(request.FrameRate,request.Width,request.Height)!=null)
                throw new ArgumentException("take requires "+CaptureContract.MinFrameRate+".."+CaptureContract.MaxFrameRate+" fps and even dimensions within "+CaptureContract.MinDimension+".."+CaptureContract.MaxWidth+" x "+CaptureContract.MinDimension+".."+CaptureContract.MaxHeight);
            if (!string.IsNullOrEmpty(request.SourceFingerprint) && (Manifest.Capabilities == null || !Manifest.Capabilities.TryGetValue(CapabilityKeys.PresentationSourceFingerprint,out var identity) || identity != request.SourceFingerprint))
                throw new InvalidOperationException("source fingerprint changed before take start");
            var result=Prepare(request.Timeline);
            if(!request.Timeline.Cues.Exists(x=>x.Type==CueTypes.CameraShot && x.T==0))throw new ArgumentException("a picture take needs a camera shot at t=0");
            if(result.Timeline.Duration<=0 || result.Timeline.Duration>CaptureContract.MaxTakeSeconds)throw new ArgumentException("take duration must be >0 and <="+(int)CaptureContract.MaxTakeSeconds+" seconds");
            if(request.Timeline.Cues.Exists(x=>x.Type==CueTypes.AudioPlay || x.Type==CueTypes.AudioStop))
                throw new InvalidOperationException("frame-stepped picture capture requires audio to be mixed in the edit; live audio cues cannot be recorded synchronously");
            Stop();_reviews.Clear();_lastFrame=null;
            _take=new TakeReceipt{TakeId=Guid.NewGuid().ToString("N"),TimelineId=result.Timeline.Id,State="Capturing",FrameRate=request.FrameRate,Width=request.Width,Height=request.Height,
                FrameCount=(int)Math.Ceiling(result.Timeline.Duration*request.FrameRate-1e-8)};
            _previousTimeScale=UnityEngine.Time.timeScale;_frozen=true;UnityEngine.Time.timeScale=0;
            _lastRequest=UnityEngine.Time.realtimeSinceStartupAsDouble;
            _player=new TimelinePlayer(result.Timeline,adapter);_player.Play();
            if(_player.State==PlayerState.Failed){FailTake(_player.Failure);throw new InvalidOperationException(_player.Failure);}
            return _take;
        }
        public byte[] NextFrame(FrameRequest request)
        {
            if(request==null || _take==null || request.TakeId!=_take.TakeId)throw new InvalidOperationException("take identity mismatch");
            _lastRequest=UnityEngine.Time.realtimeSinceStartupAsDouble;
            if(request.FrameIndex==_take.CapturedFrames-1 && _lastFrame!=null)return _lastFrame;
            if(_take.State!="Capturing" || request.FrameIndex!=_take.CapturedFrames)throw new InvalidOperationException("frame index mismatch; expected "+_take.CapturedFrames);
            try {
                Rig.DirectorCamera.aspect=(float)_take.Width/_take.Height;
                if(request.FrameIndex%_take.FrameRate==0)_reviews.Add(new{frame=request.FrameIndex,time=Time,composition=Rig.InspectFrame()});
                var bytes=FrameCapture.Capture(Rig.DirectorCamera,_take.Width,_take.Height);
                _player.Tick(1.0/_take.FrameRate);
                if(_player.State==PlayerState.Failed)throw new InvalidOperationException(_player.Failure);
                _lastFrame=bytes;_take.CapturedFrames++;
                if(_take.CapturedFrames==_take.FrameCount){
                    // Fractional duration rounding must still complete the core timeline.
                    if(_player.State==PlayerState.Playing)_player.Tick(Math.Max(0,Duration-Time));
                    if(_player.State!=PlayerState.Finished)throw new InvalidOperationException("timeline did not finish");
                    _take.State="Completed";Unfreeze();
                }
                return bytes;
            } catch(Exception ex){FailTake(ex.Message);throw;}
        }
        private void FailTake(string error){if(_take!=null){_take.State="Failed";_take.Error=error;}_player?.Stop();Unfreeze();}
        private void Unfreeze(){if(_frozen){UnityEngine.Time.timeScale=_previousTimeScale;_frozen=false;}}
        public void StopTake(string takeId){if(_take==null || _take.TakeId!=takeId)throw new InvalidOperationException("take identity mismatch");Stop();}
        public void Stop(){_player?.Stop();if(_take?.State=="Capturing")_take.State="Cancelled";Unfreeze();}
        public byte[] CapturePng()=>FrameCapture.Capture(Rig.DirectorCamera,FilmDefaults.Width,FilmDefaults.Height);
        public object GetTakeDto()=>new{take=_take,events=_player?.Events,reviews=_reviews};
        public object GetStatusDto()=>new{state=(_player?.State??PlayerState.Idle).ToString(),time=Time,duration=Duration,error=_player?.Failure,take=_take,recentEvents=_player?.Events};
    }
}
