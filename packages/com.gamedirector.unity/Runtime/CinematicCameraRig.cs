using System;
using System.Collections.Generic;
using GameDirector.Core.Dsl;
using UnityEngine;

namespace GameDirector.Unity
{
    /// <summary>
    /// Interprets ShotSpec into concrete camera motion — the generic
    /// cinematography kernel any Unity game gets for free. Positions come from
    /// the adapter's resolvers (scene anchors first, manifest coordinates as
    /// fallback), never from kernel-side assumptions about the scene.
    ///
    /// Shot semantics v0.1:
    ///   lockoff  — fixed pose at From, framed on subject
    ///   dolly    — From -&gt; To while framed on subject
    ///   crane    — dolly variant (vertical emphasis is expressed by anchor heights)
    ///   orbit    — circle around subject by params.orbitDeg (default 90)
    ///   tracking — keep the From-offset and follow the subject
    /// Framing distances are preset per Frame type and refined by Fov when set.
    /// </summary>
    public sealed class CinematicCameraRig : MonoBehaviour
    {
        public delegate Transform RoleResolver(string roleId);
        public delegate bool PoseResolver(string locationId, out Vector3 pos, out float headingDeg);

        private Camera _camera;
        private ShotSpec _shot;
        private RoleResolver _roles;
        private PoseResolver _poses;
        private double _startedAt;
        private Vector3 _trackingOffset;
        private Vector3 _orbitAxisAnchor;
        private float _baseShake;

        public Camera DirectorCamera
        {
            get
            {
                if (_camera == null)
                {
                    var go = new GameObject("DirectorCamera");
                    _camera = go.AddComponent<Camera>();
                    _camera.fieldOfView = 50f;
                    _camera.nearClipPlane = 0.05f;
                    go.tag = "Untagged"; // never claim MainCamera — host game cameras keep that role
                }
                return _camera;
            }
        }

        public bool ShotActive => _shot != null;

        public void BeginShot(ShotSpec shot, RoleResolver roles, PoseResolver poses)
        {
            _shot = shot ?? throw new ArgumentNullException(nameof(shot));
            _roles = roles;
            _poses = poses;
            _startedAt = Time.unscaledTimeAsDouble;
            _baseShake = shot.Params != null && shot.Params.TryGetValue("shake", out var s) ? s : 0f;

            var cam = DirectorCamera.transform;
            if (TryPose(shot.From, out var fromPos, out _)) cam.position = fromPos;
            else if (shot.From != "current") Debug.LogWarning($"[GameDirector] shot.from '{shot.From}' unresolved; holding current camera pose");

            if (shot.Fov.HasValue) DirectorCamera.fieldOfView = shot.Fov.Value;
            ApplyFrameDistance();

            _trackingOffset = cam.position - SubjectPosition();
            _orbitAxisAnchor = cam.position;
            LookAtSubject();
        }

        private void LateUpdate()
        {
            if (_shot == null) return;
            double elapsed = Time.unscaledTimeAsDouble - _startedAt;
            float p = _shot.DurationSeconds > 0
                ? Mathf.Clamp01((float)(elapsed / _shot.DurationSeconds))
                : 0f;
            float e = Ease(p, _shot.Ease);
            var cam = DirectorCamera.transform;

            switch (_shot.Type)
            {
                case "dolly":
                case "crane":
                    if (TryPose(_shot.From, out var a, out _) && TryPose(_shot.To, out var b, out _))
                        cam.position = Vector3.Lerp(a, b, e);
                    break;
                case "orbit":
                    float orbitDeg = _shot.Params != null && _shot.Params.TryGetValue("orbitDeg", out var od) ? od : 90f;
                    var subject = SubjectPosition();
                    var rel = _orbitAxisAnchor - subject;
                    cam.position = subject + Quaternion.AngleAxis(orbitDeg * e, Vector3.up) * rel;
                    break;
                case "tracking":
                    cam.position = SubjectPosition() + _trackingOffset;
                    break;
                // lockoff: hold pose.
            }

            if (_baseShake > 0f)
            {
                float n = Mathf.PerlinNoise(Time.unscaledTime * 7.3f, 0.5f) - 0.5f;
                cam.position += Vector3.up * (n * 0.05f * _baseShake);
            }

            LookAtSubject();
        }

        public void EndShot() => _shot = null;

        // ---- framing ----

        private static readonly Dictionary<string, float> FrameDistance = new Dictionary<string, float>
        {
            { "extreme-closeup", 0.6f }, { "closeup", 1.4f }, { "medium", 3.0f },
            { "full", 5.0f }, { "wide", 10.0f }
        };

        /// <summary>When From is "current" or unresolved, place the camera at the framing distance from the subject.</summary>
        private void ApplyFrameDistance()
        {
            if (_shot == null) return;
            if (_shot.From != "current" && TryPose(_shot.From, out _, out _)) return; // explicit anchor wins
            float dist = FrameDistance.TryGetValue(_shot.Frame, out var d) ? d : 3f;
            var subject = SubjectPosition();
            var back = DirectorCamera.transform.position - subject;
            back.y = Mathf.Max(back.y, 0.3f);
            if (back.sqrMagnitude < 0.01f) back = new Vector3(0, 1.5f, -1f);
            DirectorCamera.transform.position = subject + back.normalized * dist;
        }

        private Vector3 SubjectPosition()
        {
            var t = _roles?.Invoke(_shot?.Subject);
            if (t == null) return DirectorCamera.transform.position + DirectorCamera.transform.forward * 5f;
            var r = t.GetComponentInChildren<Renderer>();
            return r != null ? r.bounds.center : t.position + Vector3.up * 1.2f;
        }

        private void LookAtSubject()
        {
            if (_shot == null) return;
            var target = SubjectPosition();
            if (!string.IsNullOrEmpty(_shot.LookAt))
            {
                var lt = _roles?.Invoke(_shot.LookAt);
                if (lt != null) target = lt.position + Vector3.up * 1.2f;
            }
            var cam = DirectorCamera.transform;
            var dir = target - cam.position;
            if (dir.sqrMagnitude > 0.0001f) cam.rotation = Quaternion.LookRotation(dir);
        }

        private bool TryPose(string locationId, out Vector3 pos, out float heading)
        {
            pos = default; heading = 0f;
            if (_poses == null || string.IsNullOrEmpty(locationId)) return false;
            return _poses(locationId, out pos, out heading);
        }

        private static float Ease(float p, string ease)
        {
            switch (ease)
            {
                case "linear": return p;
                case "in": return p * p;
                case "out": return 1f - (1f - p) * (1f - p);
                default: return p * p * (3f - 2f * p); // inOut smoothstep
            }
        }
    }
}
