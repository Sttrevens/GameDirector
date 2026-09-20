using UnityEngine;

namespace GameDirector.Unity
{
    /// <summary>
    /// Single-frame PNG capture from any camera. Used by /capture for the
    /// review loop and frame-indexed takes; the client verifies video encoding.
    /// </summary>
    public static class FrameCapture
    {
        public static byte[] Capture(Camera camera, int width, int height)
        {
            if (camera == null) throw new System.ArgumentNullException(nameof(camera));
            if(width<=0 || height<=0 || width>SystemInfo.maxTextureSize || height>SystemInfo.maxTextureSize)
                throw new System.ArgumentOutOfRangeException("Capture dimensions exceed this GPU");
            // Offscreen targets previously forced one sample even when the game
            // used antialiasing. Spatial supersampling is pipeline-independent:
            // it needs neither a global quality change nor temporal history.
            var scale=System.Math.Min(2,SystemInfo.maxTextureSize/System.Math.Max(width,height));
            var rt = RenderTexture.GetTemporary(width*scale, height*scale, 24, RenderTextureFormat.ARGB32);
            RenderTexture resolved = null;
            var prevTarget = camera.targetTexture;
            var prevActive = RenderTexture.active;
            var prevAspect = camera.aspect;
            Texture2D tex = null;
            try
            {
                camera.targetTexture = rt;
                camera.aspect = (float)width / height;
                camera.Render();
                if(scale>1) {
                    resolved=RenderTexture.GetTemporary(width,height,0,RenderTextureFormat.ARGB32);
                    rt.filterMode=FilterMode.Bilinear;
                    Graphics.Blit(rt,resolved);
                }
                RenderTexture.active = resolved!=null?resolved:rt;
                tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                var png = tex.EncodeToPNG();

                return png;
            }
            finally
            {
                if (tex != null) Object.Destroy(tex);
                camera.aspect = prevAspect;
                camera.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                if(resolved!=null)RenderTexture.ReleaseTemporary(resolved);
                RenderTexture.ReleaseTemporary(rt);
            }
        }
    }
}
