Shader "GameDirector/LED Face"
{
    Properties
    {
        _FaceColor ("LED color", Color) = (0.55,0.82,1,1)
        _MouthOpen ("Mouth", Range(0,1)) = 0
        _Blink ("Blink", Range(0,1)) = 0
        _Mood ("Concern", Range(0,1)) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct A { float4 positionOS:POSITION; float2 uv:TEXCOORD0; };
            struct V { float4 positionCS:SV_POSITION; float2 uv:TEXCOORD0; };
            CBUFFER_START(UnityPerMaterial)
            float4 _FaceColor;
            float _MouthOpen, _Blink, _Mood;
            CBUFFER_END
            V vert(A i) { V o; o.positionCS=TransformObjectToHClip(i.positionOS.xyz); o.uv=i.uv; return o; }
            float oval(float2 p, float2 r) { return 1-smoothstep(.88,1.03,length(p/r)); }
            half4 frag(V i):SV_Target
            {
                float2 p=i.uv;
                float eyeY=.62;
                float h=lerp(.088,.008,_Blink);
                float eyes=max(oval(p-float2(.32,eyeY),float2(.026,h)),oval(p-float2(.68,eyeY),float2(.026,h)));
                float2 m=p-float2(.5,.34);
                float smile=(1-smoothstep(.013,.025,abs(m.y-(2.8*m.x*m.x-.018))))*(1-smoothstep(.105,.12,abs(m.x)));
                float open=oval(m,float2(.075,lerp(.019,.086,_MouthOpen)));
                float worried=(1-smoothstep(.012,.024,abs(m.y-(-2*m.x*m.x+.015))))*(1-smoothstep(.10,.115,abs(m.x)));
                float mouth=lerp(lerp(smile,worried,_Mood),open,saturate(_MouthOpen*2));
                float2 edge=(p-.5)/float2(.47,.40);
                float rim=(1-smoothstep(.010,.022,abs(length(edge)-1)))*.22;
                float mask=max(max(eyes,mouth),rim);
                float3 screen=float3(.011,.022,.036)+.012*(1-p.y);
                return half4(screen+mask*_FaceColor.rgb,1);
            }
            ENDHLSL
        }
    }
}
