using GameDirector.Core.Compilation;
using GameDirector.Core.Dsl;
using Xunit;

namespace GameDirector.Core.Tests;

public sealed class TimelineCompilerTests
{
    private static CompileResult Compile(TimelineAsset asset) => TimelineCompiler.Compile(asset, TestAssets.Manifest());

    [Fact]
    public void Valid_timeline_compiles_with_zero_errors()
    {
        var result = Compile(TestAssets.ValidTimeline());
        Assert.False(result.HasErrors);
        Assert.NotNull(result.Timeline);
        Assert.Equal("t_valid", result.Timeline!.Id);
    }

    [Fact]
    public void Compiled_cues_are_sorted_by_time_then_source_order()
    {
        var asset = TestAssets.ValidTimeline();
        asset.Cues.Reverse(); // authored order must not matter for timing
        var result = Compile(asset);
        var cues = result.Timeline!.OrderedCues;
        for (int i = 1; i < cues.Count; i++)
            Assert.True(cues[i].T >= cues[i - 1].T, $"cues out of order at {i}");
    }

    [Fact]
    public void Timescale_with_duration_injects_explicit_restore_cue()
    {
        var result = Compile(TestAssets.ValidTimeline());
        var injected = result.Timeline!.OrderedCues.Where(c => c.Injected).ToList();
        Assert.Single(injected);
        Assert.Equal(CueTypes.WorldTimeScale, injected[0].Type);
        Assert.Equal(1f, injected[0].Scale);
        Assert.Equal(5.0, injected[0].T, precision: 6); // t=4 + duration=1
    }

