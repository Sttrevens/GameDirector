using System;
using System.Collections.Generic;
using GameDirector.Core.Adapters;
using GameDirector.Core.Dsl;
using UnityEngine;

namespace GameDirector.Unity
{
    /// <summary>Owns a reversible offline performance. No game or wall clock leaks
    /// into actor motion or cinematography: the core advances both at cue boundaries.</summary>
    public abstract class GameDirectorAdapterBase : MonoBehaviour, IGameDirectorAdapter, IDirectorSessionAdapter
    {
        private CapabilityManifest _manifest;
        protected CinematicCameraRig Rig;
        private readonly Dictionary<string, Transform> _dynamicRoles = new Dictionary<string, Transform>();
        private readonly Dictionary<string, Move> _movers = new Dictionary<string, Move>();
        private readonly List<Action> _restore = new List<Action>();
        private readonly List<Animator> _animators = new List<Animator>();
        private readonly List<ParticleSystem> _particles = new List<ParticleSystem>();
        private bool _session;
        private float _scale = 1;
        private double _time;
        private double _presentationTime;
        private readonly List<PresentationMode.Lease> _leases = new List<PresentationMode.Lease>();
        private readonly Dictionary<Transform,PresentationMode.Lease> _actorLeases = new Dictionary<Transform,PresentationMode.Lease>();
        private sealed class Move { public Transform Target; public Vector3 To; public float Speed; public string Heading; }
        [Serializable] public sealed class AudioBinding { public string audioId; public AudioClip clip; }
        [SerializeField] protected List<AudioBinding> audioBindings = new List<AudioBinding>();
        private readonly Dictionary<string, AudioSource> _audio = new Dictionary<string, AudioSource>();
        public CapabilityManifest Manifest {
            get {
                if(_manifest==null){
                    _manifest=BuildManifest();
                    if(_manifest?.Audio!=null)_manifest.Audio.RemoveAll(a=>a==null || !audioBindings.Exists(b=>b.audioId==a.Id && b.clip!=null));
#if UNITY_EDITOR
                    // Authored vocabulary is a proposal. Advertise only states
                    // present in every actual controller serving that actor.
                    foreach(var role in _manifest.Roles) {
                        var descriptor=_manifest.FindActor(role.DefaultActor);
                        if(descriptor==null)continue;
                        // Ambiguous bindings cannot establish support/absence.
                        // Keep the proposal intact; Preflight names the stage
                        // conflict rather than blaming an apparently unknown clip.
                        try {descriptor.Clips=descriptor.Clips.FindAll(clip=>IsAnimationBound(role.Id,clip));}
                        catch(StageBindingException) { }
                    }
#endif
                }
#if UNITY_EDITOR
                if (!_session && _manifest != null) {
                    if (_manifest.Capabilities == null) _manifest.Capabilities = new Dictionary<string,string>();
                    _manifest.Capabilities[CapabilityKeys.PresentationSourceFingerprint] = SourceFingerprint();
                    _manifest.Capabilities[CapabilityKeys.ProjectSourceRoot] = System.IO.Path.GetDirectoryName(Application.dataPath);
                }
#endif
                return _manifest;
            }
        }
#if UNITY_EDITOR
        private string SourceFingerprint() {
            using (var hash=System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(BuildSourceIdentity()))).Replace("-","").ToLowerInvariant();
        }
        private string BuildSourceIdentity() {
            var text = new System.Text.StringBuilder(Application.unityVersion).Append('|').Append(gameObject.scene.path);
            if (!string.IsNullOrEmpty(gameObject.scene.path)) text.Append('|').Append(UnityEditor.AssetDatabase.GetAssetDependencyHash(gameObject.scene.path));
            var pipeline = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            if (pipeline != null) text.Append('|').Append(UnityEditor.AssetDatabase.GetAssetDependencyHash(UnityEditor.AssetDatabase.GetAssetPath(pipeline)));
            // Include live serialized visual inputs, not just saved asset hashes.
            // Unity instance references can conservatively reject a replay across
            // Editor restarts; they must never falsely accept changed live inputs.
            foreach (var root in gameObject.scene.GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<Transform>(true)) {
                    if (Rig!=null && Rig.OwnsObject(t)) continue; // only the camera actually created by this rig
                    text.Append('|').Append(t.name).Append(':').Append(t.GetSiblingIndex()).Append(':').Append(t.gameObject.activeSelf).Append(':').Append(t.gameObject.layer)
                        .Append(':').Append(t.parent!=null?t.parent.GetInstanceID():0).Append(t.localPosition.ToString("R"))
                        .Append(t.localRotation.ToString("R")).Append(t.localScale.ToString("R"));
                    foreach (var component in t.GetComponents<Component>()) if(component!=null && !(component is Transform)) {
                        text.Append('|').Append(component.GetType().FullName).Append(UnityEditor.EditorJsonUtility.ToJson(component));
                        if(component is Renderer renderer) foreach(var material in renderer.sharedMaterials)
                            if(material!=null) AppendMaterialIdentity(text,material);
                    }
                }
            text.Append('|').Append(RenderSettings.ambientMode).Append(RenderSettings.ambientLight.ToString("R"))
                .Append(RenderSettings.ambientSkyColor.ToString("R")).Append(RenderSettings.ambientEquatorColor.ToString("R"))
                .Append(RenderSettings.ambientGroundColor.ToString("R")).Append(RenderSettings.ambientIntensity.ToString("R",System.Globalization.CultureInfo.InvariantCulture))
                .Append(RenderSettings.fog).Append(RenderSettings.fogMode).Append(RenderSettings.fogColor.ToString("R"))
                .Append(RenderSettings.fogDensity.ToString("R",System.Globalization.CultureInfo.InvariantCulture))
                .Append(RenderSettings.fogStartDistance.ToString("R",System.Globalization.CultureInfo.InvariantCulture)).Append(RenderSettings.fogEndDistance.ToString("R",System.Globalization.CultureInfo.InvariantCulture))
                .Append(QualitySettings.GetQualityLevel());
            if(RenderSettings.skybox!=null)AppendMaterialIdentity(text,RenderSettings.skybox);
            // Runtime code is part of the picture's identity: a recompile keeps
            // every serialized input and instance id identical, so each module
            // version id below must change the fingerprint — the GameDirector
            // runtime assembly, the Core assembly and the concrete game adapter.
            text.Append('|').Append(typeof(GameDirectorAdapterBase).Assembly.ManifestModule.ModuleVersionId)
                .Append('|').Append(typeof(GameDirector.Core.Playback.TimelinePlayer).Assembly.ManifestModule.ModuleVersionId)
                .Append('|').Append(GetType().Assembly.ManifestModule.ModuleVersionId);
            AppendSourceIdentity(text);
            return text.ToString();
        }
        private static void AppendMaterialIdentity(System.Text.StringBuilder text, Material material) {
            // Unity may populate missing serialized shader defaults on first
            // render. Compare effective shader inputs, not this hydration cache.
            var serialized = Newtonsoft.Json.Linq.JObject.Parse(UnityEditor.EditorJsonUtility.ToJson(material));
            var data = serialized["Material"] as Newtonsoft.Json.Linq.JObject ?? serialized;
            data.Remove("m_SavedProperties");
            text.Append(serialized.ToString(Newtonsoft.Json.Formatting.None));
            var shader = material.shader;
            if (shader == null) return;
            text.Append('|').Append(shader.name);
            var shaderPath = UnityEditor.AssetDatabase.GetAssetPath(shader);
            if (!string.IsNullOrEmpty(shaderPath)) text.Append(UnityEditor.AssetDatabase.GetAssetDependencyHash(shaderPath));
            for (var i=0; i<shader.GetPropertyCount(); i++) {
                var id=shader.GetPropertyNameId(i); text.Append('|').Append(shader.GetPropertyName(i)).Append(':');
                switch (shader.GetPropertyType(i)) {
                    case UnityEngine.Rendering.ShaderPropertyType.Color: text.Append(material.GetColor(id).ToString("R")); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector: text.Append(material.GetVector(id).ToString("R")); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range: text.Append(material.GetFloat(id).ToString("R",System.Globalization.CultureInfo.InvariantCulture)); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Int: text.Append(material.GetInteger(id)); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        var texture=material.GetTexture(id);
                        if(texture!=null)text.Append(texture.GetInstanceID()).Append(':').Append(texture.imageContentsHash);
                        text.Append(material.GetTextureScale(id).ToString("R")).Append(material.GetTextureOffset(id).ToString("R")); break;
                }
            }
        }
#endif
        // Generated/unserialized inputs belong to the game adapter's identity.
        protected virtual void AppendSourceIdentity(System.Text.StringBuilder identity) { }
        protected virtual bool IsAnimationBound(string role,string clip) {
            var actor=ResolveRole(role);var root=actor!=null?actor.gameObject:SpawnPrefab(role);
            var animator=root==null?null:root.GetComponentInChildren<Animator>(true);
            if(animator==null || animator.runtimeAnimatorController==null)return false;
#if UNITY_EDITOR
            return HasAuthoredState(animator,clip);
#else
            return true; // Runtime state presence is checked by PlayAnimation.
#endif
        }
        protected abstract CapabilityManifest BuildManifest();
        protected virtual void Awake() { Rig = GetComponent<CinematicCameraRig>(); if(Rig==null)Rig=gameObject.AddComponent<CinematicCameraRig>(); }
        protected virtual void OnDisable() => EndSession();
        protected Transform ResolveRole(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (_dynamicRoles.TryGetValue(id, out var t) && t != null) return t;
            return DirectorRoleRegistry.Find(id, gameObject.scene);
        }
        // Execution-time binding failures are engine faults, not Preflight refusals.
        protected Transform RequireRole(string id) => ResolveRole(id) ?? throw new InvalidOperationException("unbound role: " + id);
        /// <summary>A manifest-valid request this stage cannot execute right now.
        /// Preflight runs AFTER admission: an undeclared mode, an unbound
        /// role/location/audio/animation binding, unowned stage clocks or an
        /// adapter stage refusal is a state conflict (409), never invalid input
        /// (400) and never an engine fault (500). Runtime execution failures
        /// after Preflight stay unclassified.</summary>
        protected static Exception RefuseStage(string message) => new BridgeRequestException(BridgeErrors.StateConflict,message);
        protected bool TryResolvePose(string id, out Vector3 pos, out float heading)
        {
            pos=default; heading=0;
            if (DirectorLocationRegistry.FindPose(id,out pos,out heading,gameObject.scene)) return true;
            var loc=Manifest?.FindLocation(id);
            if(loc?.Position==null || loc.Position.Length!=3)return false;
            pos=new Vector3(loc.Position[0],loc.Position[1],loc.Position[2]);heading=loc.HeadingDeg;return true;
        }
        public virtual void Preflight(TimelineAsset asset)
        {
            try { PreflightBindings(asset); }
            catch(StageBindingException ex) { throw RefuseStage(ex.Message); }
        }
        private void PreflightBindings(TimelineAsset asset)
        {
            // Every refusal here is RefuseStage: the request already passed
            // parse/compile/admission, so it is not the caller's input that is
            // invalid — this stage cannot execute it right now.
            if(Manifest.Capabilities==null || !Manifest.Capabilities.TryGetValue(CapabilityKeys.DirectorMode,out var mode) || mode!=DirectorModes.OfflineSandbox)
                throw RefuseStage("adapter must explicitly declare offline-sandbox mode");
            foreach(var role in Manifest.Roles) if(role.PresentAtStart && ResolveRole(role.Id)==null)throw RefuseStage("unbound role: "+role.Id);
            AuditStageClock(); // refusal happens before a session mutates the stage
            foreach(var cue in asset.Cues) {
                if(!string.IsNullOrEmpty(cue.HeadingTo) && Manifest.FindLocation(cue.HeadingTo)!=null && !TryResolvePose(cue.HeadingTo,out _,out _))
                    throw RefuseStage("unbound heading location: "+cue.HeadingTo);
                if(cue.Type==CueTypes.CameraShot) {
                    if(cue.Shot.From!=ShotVocabulary.CurrentAnchor && !TryResolvePose(cue.Shot.From,out _,out _))throw RefuseStage("unbound camera from: "+cue.Shot.From);
                    if(cue.Shot.To!=null && cue.Shot.To!=ShotVocabulary.CurrentAnchor && !TryResolvePose(cue.Shot.To,out _,out _))throw RefuseStage("unbound camera to: "+cue.Shot.To);
                }
                if(cue.Type==CueTypes.ActorSpawn || cue.Type==CueTypes.ActorMove) {
                    var id=cue.Type==CueTypes.ActorSpawn?cue.Location:cue.To;
                    if(!TryResolvePose(id,out _,out _))throw RefuseStage("unbound location: "+id);
                }
                if(cue.Type==CueTypes.ActorAnim) {
                    if(!IsAnimationBound(cue.Role,cue.Clip))throw RefuseStage("missing animation binding: "+cue.Role+"/"+cue.Clip);
                }
                if(cue.Type==CueTypes.ActorSpawn && !CanSpawn(cue.Role)) throw RefuseStage("unbound spawn prefab: "+cue.Role);
                if(cue.Type==CueTypes.AudioPlay || cue.Type==CueTypes.AudioStop) {
                    if(audioBindings.Find(x=>x.audioId==cue.AudioId && x.clip!=null)==null)
                        throw RefuseStage("unbound audio: "+cue.AudioId);
                }
            }
        }
        /// <summary>Explicit adapter declaration of a clock it owns or has
        /// made static. Never exempt an autonomous wall/unscaled-time visual.</summary>
        protected virtual bool IsStageClockExempt(Component c) => false;
        // Optional modules are probed by type name/property, never assembly
        // references: the package does not require VFX or Video modules.
        private static readonly (string Kind,Func<Component,bool> Active)[] ClockProbes = {
            (typeof(Animator).FullName,c=>((Animator)c).enabled),
            (typeof(ParticleSystem).FullName,c=>{var p=(ParticleSystem)c;return p.isPlaying || p.main.playOnAwake;}),
            (typeof(UnityEngine.Playables.PlayableDirector).FullName,c=>{var d=(UnityEngine.Playables.PlayableDirector)c;return d.state==UnityEngine.Playables.PlayState.Playing || d.playOnAwake;}),
            (typeof(Animation).FullName,c=>{var a=(Animation)c;return a.enabled && (a.playAutomatically || a.isPlaying);}),
            ("UnityEngine.VFX.VisualEffect",c=>true), // conservative: VFX has no director sampling lease
            ("UnityEngine.Video.VideoPlayer",c=>c is Behaviour b && b.enabled && (ClockFlag(c,"playOnAwake") || ClockFlag(c,"isPlaying")))
        };
        private static bool ClockFlag(Component c,string property) => (bool)c.GetType().GetProperty(property).GetValue(c);
        // Kind names match through inheritance: a managed subtype of an
        // Animator/VideoPlayer would still run that clock (Unity itself only
        // attaches exact native kinds today; the walk keeps the audit honest).
        private static bool IsKind(Component c,string kind) {
            for(var t=c.GetType();t!=null;t=t.BaseType)if(t.FullName==kind)return true;
            return false;
        }
        /// <summary>Extend the table with adapter-owned/custom autonomous clocks
        /// (e.g. unscaled-time scripts); call base to keep native probes.</summary>
        protected virtual bool IsAutonomousClock(Component c) => Array.Exists(ClockProbes,p=>IsKind(c,p.Kind) && p.Active(c));
        private List<Transform> StagePresentationRoots() {
            var roots=new List<Transform>();
            foreach(var role in Manifest.Roles)if(role.PresentAtStart) {
                var t=RequireRole(role.Id);if(!roots.Contains(t))roots.Add(t);
            }
            // A participant owns its hierarchy, even outside the cast. These
            // roots are leased in BeginSession, not merely ignored by the audit.
            foreach(var root in gameObject.scene.GetRootGameObjects())
                foreach(var script in root.GetComponentsInChildren<MonoBehaviour>(true))
                    if(script is IDirectorPresentationParticipant && !roots.Contains(script.transform))roots.Add(script.transform);
            return roots;
        }
        private void AuditStageClock() {
            var owned=StagePresentationRoots();var clocks=new List<string>();
            foreach(var root in gameObject.scene.GetRootGameObjects())
                foreach(var c in root.GetComponentsInChildren<Component>(true)) {
                    if(c==null)continue;
                    if(!IsAutonomousClock(c) || (Rig!=null && Rig.OwnsObject(c.transform)) || IsStageClockExempt(c) ||
                        owned.Exists(t=>c.transform==t || c.transform.IsChildOf(t)))continue;
                    clocks.Add(HierarchyPath(c.transform)+" ("+c.GetType().Name+")");
                }
            if(clocks.Count==0)return;
            clocks.Sort(StringComparer.Ordinal);
            throw RefuseStage("stage has "+clocks.Count+" clocks the director does not own: "+string.Join(", ",clocks)+
                "; disable them in the stage, make them presentation participants, or exempt them in the adapter");
        }
        private static string HierarchyPath(Transform t) {
            var path=t.name;while(t.parent!=null){t=t.parent;path=t.name+"/"+path;}return path;
        }
        private static uint ParticleSeed(Transform t) {
            // Stable FNV-1a over the UTF-8 hierarchy path, never GetHashCode or
            // an instance id. Zero is reserved by Unity for automatic seeding.
            uint hash=2166136261;
            foreach(var b in System.Text.Encoding.UTF8.GetBytes(HierarchyPath(t)))hash=unchecked((hash^b)*16777619);
            return hash==0?1:hash;
        }
#if UNITY_EDITOR
        private static bool HasAuthoredState(Animator animator,string clip) {
            if(animator==null)return false;
            var controller=animator.runtimeAnimatorController;
            if(controller is AnimatorOverrideController replacement)controller=replacement.runtimeAnimatorController;
            var authored=controller as UnityEditor.Animations.AnimatorController;
            return authored!=null && authored.layers.Length>0 && HasState(authored.layers[0].stateMachine,clip,authored.layers[0].name);
        }
        private static bool HasState(UnityEditor.Animations.AnimatorStateMachine machine,string clip,string prefix) {
            foreach(var s in machine.states)if(s.state.name==clip || prefix+"."+s.state.name==clip)return true;
            foreach(var child in machine.stateMachines)if(HasState(child.stateMachine,clip,prefix+"."+child.stateMachine.name))return true;
            return false;
        }
#endif
        protected virtual GameObject SpawnPrefab(string role) => null;
        protected virtual bool CanSpawn(string role) => SpawnPrefab(role) != null;
        public virtual void BeginSession()
        {
            EndSession(); _session=true; _time=0; _presentationTime=0; _scale=1;
            _restore.Add(Rig.CaptureState());
            // Existing scene actors are borrowed, spawned actors are owned.
            var borrowed = new List<Transform>();
            foreach(var role in Manifest.Roles) if(role.PresentAtStart) {
                var t=RequireRole(role.Id);
                if(borrowed.Contains(t))continue;
                borrowed.Add(t);
                var parent=t.parent;var pos=t.localPosition;var rot=t.localRotation;var scale=t.localScale;var active=t.gameObject.activeSelf;
                // A prop may be a registered role below another borrowed actor.
                // Restore its local attachment, independently of parent restore order.
                _restore.Add(()=>{if(t!=null){t.SetParent(parent,false);t.localPosition=pos;t.localRotation=rot;t.localScale=scale;t.gameObject.SetActive(active);}});
            }
            // Snapshot every attachment before any Animator.Rebind. Then lease
            // each disjoint hierarchy once: a registered hand prop must not run
            // its particles or acquire native components a second time.
            var roots=StagePresentationRoots();
            foreach(var t in roots)
                if(!roots.Exists(other=>other!=t && t.IsChildOf(other)))OwnActor(t);
            foreach (var lease in _leases) lease.Prepare();
            EvaluateParticipants(DirectorPresentationPhase.BeforeAnimation, 0);
            EvaluateParticipants(DirectorPresentationPhase.AfterAnimation, 0);
        }
        private void OwnActor(Transform root, bool borrowed = true)
        {
            var lease=PresentationMode.Acquire(root.gameObject);_restore.Add(lease.Dispose);_leases.Add(lease);_actorLeases.Add(root,lease);
            foreach(var a in root.GetComponentsInChildren<Animator>(true)) {
                if(_animators.Contains(a))continue;
                var enabled=a.enabled;var speed=a.speed;var culling=a.cullingMode;var motion=a.applyRootMotion;var events=a.fireEvents;
                var states=new AnimatorStateInfo[a.layerCount];
                for(int layer=0;layer<states.Length;layer++)states[layer]=a.GetCurrentAnimatorStateInfo(layer);
                if(borrowed)_restore.Add(()=>{if(a==null)return;a.enabled=true;a.speed=speed;a.applyRootMotion=motion;a.cullingMode=culling;
                    for(int layer=0;layer<states.Length;layer++)if(states[layer].fullPathHash!=0)a.Play(states[layer].fullPathHash,layer,states[layer].normalizedTime);
                    a.Update(0);a.fireEvents=events;a.enabled=enabled;});
                a.cullingMode=AnimatorCullingMode.AlwaysAnimate;a.applyRootMotion=false;a.fireEvents=false;a.enabled=true;a.speed=1;
                a.Rebind();a.Update(0);a.speed=0;_animators.Add(a);
            }
            foreach(var p in root.GetComponentsInChildren<ParticleSystem>(true)) {
                if(_particles.Contains(p))continue;
                var seed=p.randomSeed;var automatic=p.useAutoRandomSeed;var playing=p.isPlaying;var paused=p.isPaused;var time=p.time;
                var state=p.GetPlaybackState();var particles=new ParticleSystem.Particle[p.particleCount];var count=p.GetParticles(particles);
                if(borrowed)_restore.Add(()=>{if(p!=null){p.Stop(false,ParticleSystemStopBehavior.StopEmittingAndClear);p.randomSeed=seed;p.useAutoRandomSeed=automatic;
                    p.SetPlaybackState(state);p.SetParticles(particles,count);p.time=time;if(playing)p.Play(false);else if(paused)p.Pause(false);}});
                p.Stop(false,ParticleSystemStopBehavior.StopEmittingAndClear);if(automatic)p.randomSeed=ParticleSeed(p.transform);
                p.useAutoRandomSeed=false;p.Simulate(0,false,true,false);p.Pause(false);_particles.Add(p);
            }
        }
        public virtual void AdvancePresentation(double deltaSeconds,double timelineTime)
        {
            if(!_session)return;
            float dt=(float)(deltaSeconds*_scale);_time=timelineTime;_presentationTime+=dt;
            EvaluateParticipants(DirectorPresentationPhase.BeforeAnimation, dt);
            foreach(var a in _animators)if(a!=null && a.gameObject.activeInHierarchy){a.speed=1;a.Update(dt);a.speed=0;}
            foreach(var p in _particles)if(p!=null)p.Simulate(dt,false,false,false);
            foreach(var key in new List<string>(_movers.Keys)) {
                var m=_movers[key];if(m.Target==null){_movers.Remove(key);continue;}
                m.Target.position=Vector3.MoveTowards(m.Target.position,m.To,m.Speed*dt);
                FaceTowards(m.Target,m.To);
                if((m.Target.position-m.To).sqrMagnitude<.00001f) { // engine-local: actor movement arrival epsilon, not camera aim
                    _movers.Remove(key);if(!string.IsNullOrEmpty(m.Heading))FaceRole(key,m.Heading);
                }
            }
            EvaluateParticipants(DirectorPresentationPhase.AfterAnimation, dt);
            Rig.Evaluate(timelineTime);
        }
        private void EvaluateParticipants(DirectorPresentationPhase phase, double dt) {
            foreach (var lease in _leases) lease.Evaluate(phase, dt, _presentationTime);
        }
        public virtual void EndSession()
        {
            if(!_session)return;_session=false;
            _movers.Clear(); Rig?.EndShot();
            foreach(var a in _audio.Values)if(a!=null){a.Stop();Destroy(a);} _audio.Clear();
            Exception failure=null;
            for(int i=_restore.Count-1;i>=0;i--){try{_restore[i]();}catch(Exception ex){failure=ex;}} _restore.Clear();
            _animators.Clear();_particles.Clear();_leases.Clear();_actorLeases.Clear();
            foreach(var t in _dynamicRoles.Values)if(t!=null){t.gameObject.SetActive(false);DestroyOwned(t.gameObject);} _dynamicRoles.Clear();
            _scale=1;
            if(failure!=null)throw new InvalidOperationException("session cleanup failed",failure);
        }
        public virtual void ApplyCameraShot(ShotSpec shot) => Rig.BeginShot(shot,ResolveRole,TryResolvePose,_time);
        public virtual void PlayAnimation(string role,string clip,float fadeSeconds)
        {
            var a=RequireRole(role).GetComponentInChildren<Animator>();
            if(a==null || !a.HasState(0,Animator.StringToHash(clip)))throw new InvalidOperationException("missing Animator state: "+role+"/"+clip);
            a.speed=1;
            if(fadeSeconds<=0)a.Play(clip,0,0);else a.CrossFadeInFixedTime(clip,fadeSeconds,0,0);
            a.Update(0);a.speed=0;
        }
        public virtual void MoveRole(string role,string to,float speed,string heading)
        {
            if(!TryResolvePose(to,out var pos,out _))throw new InvalidOperationException("unbound location: "+to);
            _movers[role]=new Move{Target=RequireRole(role),To=pos,Speed=speed,Heading=heading};
        }
        public virtual void FaceRole(string role,string target)
        {
            var other=ResolveRole(target); Vector3 p;
            if(other!=null)p=other.position;
            else if(!TryResolvePose(target,out p,out _))throw new InvalidOperationException("unbound facing target: "+target);
            FaceTowards(RequireRole(role),p);
        }
        public virtual void SpawnRole(string role,string location,string heading)
        {
            if(ResolveRole(role)!=null)throw new InvalidOperationException("role already exists: "+role);
            if(!TryResolvePose(location,out var pos,out var yaw))throw new InvalidOperationException("unbound location: "+location);
            var instance=SpawnRoleInstance(role,pos,Quaternion.Euler(0,yaw,0));
            if(instance==null)throw new InvalidOperationException("spawn failed: "+role);
            if(instance.gameObject.activeInHierarchy) {
                instance.gameObject.SetActive(false);DestroyOwned(instance.gameObject);
                throw new InvalidOperationException("spawn adapter must return an inactive presentation actor: "+role);
            }
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(instance.gameObject,gameObject.scene);
            _dynamicRoles.Add(role,instance);
            // Scripts were removed while inactive, so activation only wakes the
            // native presentation graph. Establish animation state immediately.
            instance.gameObject.SetActive(true);OwnActor(instance, false);
            var lease=_leases[_leases.Count-1];lease.Prepare();
            lease.Evaluate(DirectorPresentationPhase.BeforeAnimation,0,_presentationTime);
            lease.Evaluate(DirectorPresentationPhase.AfterAnimation,0,_presentationTime);
            if(!string.IsNullOrEmpty(heading))FaceRole(role,heading);
        }
        private static void DestroyOwned(GameObject actor) {
            if(Application.isPlaying) Destroy(actor); else DestroyImmediate(actor);
        }
        public virtual void DespawnRole(string role)
        {
            _movers.Remove(role);var t=RequireRole(role);
            t.gameObject.SetActive(false);
            if(_dynamicRoles.Remove(role)) {
                try {
                    if(_actorLeases.TryGetValue(t,out var lease)) { _leases.Remove(lease);_actorLeases.Remove(t);lease.Dispose(); }
                } finally { DestroyOwned(t.gameObject); }
            }
        }
        public virtual void SetTimeScale(float scale) => _scale=scale;
        public virtual void Marker(string label) { }
        public virtual void PlayAudio(string id,float volume)
        {
            var binding=audioBindings.Find(x=>x.audioId==id && x.clip!=null);
            if(binding==null)throw new InvalidOperationException("unbound audio: "+id);
            if(!_audio.TryGetValue(id,out var source) || source==null){source=gameObject.AddComponent<AudioSource>();_audio[id]=source;}
            source.clip=binding.clip;source.volume=volume;source.Play();
        }
        public virtual void StopAudio(string id){if(_audio.TryGetValue(id,out var source) && source!=null)source.Stop();}
        protected abstract Transform SpawnRoleInstance(string role,Vector3 position,Quaternion rotation);
        protected static void FaceTowards(Transform t,Vector3 p){var d=p-t.position;d.y=0;if(d.sqrMagnitude>.0001f)t.rotation=Quaternion.LookRotation(d);}
    }
}
