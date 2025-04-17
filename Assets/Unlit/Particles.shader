Shader "Unlit/Particles"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
    }
    SubShader
    {
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5

            struct Particle
            {
                float3 position; float pad0;
                float3 velocity; float pad1;
                float3 predictedPosition; float pad2;
                float3 deltaP; float pad3;
                float lambda; float3 pad4;
                float4 color;
            };

            StructuredBuffer<Particle> _Particles;

            struct appdata { uint vertexID : SV_VertexID; };
            struct v2f { float4 pos : SV_POSITION; float4 color : COLOR; };

            v2f vert(appdata v)
            {
                v2f o;
                float3 pos = _Particles[v.vertexID].position;
                o.pos = UnityObjectToClipPos(float4(pos, 1.0));
                o.color = _Particles[v.vertexID].color;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                return i.color;
            }
            ENDCG
        }
    }
}