    [Fact]
    public void Unknown_role_is_an_error()
    {
        var asset = TestAssets.ValidTimeline();
        asset.Cues.Add(new Cue { T = 1, Type = CueTypes.ActorAnim, Role = "ghost", Clip = "Idle" });
        var result = Compile(asset);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == TimelineCompiler.EUnknownRole);
        Assert.Null(result.Timeline);
    }

    [Fact]
    public void Unknown_clip_is_an_error()
    {
        var asset = TestAssets.ValidTimeline();
        asset.Cues.Add(new Cue { T = 1, Type = CueTypes.ActorAnim, Role = "sentinel", Clip = "Moonwalk" });
        var result = Compile(asset);
        Assert.Contains(result.Diagnostics, d => d.Code == TimelineCompiler.EUnknownClip && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Unknown_location_is_an_error()
    {
        var asset = TestAssets.ValidTimeline();
        asset.Cues.Add(new Cue { T = 1, Type = CueTypes.ActorSpawn, Role = "sentinel", Location = "loc_nowhere" });
        var result = Compile(asset);
        Assert.Contains(result.Diagnostics, d => d.Code == TimelineCompiler.EUnknownLocation);
    }

    [Fact]
    public void Unknown_shot_type_is_an_error()
    {
        var asset = TestAssets.ValidTimeline();
        asset.Cues.Add(new Cue { T = 1, Type = CueTypes.CameraShot, Shot = new ShotSpec { Type = "helicopter", Subject = "hero", Frame = "wide" } });
        var result = Compile(asset);
        Assert.Contains(result.Diagnostics, d => d.Code == TimelineCompiler.EUnknownShotType);
    }

    [Fact]
    public void Negative_time_is_an_error()
    {
        var asset = TestAssets.ValidTimeline();
        asset.Cues.Add(new Cue { T = -0.5, Type = CueTypes.Marker, Label = "bad" });
        var result = Compile(asset);
        Assert.Contains(result.Diagnostics, d => d.Code == TimelineCompiler.EBadTime);
    }

    [Fact]
    public void Unknown_cue_type_is_an_error()
    {
        var asset = TestAssets.ValidTimeline();
        asset.Cues.Add(new Cue { T = 1, Type = "explosion.big" });
        var result = Compile(asset);
        Assert.Contains(result.Diagnostics, d => d.Code == TimelineCompiler.EUnknownCueType);
    }

    [Fact]
    public void Overlapping_shots_warn_but_still_compile()
    {
        var asset = TestAssets.ValidTimeline();
        asset.Cues.Add(TestAssets.Shot(0.5)); // overlaps the opening shot at t=0
        var result = Compile(asset);
        Assert.False(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == TimelineCompiler.WShotOverlap && d.Severity == DiagnosticSeverity.Warning);
    }

    [Theory] [InlineData(3.3, false)] [InlineData(3.3 - .0000005, true)] [InlineData(3.2, true)]
    public void ShotWindowsUseThePlayersSimultaneityTolerance(double next, bool overlap)
    {
        var asset = new TimelineAsset { Id = "boundary", Cues = new() { TestAssets.Shot(1.1, duration: 2.2), TestAssets.Shot(next) } };
        var result = Compile(asset); Assert.False(result.HasErrors);
        Assert.Equal(overlap, result.Diagnostics.Any(d => d.Code == TimelineCompiler.WShotOverlap));
    }

    [Fact]
    public void Anim_on_not_spawned_role_is_an_error()
    {
        var asset = TestAssets.ValidTimeline();
        asset.Cues.Clear();
        asset.Cues.Add(new Cue { T = 0, Type = CueTypes.ActorAnim, Role = "sentinel", Clip = "Idle" }); // PresentAtStart=false, no spawn
        var result = Compile(asset);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Diagnostics, d => d.Code == TimelineCompiler.ERoleNotPresent && d.Severity == DiagnosticSeverity.Error);
        Assert.Null(result.Timeline);
    }

    [Fact]
    public void Reserved_current_location_is_accepted_for_shot_from()
    {
        var asset = TestAssets.ValidTimeline();
        asset.Cues.Add(new Cue { T = 1, Type = CueTypes.CameraShot, Shot = new ShotSpec { Type = "lockoff", Subject = "hero", Frame = "closeup", From = "current" } });
        var result = Compile(asset);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == TimelineCompiler.EUnknownLocation);
    }

    public static IEnumerable<object[]> CueRules() => CapabilitySpecs.All.SelectMany(s=>s.Rules.Where(r=>r.Effect is CapabilityEffects.ForbidCue or CapabilityEffects.RequireZero)
        .Select(r=>new object[]{s.Key,r.When,r.CueType,r.Field,r.Remedy}));
    [Theory] [MemberData(nameof(CueRules))]
    public void DeclaredCueRulesRefuseOnlyTheirMatchingValue(string key,string value,string type,string field,string remedy)
    {
        var manifest=TestAssets.Manifest();var cue=new Cue{Type=type,Role=type==CueTypes.ActorSpawn?"sentinel":"hero",Location="loc_gate",Clip="Idle"};
        var asset=new TimelineAsset{Id="rules",Cues=new(){cue}};
        Assert.False(TimelineCompiler.Compile(asset,manifest).HasErrors);manifest.Capabilities[key]=value;
        var refusal=Assert.Single(TimelineCompiler.Compile(asset,manifest).Diagnostics);
        Assert.Equal(TimelineCompiler.EUnsupportedByAdapter,refusal.Code);Assert.Equal("cues[0]."+field,refusal.Path);
        Assert.Equal("this adapter declares "+key+": "+value+"; "+remedy,refusal.Message);
        if(type==CueTypes.ActorAnim){cue.Fade=0;Assert.False(TimelineCompiler.Compile(asset,manifest).HasErrors);cue.Fade=.2f;}
        foreach(var other in CapabilitySpecs.Find(key).Values.Where(v=>v.Value!=value)) {
            manifest.Capabilities[key]=other.Value;Assert.False(TimelineCompiler.Compile(asset,manifest).HasErrors);
        }
    }
    public static IEnumerable<object[]> ManifestValues()
    {
        foreach(var spec in CapabilitySpecs.All) {
            if(spec.Values!=null)foreach(var value in spec.Values)yield return new object[]{spec.Key,value.Value,true};
            else foreach(var value in spec.Kind==CapabilityKinds.NameList?new[]{"tilt, pan, turns_2"}:spec.Kind==CapabilityKinds.Identifier?new[]{"three","otherEngine_2"}:new[]{"off-machine-value"})yield return new object[]{spec.Key,value,true};
            yield return new object[]{spec.Key,null!,false};
            yield return new object[]{spec.Key,spec.Kind is CapabilityKinds.AbsolutePath or CapabilityKinds.Token?" \t":"9invalid",false};
            if(spec.Kind==CapabilityKinds.NameList)foreach(var value in new[]{"tilt,tilt","tilt,,pan","9lives",""})yield return new object[]{spec.Key,value,false};
        }
    }
    [Theory] [MemberData(nameof(ManifestValues))]
    public void EveryRegisteredCapabilityValidatesThroughItsRow(string key,string? value,bool accepted)
    {
        var manifest=TestAssets.Manifest();manifest.Capabilities[key]=value;
        var result=TimelineCompiler.ValidateManifest(manifest);Assert.Equal(!accepted,result.HasErrors);
        if(!accepted)Assert.All(result.Diagnostics,d=>{Assert.Equal(TimelineCompiler.EBadValue,d.Code);Assert.Equal(-1,d.CueIndex);Assert.Equal("manifest.capabilities."+key,d.Path);});
    }
    [Fact] public void NullCameraParamsDoesNotAbortLaterTypedRowsAndDiagnosticOrderStaysTableOrder()
    {
        var m=TestAssets.Manifest();m.Capabilities=new(){["capture.audio"]="live",["engine"]="bad-id",["presentation.sourceFingerprint"]="",["project.sourceRoot"]=" ",["director.mode"]="live",["camera.params"]=null!,["actor.animation.fade"]="bad",["actor.spawn"]="bad",["measure"]="bad"};
        var result=TimelineCompiler.ValidateManifest(m);Assert.Equal(9,result.Diagnostics.Count);
        Assert.Equal(CapabilitySpecs.All.Select(s=>"manifest.capabilities."+s.Key),result.Diagnostics.Select(d=>d.Path));
        Assert.Equal("capability 'actor.spawn' must be 'supported' or 'unsupported', got 'bad'",result.Diagnostics[0].Message);
        Assert.Equal("capability 'actor.animation.fade' must be 'crossfade' or 'cut-only', got 'bad'",result.Diagnostics[1].Message);
        Assert.Equal("capability 'camera.params' must not be null",result.Diagnostics[2].Message);
    }
    [Fact] public void ExtensionCapabilitiesStayAcceptedAndUninterpreted()
    {
        var m=TestAssets.Manifest();m.Capabilities["extension.custom"]=null!;Assert.False(TimelineCompiler.ValidateManifest(m).HasErrors);
    }
    [Fact] public void CueRuleRefusalsKeepTheOriginalDiagnosticOrder()
    {
        var m=TestAssets.Manifest();m.Capabilities["actor.spawn"]="unsupported";m.Capabilities["actor.animation.fade"]="cut-only";
        var asset=new TimelineAsset{Id="order",Cues=new(){new(){T=0,Type=CueTypes.ActorSpawn,Role="missing",Location="missing"},new(){T=1,Type=CueTypes.ActorAnim,Role="missing",Clip="Idle",Fade=.2f}}};
        var diagnostics=TimelineCompiler.Compile(asset,m).Diagnostics;
        Assert.Equal(new[]{"GD1012","GD1001","GD1003","GD1012","GD1001"},diagnostics.Select(d=>d.Code));
        Assert.Equal(new[]{"cues[0].type","cues[0].role","cues[0].location","cues[1].fade","cues[1].role"},diagnostics.Select(d=>d.Path));
        Assert.Equal("this adapter declares actor.spawn: unsupported; bind the role in the scene (presentAtStart) instead",diagnostics[0].Message);
        Assert.Equal("this adapter declares actor.animation.fade: cut-only; set fade: 0",diagnostics[3].Message);
        asset.Cues[0].T=-1;Assert.Equal(TimelineCompiler.EBadTime,TimelineCompiler.Compile(asset,m).Diagnostics[0].Code);
        Assert.DoesNotContain(TimelineCompiler.Compile(asset,m).Diagnostics,d=>d.CueIndex==0&&d.Code==TimelineCompiler.EUnsupportedByAdapter);
    }
    [Theory] [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)] [InlineData(-.1f)] [InlineData(0f)]
    public void RequireZeroNeverPrecedesOrDuplicatesAnInvalidNumericDiagnostic(float fade)
    {
        var m=TestAssets.Manifest();m.Capabilities["actor.animation.fade"]="cut-only";
        var asset=new TimelineAsset{Id="fade",Cues=new(){new(){T=0,Type=CueTypes.ActorAnim,Role="hero",Clip="Idle",Fade=fade}}};
        var diagnostics=TimelineCompiler.Compile(asset,m).Diagnostics;
        Assert.DoesNotContain(diagnostics,d=>d.Code==TimelineCompiler.EUnsupportedByAdapter);
        if(fade==0)Assert.Empty(diagnostics);else {var d=Assert.Single(diagnostics);Assert.Equal(TimelineCompiler.EBadValue,d.Code);Assert.Equal("cues[0].fade",d.Path);}
    }
    public static IEnumerable<object[]> BuiltinParams()=>ShotVocabulary.Params.Select(p=>new object[]{p.Name,p.Default??1f});
    [Theory] [MemberData(nameof(BuiltinParams))]
    public void EveryBuiltinParamIsAllowedWhenUndeclaredAndRestrictableWhenDeclared(string name,float value)
    {
        var m=TestAssets.Manifest();var asset=TestAssets.ValidTimeline();asset.Cues[0].Shot.Params=new(){[name]=value};
        Assert.False(TimelineCompiler.Compile(asset,m).HasErrors);m.Capabilities["camera.params"]=name+", custom_2";
        Assert.False(TimelineCompiler.Compile(asset,m).HasErrors);m.Capabilities["camera.params"]="custom_2";
        var d=Assert.Single(TimelineCompiler.Compile(asset,m).Diagnostics.Where(d=>d.Code==TimelineCompiler.EUnsupportedByAdapter));
        Assert.Equal("cues[0].shot.params."+name,d.Path);Assert.Equal("shot parameter '"+name+"' is not in this adapter's camera.params (custom_2)",d.Message);
    }
    [Theory] [InlineData(5f,true)] [InlineData(170f,true)] [InlineData(4.99f,false)] [InlineData(170.01f,false)]
    public void FovBoundsAreTheExportedLensBounds(float value,bool accepted)
    {
        var asset=TestAssets.ValidTimeline();asset.Cues[0].Shot.Fov=value;
        Assert.Equal(!accepted,Compile(asset).HasErrors);
        Assert.Equal(accepted,value>=EngineContract.Current.Camera.Lens.MinFov&&value<=EngineContract.Current.Camera.Lens.MaxFov);
    }

    [Fact]
    public void Shot_params_are_checked_against_the_builtin_table_by_default()
    {
        var asset = TestAssets.ValidTimeline();
        asset.Cues[0].Shot.Params = new Dictionary<string, float> { ["tiltDeg"] = 10 }; // typo for tilt
        var result = Compile(asset);
        Assert.Contains(result.Diagnostics, d => d.Code == TimelineCompiler.EUnsupportedByAdapter && d.Message.Contains("tiltDeg"));
        asset.Cues[0].Shot.Params = new Dictionary<string, float> { ["tilt"] = 10, ["endFov"] = 35, ["coverage"] = 1.2f };
        Assert.False(Compile(asset).HasErrors);
    }

    [Fact]
    public void Declared_camera_params_are_the_adapters_complete_set()
    {
        // A declared list is authoritative and may name custom params: a custom
        // shot type's own vocabulary compiles, while an undeclared built-in fails.
        var manifest = TestAssets.Manifest();
        manifest.Capabilities[CapabilityKeys.CameraParams] = "tilt, pan , orbitDeg, turns"; // whitespace tolerated
        manifest.ShotTypes.Add("spiral"); // registered by the game's CinematicCameraRig.Motions
        var asset = TestAssets.ValidTimeline();
        asset.Cues[0].Shot.Params = new Dictionary<string, float> { ["orbitDeg"] = 120 };
        Assert.False(TimelineCompiler.Compile(asset, manifest).HasErrors);
        asset.Cues[0].Shot.Type = "spiral";
        asset.Cues[0].Shot.Params = new Dictionary<string, float> { ["turns"] = 3 }; // custom param, declared
        Assert.False(TimelineCompiler.Compile(asset, manifest).HasErrors);
        asset.Cues[0].Shot.Params["shake"] = .4f; // built-in but not declared by this adapter
        var result = TimelineCompiler.Compile(asset, manifest);
        Assert.Contains(result.Diagnostics, d => d.Code == TimelineCompiler.EUnsupportedByAdapter && d.Message.Contains("shake") && d.Message.Contains("camera.params"));
    }

    [Fact]
    public void Written_shot_to_must_resolve_for_every_shot_type()
    {
        // shot.to is validated whenever written, not only on RequiresTarget types.
        var asset = TestAssets.ValidTimeline();
        asset.Cues[0].Shot.To = "loc_nowhere"; // lockoff: no destination required, but a written one must exist
        var result = Compile(asset);
        Assert.Contains(result.Diagnostics, d => d.Code == TimelineCompiler.EUnknownLocation && d.Message.Contains("shot.to"));
        asset.Cues[0].Shot.To = ShotVocabulary.CurrentAnchor;
        Assert.False(Compile(asset).HasErrors);
        asset.Cues[0].Shot.To = "loc_gate";
        Assert.False(Compile(asset).HasErrors);
    }

    [Fact]
    public void Diagnostics_name_the_authored_field_path()
    {
        // The most specific field wins: readers feed Path back to the author.
        var asset = TestAssets.ValidTimeline();
        asset.Cues[0].Shot.To = "loc_nowhere";
        var result = Compile(asset);
        var to = Assert.Single(result.Diagnostics, d => d.Code == TimelineCompiler.EUnknownLocation);
        Assert.Equal("cues[0].shot.to", to.Path);
        Assert.Contains("cues[0].shot.to", to.ToString());
        Assert.DoesNotContain("cue[0] ", to.ToString());

        asset = TestAssets.ValidTimeline();
        asset.Cues[5].Scale = 20; // world.timescale above the real ceiling
        var scale = Assert.Single(Compile(asset).Diagnostics, d => d.Code == TimelineCompiler.EBadValue);
        Assert.Equal("cues[5].scale", scale.Path);
        Assert.Contains("<= 10", scale.Message); // the rule is 0 < scale <= 10

        asset = TestAssets.ValidTimeline();
        asset.Cues.Add(new Cue { T = 1, Type = "explosion.big" });
        var type = Assert.Single(Compile(asset).Diagnostics, d => d.Code == TimelineCompiler.EUnknownCueType);
        Assert.Equal("cues[8].type", type.Path);

        var manifest = TestAssets.Manifest();
        manifest.Capabilities[CapabilityKeys.AnimationFade] = "cut-only";
        var fades = TimelineCompiler.Compile(TestAssets.ValidTimeline(), manifest).Diagnostics
            .Where(d => d.Code == TimelineCompiler.EUnsupportedByAdapter).ToList();
        Assert.Equal(2, fades.Count);
        Assert.Equal("cues[1].fade", fades[0].Path);
        Assert.Equal("cues[3].fade", fades[1].Path);

        manifest = TestAssets.Manifest();
        manifest.Capabilities[CapabilityKeys.CameraParams] = "tilt,tilt";
        var capability = Assert.Single(TimelineCompiler.Compile(TestAssets.ValidTimeline(), manifest).Diagnostics);
        Assert.Equal("manifest.capabilities.camera.params", capability.Path);

        asset = TestAssets.ValidTimeline();
        asset.Version = "9.9";
        var version = Assert.Single(Compile(asset).Diagnostics, d => d.Code == TimelineCompiler.EUnsupportedVersion);
        Assert.Equal("version", version.Path);
        Assert.Equal(-1, version.CueIndex);
    }

    [Fact]
    public void Missing_subject_is_a_single_error_at_the_subject_field()
    {
        var asset = TestAssets.ValidTimeline();
        asset.Cues[0].Shot.Subject = null;
        var result = Compile(asset);
        var subject = Assert.Single(result.Diagnostics);
        Assert.Equal(TimelineCompiler.EUnknownRole, subject.Code);
        Assert.Equal("cues[0].shot.subject", subject.Path);
    }

    [Fact]
    public void Omitted_shot_fields_keep_their_documented_defaults()
    {
        // The defaults an omitted field takes are named in ShotVocabulary —
        // distinct from the wider first-draft defaults generators write.
        var shot = new ShotSpec();
        Assert.Equal("lockoff", shot.Type);
        Assert.Equal("medium", shot.Frame);
        Assert.Equal("inOut", shot.Ease);
        Assert.Equal(ShotVocabulary.OmittedShotId, shot.Type);
        Assert.Equal(ShotVocabulary.OmittedFrameId, shot.Frame);
        Assert.Equal(ShotVocabulary.OmittedEase, shot.Ease);
        Assert.Equal("lockoff", ShotVocabulary.DefaultShotId);
        Assert.Equal("full", ShotVocabulary.DefaultFrameId);
    }
}
