using System;
using System.Collections.Generic;

namespace GameDirector.Core.Dsl
{
    public static class CapabilityKinds{public const string Enum="enum", NameList="name-list", Identifier="identifier", AbsolutePath="absolute-path", Token="token";}
    public static class CapabilityUndeclared{public const string Unrestricted="unrestricted", BuiltinTable="builtin-table", Absent="absent";}
    public static class CapabilityDeclarers{public const string Adapter="adapter", Engine="engine", AdapterOrEngine="adapter-or-engine";}
    public static class CapabilityIdentities{public const string Vocabulary="vocabulary", Volatile="volatile", Inspection="inspection";}
    public static class CapabilityExposures{public const string Public="public", MachineLocal="machine-local";}
    public static class CapabilityEffects{public const string ForbidCue="forbid-cue", RequireZero="require-zero", RestrictShotParams="restrict-shot-params";}
    public sealed class CapabilityValue
    {
        internal CapabilityValue(string value,string meaning){Value=value;Meaning=meaning;}
        public string Value{get;} public string Meaning{get;}
    }
    public sealed class CapabilityRule
    {
        internal CapabilityRule(string when,string effect,string cueType,string field,string remedy=null){When=when;Effect=effect;CueType=cueType;Field=field;Remedy=remedy;}
        public string When{get;} public string Effect{get;} public string CueType{get;} public string Field{get;} public string Remedy{get;}
    }
    public sealed class CapabilitySpec
    {
        internal CapabilitySpec(string key,string kind,string undeclared,string declarer,string identity,string exposure,string description,
            CapabilityValue[] values=null,string separator=null,string itemSyntax=null,params CapabilityRule[] rules)
        {Key=key;Kind=kind;Undeclared=undeclared;Declarer=declarer;Identity=identity;Exposure=exposure;Description=description;Values=values;Separator=separator;ItemSyntax=itemSyntax;Rules=rules;}
        public string Key{get;} public string Kind{get;} public IReadOnlyList<CapabilityValue> Values{get;} public string Separator{get;} public string ItemSyntax{get;}
        public string Undeclared{get;} public string Declarer{get;} public string Identity{get;} public string Exposure{get;} public string Description{get;}
        public IReadOnlyList<CapabilityRule> Rules{get;}
        /// <summary>The one typed-capability validator. Machine-local paths are
        /// nonblank here; rootedness/existence belong to the local output owner.</summary>
        public IEnumerable<string> Problems(string value)
        {
            switch(Kind) {
                case CapabilityKinds.Enum:
                    var allowed=new List<string>();
                    foreach(var v in Values){if(v.Value==value)yield break;allowed.Add("'"+v.Value+"'");}
                    yield return "capability '"+Key+"' must be "+string.Join(" or ",allowed)+", got '"+value+"'"; break;
                case CapabilityKinds.NameList:
                    if(value==null){yield return "capability '"+Key+"' must not be null";yield break;}
                    var seen=new HashSet<string>(StringComparer.Ordinal);
                    foreach(var name in Items(value)) if(name.Length==0||!seen.Add(name)||!CapabilitySpecs.IsName(name))
                        yield return "capability '"+Key+"' must be a comma-separated list of shot parameter names; bad entry '"+name+"'";
                    break;
                case CapabilityKinds.Identifier:
                    if(!CapabilitySpecs.IsName(value))yield return "capability '"+Key+"' must be an identifier ("+CapabilitySpecs.NameSyntax+"), got '"+value+"'"; break;
                case CapabilityKinds.AbsolutePath:
                case CapabilityKinds.Token:
                    if(string.IsNullOrWhiteSpace(value))yield return "capability '"+Key+"' must not be null or blank"; break;
            }
        }
        /// <summary>The one name-list parser; preserves bad entries for Problems.</summary>
        public IReadOnlyList<string> Items(string value)=>Kind!=CapabilityKinds.NameList||value==null?Array.Empty<string>():
            Array.ConvertAll(value.Split(new[]{Separator},StringSplitOptions.None),s=>s.Trim());
    }
    public static class CapabilitySpecs
    {
        // Order is validation, grammar and export order. Tables precede derived sets.
        private static readonly CapabilitySpec[] Table={
            new CapabilitySpec(CapabilityKeys.ActorSpawn,CapabilityKinds.Enum,CapabilityUndeclared.Unrestricted,CapabilityDeclarers.Adapter,CapabilityIdentities.Vocabulary,CapabilityExposures.Public,
                "Whether actor.spawn cues can execute on this stage.",new[]{new CapabilityValue(CapabilityKeys.SpawnSupported,"actor.spawn cues execute"),new CapabilityValue(CapabilityKeys.SpawnUnsupported,"every actor.spawn cue is refused at compile time, GD1012")},
                rules:new[]{new CapabilityRule(CapabilityKeys.SpawnUnsupported,CapabilityEffects.ForbidCue,CueTypes.ActorSpawn,"type","bind the role in the scene (presentAtStart) instead")}),
            new CapabilitySpec(CapabilityKeys.AnimationFade,CapabilityKinds.Enum,CapabilityUndeclared.Unrestricted,CapabilityDeclarers.Adapter,CapabilityIdentities.Vocabulary,CapabilityExposures.Public,
                "How actor.anim fades are performed.",new[]{new CapabilityValue(CapabilityKeys.FadeCrossfade,"fade > 0 crossfades over fade seconds"),new CapabilityValue(CapabilityKeys.FadeCutOnly,"actor.anim with fade > 0 is refused, GD1012")},
                rules:new[]{new CapabilityRule(CapabilityKeys.FadeCutOnly,CapabilityEffects.RequireZero,CueTypes.ActorAnim,"fade","set fade: 0")}),
            new CapabilitySpec(CapabilityKeys.CameraParams,CapabilityKinds.NameList,CapabilityUndeclared.BuiltinTable,CapabilityDeclarers.AdapterOrEngine,CapabilityIdentities.Vocabulary,CapabilityExposures.Public,
                "Complete set of shot param names the engine implements; built-in names keep their contract meaning, other names belong to custom shot types.",separator:",",itemSyntax:NameSyntax,
                rules:new[]{new CapabilityRule(null,CapabilityEffects.RestrictShotParams,CueTypes.CameraShot,"shot.params")}),
            new CapabilitySpec(CapabilityKeys.DirectorMode,CapabilityKinds.Enum,CapabilityUndeclared.Absent,CapabilityDeclarers.AdapterOrEngine,CapabilityIdentities.Vocabulary,CapabilityExposures.Public,
                "How the director may drive the game.",new[]{new CapabilityValue(DirectorModes.OfflineSandbox,"fully reversible, clocked presentation owned by the director")}),
            new CapabilitySpec(CapabilityKeys.ProjectSourceRoot,CapabilityKinds.AbsolutePath,CapabilityUndeclared.Absent,CapabilityDeclarers.Engine,CapabilityIdentities.Volatile,CapabilityExposures.MachineLocal,
                "Absolute path of the connected game source; capture output must be written outside it."),
            new CapabilitySpec(CapabilityKeys.PresentationSourceFingerprint,CapabilityKinds.Token,CapabilityUndeclared.Absent,CapabilityDeclarers.Engine,CapabilityIdentities.Volatile,CapabilityExposures.MachineLocal,
                "Live visual/source identity; a take is valid only against it."),
            new CapabilitySpec(CapabilityKeys.Engine,CapabilityKinds.Identifier,CapabilityUndeclared.Absent,CapabilityDeclarers.Engine,CapabilityIdentities.Vocabulary,CapabilityExposures.Public,
                "Engine family id stamped by the bridge (Three: three); informational.",itemSyntax:NameSyntax),
            new CapabilitySpec(CapabilityKeys.CaptureAudio,CapabilityKinds.Enum,CapabilityUndeclared.Absent,CapabilityDeclarers.AdapterOrEngine,CapabilityIdentities.Vocabulary,CapabilityExposures.Public,
                "Informational: TakeAdmission refuses audio cues regardless.",new[]{new CapabilityValue(CapabilityKeys.AudioPostProduction,"picture capture is silent; mix audio in the edit; see take.silentPictureAudio")}),
            new CapabilitySpec(CapabilityKeys.Measure,CapabilityKinds.Enum,CapabilityUndeclared.Absent,CapabilityDeclarers.AdapterOrEngine,CapabilityIdentities.Inspection,CapabilityExposures.Public,
                "A read-only inspection surface; never changes what a timeline means or how it renders, so catalogs and decision evidence stay valid; the take cache still keys on the full manifest.",new[]{new CapabilityValue(CapabilityKeys.MeasureV1,"per-frame measure rows on take frames flagged measure")})
        };
        public static IReadOnlyList<CapabilitySpec> All=>Table;
        public static CapabilitySpec Find(string key)=>key==null?null:Array.Find(Table,s=>s.Key==key);
        public static string Declared(CapabilityManifest m,string key)=>m?.Capabilities!=null&&m.Capabilities.TryGetValue(key,out var value)?value:null;
        public const string NameSyntax="a letter, then letters, digits or '_'";
        public static bool IsName(string s)
        {
            if(string.IsNullOrEmpty(s)||!char.IsLetter(s[0]))return false;
            for(int i=1;i<s.Length;i++)if(!char.IsLetterOrDigit(s[i])&&s[i]!='_')return false;
            return true;
        }
        public static double? NumericField(Cue c,string field)
        {
            switch(field){case "fade":return c.Fade;case "speed":return c.Speed;case "volume":return c.Volume;case "scale":return c.Scale;case "duration":return c.Duration;default:return null;}
        }
        public static readonly IReadOnlyList<string> NonIdentity=Select(s=>s.Identity!=CapabilityIdentities.Vocabulary);
        public static readonly IReadOnlyList<string> MachineLocal=Select(s=>s.Exposure==CapabilityExposures.MachineLocal);
        private static IReadOnlyList<string> Select(Func<CapabilitySpec,bool> predicate)
        {var keys=new List<string>();foreach(var s in Table)if(predicate(s))keys.Add(s.Key);return keys;}
    }
}
