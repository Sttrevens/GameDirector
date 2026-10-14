using System;
using System.Collections.Generic;
using System.Linq;
using GameDirector.Core.Dsl;
namespace GameDirector.Unity {
/// <summary>The Unity engine's coverage claim. Check is pure; the live refusal
/// is evaluated once on demand, never during type initialization.</summary>
public static class UnityEngineContract {
    public const int Major=1,MinMinor=0;
    public static readonly IReadOnlyList<string> ImplementedParams=Array.AsReadOnly(new[]{
        "targetHeight","coverage","distance","offsetX","offsetY","offsetZ","toOffsetX","toOffsetY","toOffsetZ","orbitDeg","shake","tilt","pan","roll","endFov"
    });
    private static readonly Lazy<string> CachedRefusal=new Lazy<string>(()=>Check(EngineContract.Current.Version,
        ShotVocabulary.Shots.Select(s=>s.Motion),ShotVocabulary.EaseSpecs.Select(e=>e.Formula),
        new[]{EngineContract.Current.Camera.Progress.Id,EngineContract.Current.Camera.Lens.FocalToFov.Id,EngineContract.Current.Camera.Rig.AutoFrameDistance.Id},
        ShotVocabulary.Params.Select(p=>p.Name),ImplementedParams));
    public static string Refusal()=>CachedRefusal.Value;
    public static string Check(ContractVersion version,IEnumerable<string> motionIds,IEnumerable<string> easeFormulaIds,IEnumerable<string> formulaIds,
        IEnumerable<string> paramNames,IEnumerable<string> implementedParams,int major=Major,int minMinor=MinMinor) {
        var why=version.Refusal(major,minMinor);if(why!=null)return why;
        foreach(var id in motionIds)if(!CinematicCameraRig.ImplementsMotion(id))return "unimplemented motion: "+id;
        foreach(var id in easeFormulaIds)if(!CinematicCameraRig.ImplementsEase(id))return "unimplemented ease formula: "+id;
        foreach(var id in formulaIds)if(id!=CameraFormulas.Progress&&id!=CameraFormulas.FocalToFov&&id!=CameraFormulas.AutoFrameDistance)return "unimplemented camera formula: "+id;
        var required=new HashSet<string>(paramNames,StringComparer.Ordinal);var implemented=new HashSet<string>(implementedParams,StringComparer.Ordinal);
        if(required.SetEquals(implemented))return null;
        return "shot params differ; missing: "+string.Join(", ",required.Except(implemented).OrderBy(p=>p,StringComparer.Ordinal))+
            "; extra: "+string.Join(", ",implemented.Except(required).OrderBy(p=>p,StringComparer.Ordinal));
    }
    public static void Require() {var why=Refusal();if(why!=null)throw new InvalidOperationException("[GameDirector] engine contract refused: "+why);}
}
}
