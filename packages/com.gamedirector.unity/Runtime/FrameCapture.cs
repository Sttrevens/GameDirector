using UnityEngine;

namespace GameDirector.Unity
{
    /// <summary>
    /// Single-frame PNG capture from any camera. Used by /capture for the
    /// LLM review loop. For full-video takes, the CDREBIRTH adapter will drive
    /// AVPro Movie Capture (already present in that project) — see roadmap M2.
    /// </summary>
    public static class FrameCapture
    {
        public static byte[] Capture(Camera camera, int width, int height)
        {
            if (camera == null) throw new System.ArgumentNullException(nameof(camera));
            var rt = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            var prevTarget = camera.targetTexture;
            var prevActive = RenderTexture.active;
            try
            {
                camera.targetTexture = rt;
                camera.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                var png = tex.EncodeToPNG();
                Object.Destroy(tex);
                return png;
            }
            finally
            {
                camera.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
