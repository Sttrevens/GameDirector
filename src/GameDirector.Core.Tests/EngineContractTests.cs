using System.Reflection;
using System.Text.Json.Nodes;
using GameDirector.Client;
using GameDirector.Core.Capture;
using GameDirector.Core.Dsl;
using Xunit;

namespace GameDirector.Core.Tests;

public sealed class EngineContractTests
{
    private static JsonObject Graph() => JsonNode.Parse(DslJson.Serialize(EngineContract.Current))!.AsObject();
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "GameDirector.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("GameDirector.sln");
    }

    [Fact] public void StaticInitializationExportsAndTouchesEveryRow()
    {
        var graph = Graph(); Assert.Equal("gamedirector.engine-contract", graph["schema"]!.GetValue<string>());
        Assert.All(ShotVocabulary.EaseSpecs, e => Assert.Same(e, ShotVocabulary.FindEase(e.Id)));
        Assert.Equal(ShotVocabulary.EaseSpecs.Select(e => e.Id), ShotVocabulary.Eases);
        Assert.All(ShotVocabulary.Params, p => Assert.Same(p, ShotVocabulary.FindParam(p.Name)));
        Assert.All(CameraMotions.All, m => Assert.Same(m, CameraMotions.Find(m.Id)));
        Assert.All(CapabilitySpecs.All, s => { Assert.Same(s, CapabilitySpecs.Find(s.Key)); _ = s.Problems(null).ToArray(); _ = s.Items(null); });
        Assert.Equal(BridgeErrors.All.Count, EngineContract.Current.Bridge.Errors.Count);
        Assert.Equal(TakeStates.All, EngineContract.Current.Take.States.Values);
        Assert.Null(ShotVocabulary.FindEase(null)); Assert.Null(CameraMotions.Find(null)); Assert.Null(CapabilitySpecs.Find(null));
        Assert.Equal(new TakeRequest().CaptureFromFrame, EngineContract.Current.Take.RequestDefaults.CaptureFromFrame);
        Assert.Equal(new FrameRequest().AdvanceOnly, EngineContract.Current.Take.RequestDefaults.AdvanceOnly);
    }

    [Theory] [InlineData(1,0,true)] [InlineData(1,1,true)] [InlineData(1,2,true)] [InlineData(1,3,false)] [InlineData(0,0,false)] [InlineData(2,0,false)]
    public void VersionRefusesDifferentMajorOrTooOldCore(int major,int minor,bool accepted)
    {
        var version = EngineContract.Current.Version;
        Assert.Equal("1.2", version.ToString());
        Assert.Equal(accepted, version.Refusal(major,minor) == null);
        if (!accepted) Assert.Equal("engine implements engine contract " + major + "." + minor + " but GameDirector.Core provides 1.2; install matching GameDirector packages", version.Refusal(major,minor));
        var property = typeof(EngineContract).GetProperty(nameof(EngineContract.Version))!;
        Assert.False(property.GetMethod!.IsStatic);
        Assert.DoesNotContain(typeof(EngineContract).GetFields(), f => f.IsLiteral && f.Name.Contains("Version"));
        // A newer Core minor accepts an older engine, not the reverse.
        var newer = (ContractVersion)Activator.CreateInstance(typeof(ContractVersion), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[]{1,2}, null)!;
        Assert.Null(newer.Refusal(1,0)); Assert.Null(newer.Refusal(1,2)); Assert.NotNull(newer.Refusal(1,3)); Assert.NotNull(newer.Refusal(2,0));
    }

    [Fact] public void EveryEaseSampleEqualsIndependentFloatAndDoubleKernels()
    {
        Assert.Equal(new[]{"identity","quad-in","quad-out","smoothstep"}, ShotVocabulary.EaseSpecs.Select(e => e.Formula));
        foreach (var ease in ShotVocabulary.EaseSpecs) {
            Assert.Equal(new[]{0f,.25f,.5f,.75f,1f}, ease.Samples.Select(s => s.P));
            foreach (var sample in ease.Samples) {
                Assert.Equal(sample.E, EaseFloat(ease.Formula,sample.P));
                Assert.Equal((double)sample.E, EaseDouble(ease.Formula,sample.P));
            }
        }
    }
    private static float EaseFloat(string formula,float p) => formula switch {
        "identity" => p, "quad-in" => p*p, "quad-out" => 1-(1-p)*(1-p), "smoothstep" => p*p*(3-2*p), _ => throw new Exception(formula) };
    private static double EaseDouble(string formula,double p) => formula switch {
        "identity" => p, "quad-in" => p*p, "quad-out" => 1-(1-p)*(1-p), "smoothstep" => p*p*(3-2*p), _ => throw new Exception(formula) };

    [Fact] public void ImmutableFormulaIdsKeepTheirPinnedExpressions()
    {
        Assert.Equal(new[]{"p","p*p","1-(1-p)*(1-p)","p*p*(3-2*p)"},ShotVocabulary.EaseSpecs.Select(e=>e.Expression));
        Assert.Equal(new[]{"pos = from","pos = lerp(from, to, e)","pos = target(t) + rotateUp(from - target0, orbitDeg * e)","pos = target(t) + (from - target0)"},CameraMotions.All.Select(m=>m.Expression));
        var c=EngineContract.Current.Camera;
        Assert.Equal("clamp01-linear",c.Progress.Id);Assert.Equal("vertical-2atan",c.Lens.FocalToFov.Id);Assert.Equal("height-over-coverage",c.Rig.AutoFrameDistance.Id);
        Assert.Equal("p = durationSeconds > 0 ? clamp01((t - shotStart) / durationSeconds) : 0; e = ease(p); the camera runs on the unscaled timeline clock (world.timescale scales the world only)",c.Progress.Expression);
        Assert.Equal("fovDeg = degrees(2*atan(sensorSizeMm/(2*focalLengthMm))) (vertical)",c.Lens.FocalToFov.Expression);
        Assert.Equal("from:current and distance <= 0 only: max(minSubjectHeight, subjectHeight) / (2*tan(fov/2)*coverage) along (start - target), or autoFrameFallback when lengthSq < degenerateSqrMagnitude",c.Rig.AutoFrameDistance.Expression);
    }

    [Fact] public void OmittedOrbitSweepSamplesMatchTheDeclaredDefaultAndEase()
    {
        var samples=CameraMotions.Find("orbit-up").Samples.Where(s=>(s.Params==null||!s.Params.ContainsKey("orbitDeg"))&&s.Observable=="swept-deg").ToArray();
        Assert.NotEmpty(samples);
        foreach(var s in samples) {
            var formula=ShotVocabulary.FindEase(s.Ease).Formula;
            Assert.Equal(s.Expected,ShotVocabulary.DefaultOf("orbitDeg")*EaseFloat(formula,s.P));
            Assert.Equal((double)s.Expected,ShotVocabulary.DefaultOf("orbitDeg")*EaseDouble(formula,s.P));
        }
    }

    [Fact] public void EveryMotionSampleEqualsAnIndependentSceneEvaluation()
    {
        var scene = EngineContract.Current.Camera.Scene;
        foreach (var shot in ShotVocabulary.Shots) Assert.NotEmpty(CameraMotions.Find(shot.Motion).Samples);
        foreach (var motion in CameraMotions.All) {
            Assert.NotEmpty(motion.Samples); Assert.All(motion.Reads, name => Assert.NotNull(ShotVocabulary.FindParam(name)));
            foreach (var sample in motion.Samples) {
                var ease = ShotVocabulary.FindEase(sample.Ease); Assert.NotNull(ease);
                var e = EaseDouble(ease.Formula,sample.P);
                var from = scene.From.Select(v => (double)v).ToArray(); var to = scene.To.Select(v => (double)v).ToArray();
                var target = scene.SubjectCenter.Select(v => (double)v).ToArray(); var pos = (double[])from.Clone();
                var initialTarget = (double[])target.Clone();
                if (sample.StepSubject) for (int i=0;i<3;i++) target[i]+=scene.SubjectStep[i];
                switch (motion.Id) {
                    case "hold": break;
                    case "lerp": for (int i=0;i<3;i++) pos[i]+=((sample.AuthoredTo?to[i]:from[i])-from[i])*e; break;
                    case "orbit-up":
                        var degrees = sample.Params != null && sample.Params.TryGetValue("orbitDeg",out var d) ? d : ShotVocabulary.DefaultOf("orbitDeg");
                        var a = degrees*e*Math.PI/180; var x = from[0]-initialTarget[0]; var z = from[2]-initialTarget[2];
                        pos[0]=target[0]+x*Math.Cos(a)+z*Math.Sin(a); pos[1]=target[1]+from[1]-initialTarget[1]; pos[2]=target[2]-x*Math.Sin(a)+z*Math.Cos(a); break;
                    case "track-offset": for (int i=0;i<3;i++) pos[i]=target[i]+from[i]-initialTarget[i]; break;
                    default: throw new Exception(motion.Id);
                }
                var expected = sample.Observable switch {
                    "displacement" => Length(pos.Zip(from,(p,f)=>p-f)),
                    "fraction" => Length(pos.Zip(from,(p,f)=>p-f))/Length(to.Zip(from,(p,f)=>p-f)),
                    "radius" => Math.Sqrt(Math.Pow(pos[0]-target[0],2)+Math.Pow(pos[2]-target[2],2)),
                    "radius3d" => Length(pos.Zip(target,(p,t)=>p-t)),
                    "swept-deg" => Math.Acos(Math.Clamp(((from[0]-initialTarget[0])*(pos[0]-target[0])+(from[2]-initialTarget[2])*(pos[2]-target[2]))/
                        (Math.Sqrt(Math.Pow(from[0]-initialTarget[0],2)+Math.Pow(from[2]-initialTarget[2],2))*Math.Sqrt(Math.Pow(pos[0]-target[0],2)+Math.Pow(pos[2]-target[2],2))),-1,1))*180/Math.PI,
                    "side" => Math.Sign(pos[0]-from[0]),
                    "drift" => Length(pos.Select((p,i)=>(p-target[i])-(from[i]-initialTarget[i]))),
                    _ => throw new Exception(sample.Observable) };
                Assert.InRange(Math.Abs(expected-sample.Expected),0,sample.Tolerance);
                Assert.InRange(Math.Abs(MotionFloat(motion.Id,sample,scene,ease.Formula)-sample.Expected),0,sample.Tolerance);
                if (motion.Id == "lerp") {
                    Assert.Equal(sample.Expected, EaseFloat(ease.Formula,sample.P));
                    var pinned = ease.Samples.Single(s => s.P == sample.P); Assert.Equal(pinned.E,sample.Expected);
                }
            }
        }
    }
    private static double Length(IEnumerable<double> values) => Math.Sqrt(values.Sum(x=>x*x));
    private static float MotionFloat(string motion,MotionSample s,ConformanceScene scene,string formula)
    {
        System.Numerics.Vector3 V(IReadOnlyList<float> a)=>new(a[0],a[1],a[2]);
        var from=V(scene.From);var to=V(scene.To);var target0=V(scene.SubjectCenter);var target=target0+(s.StepSubject?V(scene.SubjectStep):System.Numerics.Vector3.Zero);
        var e=EaseFloat(formula,s.P);var pos=from;
        switch(motion) {
            case "hold":break;
            case "lerp":pos=from+((s.AuthoredTo?to:from)-from)*e;break;
            case "orbit-up":var deg=s.Params!=null&&s.Params.TryGetValue("orbitDeg",out var d)?d:ShotVocabulary.DefaultOf("orbitDeg");var angle=deg*e*MathF.PI/180;var delta=from-target0;
                pos=target+new System.Numerics.Vector3(delta.X*MathF.Cos(angle)+delta.Z*MathF.Sin(angle),delta.Y,-delta.X*MathF.Sin(angle)+delta.Z*MathF.Cos(angle));break;
            case "track-offset":pos=target+(from-target0);break;
            default:throw new Exception(motion);
        }
        var a=from-target0;var b=pos-target;a.Y=b.Y=0;
        return s.Observable switch {
            "displacement"=>(pos-from).Length(),"fraction"=>(pos-from).Length()/(to-from).Length(),"radius"=>b.Length(),"radius3d"=>(pos-target).Length(),
            "swept-deg"=>MathF.Acos(Math.Clamp(System.Numerics.Vector3.Dot(a,b)/(a.Length()*b.Length()),-1,1))*180/MathF.PI,
            "side"=>Math.Sign(pos.X-from.X),"drift"=>((pos-target)-(from-target0)).Length(),_=>throw new Exception(s.Observable)};
    }

    [Fact] public void AimLensAndAutoFrameSamplesRecomputeInDouble()
    {
        var camera = EngineContract.Current.Camera;
        foreach (var s in camera.Aim.Samples) {
            var pan=s.Pan*Math.PI/180;var tilt=s.Tilt*Math.PI/180;var roll=s.Roll*Math.PI/180;
            var x=-Math.Sin(pan);var y=Math.Sin(tilt)*Math.Cos(pan);var z=Math.Cos(tilt)*Math.Cos(pan);
            var dx=Math.Cos(roll)*x+Math.Sin(roll)*y;var dy=-Math.Sin(roll)*x+Math.Cos(roll)*y;var tangent=Math.Tan(s.Fov*Math.PI/360);
            Assert.InRange(Math.Abs((.5+.5*(dx/z)/(tangent*s.Aspect))-s.X),0,1e-6);
            Assert.InRange(Math.Abs((.5+.5*(dy/z)/tangent)-s.Y),0,1e-6);
        }
        foreach (var s in camera.Lens.Samples)
            Assert.InRange(Math.Abs(2*Math.Atan(camera.Lens.SensorSizeMm/(2*(double)s.FocalLengthMm))*180/Math.PI-s.Fov),0,1e-6);
        foreach (var s in camera.Rig.Samples) {
            var coverage = s.Coverage ?? ShotVocabulary.Coverage(s.Frame);
            var radius=Math.Max(camera.Rig.MinSubjectHeight,camera.Scene.SubjectSize[1])/(2*Math.Tan(camera.Lens.DefaultFov*Math.PI/360)*coverage);
            Assert.InRange(Math.Abs(radius-s.Radius3D),0,1e-6);
        }
    }

    [Fact] public void CadenceCountsOnlyRenderedTimelineFramesNotPictureIndices()
    {
        foreach (var s in ReviewPolicy.Samples) {
            var actual=Enumerable.Range(s.CaptureFromFrame,s.FrameCount-s.CaptureFromFrame).Where(i=>ReviewPolicy.IsReviewFrame(i,s.Fps));
            Assert.Equal(s.Reviewed,actual);
        }
        Assert.Equal(1,ReviewPolicy.IntervalSeconds); Assert.Equal(30,BridgeDefaults.RequestTimeoutSeconds); Assert.Equal(100,BridgeDefaults.TakeStartTimeoutSeconds);
    }

    [Fact] public void DefaultsAreEitherLiteralOrNamedDerivationsAndFallbackIsExplicit()
    {
        foreach (var p in ShotVocabulary.Params) {
            Assert.NotEqual(p.Default.HasValue,p.Derivation!=null);
            if(p.Default.HasValue) Assert.Equal(p.Default.Value,ShotVocabulary.DefaultOf(p.Name));
            else { Assert.True(ShotVocabulary.Derivations.ContainsKey(p.Derivation)); Assert.Throws<ArgumentException>(()=>ShotVocabulary.DefaultOf(p.Name)); }
        }
        Assert.Throws<ArgumentException>(()=>ShotVocabulary.DefaultOf("unknown")); Assert.Throws<ArgumentException>(()=>ShotVocabulary.DefaultOf(null));
        Assert.NotNull(ShotVocabulary.FindFrame(ShotVocabulary.FallbackFrameId)); Assert.Equal(.4,ShotVocabulary.Coverage("x"));
        Assert.False(ShotVocabulary.ValidEase(null));
    }

    [Fact] public void SubjectBoundsDerivationPreservesTheFullVectorUnlessHeightIsAuthored()
    {
        var text=EngineContract.Current.Camera.Derivations[ParamDerivations.SubjectBoundsCenter];
        Assert.Contains("omitted",text);Assert.Contains("full vector (subject bounds center - subject pivot)",text);
        Assert.Contains("preserving X, Y and Z",text);Assert.Contains("authored",text);Assert.Contains("(0,targetHeight,0)",text);
        var meaning=ShotVocabulary.FindParam("targetHeight").Meaning;
        Assert.Contains("full vector",meaning);Assert.Contains("(0,targetHeight,0)",meaning);
        var note=Graph()["camera"]!["scene"]!["targetHeightNote"]?.GetValue<string>();Assert.NotNull(note);
        Assert.Contains("off-center",note);Assert.Contains("(1,1,0.5)",note);Assert.Contains("(0,1,0)",note);Assert.Contains("must differ",note);
    }

    [Fact] public void CapabilitiesAreWellFormedAndDeriveTheirIdentityAndExposureSets()
    {
        Assert.Equal(new[]{"actor.spawn","actor.animation.fade","camera.params","director.mode","project.sourceRoot","presentation.sourceFingerprint","engine","capture.audio","measure"},CapabilitySpecs.All.Select(s=>s.Key));
        Assert.Equal(new[]{"measure","presentation.sourceFingerprint","project.sourceRoot"},CapabilityKeys.NonIdentity.Order(StringComparer.Ordinal));
        Assert.Equal(new[]{"presentation.sourceFingerprint","project.sourceRoot"},CapabilityKeys.MachineLocal.Order(StringComparer.Ordinal));
        foreach (var s in CapabilitySpecs.All) {
            Assert.Contains(s.Kind,new[]{"enum","name-list","identifier","absolute-path","token"});
            Assert.Contains(s.Declarer,new[]{"adapter","engine","adapter-or-engine"}); Assert.Contains(s.Identity,new[]{"vocabulary","volatile","inspection"}); Assert.Contains(s.Exposure,new[]{"public","machine-local"});
            Assert.Contains(s.Undeclared,new[]{"unrestricted","builtin-table","absent"}); Assert.False(string.IsNullOrWhiteSpace(s.Description));
            if(s.Kind=="enum") { Assert.NotEmpty(s.Values); Assert.Equal(s.Values.Count,s.Values.Select(v=>v.Value).Distinct().Count()); Assert.All(s.Values,v=>{Assert.False(string.IsNullOrWhiteSpace(v.Meaning));Assert.Empty(s.Problems(v.Value));}); }
            foreach(var rule in s.Rules) {
                Assert.Contains(rule.CueType,CueTypes.All); Assert.Contains(rule.Effect,new[]{"forbid-cue","require-zero","restrict-shot-params"});
                Assert.True(rule.When==null||s.Values.Any(v=>v.Value==rule.When));
                if(rule.Effect=="require-zero") Assert.NotNull(CapabilitySpecs.NumericField(new Cue(),rule.Field));
                else Assert.NotNull(typeof(Cue).GetProperty(rule.Field=="shot.params"?"Shot":char.ToUpperInvariant(rule.Field[0])+rule.Field[1..]));
            }
        }
        var cue=new Cue{Fade=.3f,Speed=4,Volume=.4f,Scale=2,Duration=7};
        foreach(var p in typeof(Cue).GetProperties().Where(p=>p.Name!=nameof(Cue.T)&&p.Name!=nameof(Cue.SourceIndex)&&(p.PropertyType==typeof(float)||p.PropertyType==typeof(double))))
            Assert.Equal(Convert.ToDouble(p.GetValue(cue)),CapabilitySpecs.NumericField(cue,char.ToLowerInvariant(p.Name[0])+p.Name[1..]));
        Assert.Null(CapabilitySpecs.NumericField(cue,"t")); Assert.Null(CapabilitySpecs.NumericField(cue,"unknown"));
    }

    [Theory] [InlineData("actor.spawn","maybe","capability 'actor.spawn' must be 'supported' or 'unsupported', got 'maybe'")]
    [InlineData("actor.animation.fade",null,"capability 'actor.animation.fade' must be 'crossfade' or 'cut-only', got ''")]
    [InlineData("camera.params",null,"capability 'camera.params' must not be null")]
    [InlineData("camera.params","tilt,tilt","capability 'camera.params' must be a comma-separated list of shot parameter names; bad entry 'tilt'")]
    [InlineData("camera.params","tilt,,pan","capability 'camera.params' must be a comma-separated list of shot parameter names; bad entry ''")]
    [InlineData("camera.params","9lives","capability 'camera.params' must be a comma-separated list of shot parameter names; bad entry '9lives'")]
    public void OriginalCapabilityProblemTextsArePreserved(string key,string? value,string expected) => Assert.Equal(new[]{expected},CapabilitySpecs.Find(key).Problems(value));

    [Fact] public void IdentifierAndNameListSyntaxComeFromOneDeclaration()
    {
        var field=typeof(CapabilitySpecs).GetField("NameSyntax");Assert.NotNull(field);Assert.True(field.IsLiteral);
        var syntax=(string)field.GetRawConstantValue()!;
        Assert.Equal("a letter, then letters, digits or '_'",syntax);
        foreach(var spec in CapabilitySpecs.All.Where(s=>s.Kind is CapabilityKinds.Identifier or CapabilityKinds.NameList)) {
            Assert.Equal(syntax,spec.ItemSyntax);
            if(spec.Kind==CapabilityKinds.Identifier)Assert.Equal("capability '"+spec.Key+"' must be an identifier ("+syntax+"), got '9bad'",Assert.Single(spec.Problems("9bad")));
        }
    }

    [Fact] public void ItemsIsTheOnlyTrimmedNameListSyntaxAndNewTypedValuesAreValidated()
    {
        var names=CapabilitySpecs.Find("camera.params"); Assert.Equal(new[]{"tilt","pan","custom_2"},names.Items("tilt, pan ,custom_2"));
        Assert.Empty(names.Items(null)); Assert.Equal(new[]{""},names.Items(""));
        Assert.True(CapabilitySpecs.IsName("名_2"));Assert.False(CapabilitySpecs.IsName("_a"));Assert.False(CapabilitySpecs.IsName("9a"));Assert.False(CapabilitySpecs.IsName(null));Assert.False(CapabilitySpecs.IsName(""));Assert.False(CapabilitySpecs.IsName("a-b"));
        foreach(var key in new[]{"project.sourceRoot","presentation.sourceFingerprint"}) {
            var spec=CapabilitySpecs.Find(key);Assert.NotEmpty(spec.Problems(null));Assert.NotEmpty(spec.Problems(" \t"));Assert.Empty(spec.Problems("relative-or-off-machine"));
        }
        Assert.Empty(CapabilitySpecs.Find("engine").Problems("three"));Assert.NotEmpty(CapabilitySpecs.Find("engine").Problems("three-js"));
        Assert.Empty(CapabilitySpecs.Find("capture.audio").Problems("post-production"));Assert.NotEmpty(CapabilitySpecs.Find("capture.audio").Problems("live"));
        Assert.Empty(CapabilitySpecs.Find("director.mode").Problems("offline-sandbox"));Assert.NotEmpty(CapabilitySpecs.Find("director.mode").Problems("live"));
        var m=TestAssets.Manifest();m.Capabilities["engine"]="three";Assert.Equal("three",CapabilitySpecs.Declared(m,"engine"));Assert.Null(CapabilitySpecs.Declared(m,"absent"));m.Capabilities=null;Assert.Null(CapabilitySpecs.Declared(m,"engine"));
    }

    [Fact] public void RepositoryAndSyntheticManifestExtensionKeysRemainUninterpreted()
    {
        var root=RepoRoot();var extensions=new SortedSet<string>(StringComparer.Ordinal);var count=0;
        foreach(var folder in new[]{"adapters","samples","packages"}) foreach(var file in Directory.EnumerateFiles(Path.Combine(root,folder),"*.json",SearchOption.AllDirectories)) {
            var json=JsonNode.Parse(File.ReadAllText(file)); if(json is not JsonObject o||!o.ContainsKey("manifestVersion"))continue;
            var manifest=DslJson.LoadAuthored<CapabilityManifest>(file);count++;
            foreach(var key in manifest.Capabilities.Keys) if(CapabilitySpecs.Find(key)==null)extensions.Add(key);
        }
        // Keep extension-key coverage without depending on a private adapter.
        var synthetic=TestAssets.Manifest();
        foreach(var key in new[]{"capture.frame","capture.video","fixture.networking"}) synthetic.Capabilities[key]="fixture";
        var roundtrip=DslJson.Deserialize<CapabilityManifest>(DslJson.Serialize(synthetic));count++;
        foreach(var key in roundtrip.Capabilities.Keys) if(CapabilitySpecs.Find(key)==null)extensions.Add(key);
        Assert.True(count>=3);Assert.Equal(new[]{"capture.frame","capture.video","fixture.networking"},extensions);
    }

    [Fact] public void EveryEngineFacingConstHasAnEqualExportedPath()
    {
        var paths=new Dictionary<string,string> {
            ["CaptureContract.MinFrameRate"]="capture.minFrameRate",["CaptureContract.MaxFrameRate"]="capture.maxFrameRate",["CaptureContract.MinDimension"]="capture.minDimension",["CaptureContract.MaxWidth"]="capture.maxWidth",["CaptureContract.MaxHeight"]="capture.maxHeight",["CaptureContract.MaxTakeSeconds"]="capture.maxTakeSeconds",["CaptureContract.IdleLeaseSeconds"]="capture.idleLeaseSeconds",
            ["FrameClock.AlignmentToleranceFrames"]="frameClock.alignmentToleranceFrames",["FrameClock.CueEpsilonSeconds"]="frameClock.cueEpsilonSeconds",
            ["CameraRig.NearClip"]="camera.rig.nearClip",["CameraRig.FarClip"]="camera.rig.farClip",["CameraRig.MinSubjectHeight"]="camera.rig.minSubjectHeight",["CameraRig.DegenerateSqrMagnitude"]="camera.rig.degenerateSqrMagnitude",["CameraRig.AimEpsilonSqrMagnitude"]="camera.rig.aimEpsilonSqrMagnitude",
            ["CameraRig.AutoFrameFallbackUp"]="camera.rig.autoFrameFallback.up",["CameraRig.DistanceFallbackUp"]="camera.rig.distanceFallback.up",["CameraRig.FallbackBack"]="camera.rig.autoFrameFallback.back",
            ["CameraRig.ShakeNoise"]="camera.shake.noise",["CameraRig.ShakeClock"]="camera.shake.clock",["CameraRig.ShakeFrequencyHz"]="camera.shake.frequencyHz",["CameraRig.ShakeRow"]="camera.shake.row",["CameraRig.ShakeCenter"]="camera.shake.center",["CameraRig.ShakeScale"]="camera.shake.scale",
            ["BridgeLimits.MaxRequestBytes"]="bridge.limits.maxRequestBytes",["BridgeLimits.MaxQueuedRequests"]="bridge.limits.maxQueuedRequests",["BridgeLimits.MainThreadJobsPerUpdate"]="bridge.limits.mainThreadJobsPerUpdate",
            ["BridgeBudgets.EngineWaitSeconds"]="bridge.budgets.engineWaitSeconds",["BridgeBudgets.DeliverySlackSeconds"]="bridge.budgets.deliverySlackSeconds",["BridgeBudgets.CompileSeconds"]="bridge.budgets.compileSeconds",["BridgeBudgets.PageLoadSeconds"]="bridge.budgets.pageLoadSeconds",["BridgeBudgets.StageReadySeconds"]="bridge.budgets.stageReadySeconds",["BridgeBudgets.StartSlackSeconds"]="bridge.budgets.startSlackSeconds",
            ["ReviewPolicy.IntervalSeconds"]="review.intervalSeconds",["ShotVocabulary.DefaultFov"]="camera.lens.defaultFov",["ShotVocabulary.SensorSizeMm"]="camera.lens.sensorSizeMm",["ShotVocabulary.MinFov"]="camera.lens.minFov",["ShotVocabulary.MaxFov"]="camera.lens.maxFov",["ShotVocabulary.CurrentAnchor"]="camera.currentAnchor",["ShotVocabulary.FallbackFrameId"]="camera.fallbackFrame" };
        var graph=Graph();var found=new HashSet<string>();
        foreach(var type in new[]{typeof(CaptureContract),typeof(FrameClock),typeof(CameraRig),typeof(BridgeLimits),typeof(BridgeBudgets),typeof(ReviewPolicy),typeof(ShotVocabulary)})
            foreach(var field in type.GetFields(BindingFlags.Public|BindingFlags.Static).Where(f=>f.IsLiteral)) {
                var key=type.Name+"."+field.Name;
                if(type==typeof(ShotVocabulary)&&field.Name is nameof(ShotVocabulary.DefaultShotId) or nameof(ShotVocabulary.DefaultFrameId) or nameof(ShotVocabulary.OmittedShotId) or nameof(ShotVocabulary.OmittedFrameId) or nameof(ShotVocabulary.OmittedEase))continue;
                Assert.True(paths.ContainsKey(key),"unmapped const: "+key);found.Add(key);JsonNode? node=graph;
                foreach(var part in paths[key].Split('.'))node=node![part];
                Assert.True(JsonNode.DeepEquals(JsonNode.Parse(DslJson.Serialize(field.GetRawConstantValue())),node),key);
            }
        Assert.Equal(paths.Keys.Order(),found.Order());
        Assert.Equal(CameraRig.FallbackBack,EngineContract.Current.Camera.Rig.DistanceFallback.Back);
    }
}
