// Rubbish water (river and sea): low-poly to match the town. The mesh is a plain grid; small waves move its vertices,
// every triangle is shaded as one flat facet so the surface glitters as it moves, the colour deepens away from the
// bank, foam laps at the waterline and (on the river) streaks of lighter water drift downstream.
// No textures, no depth or opaque texture: what the shader needs to know about the shore is baked into the mesh by
// the relief bake (TownReliefBaker): uv0 = (metres along the flow, metres across), uv1.x = metres from the waterline.
Shader "Rubbish/Water"
{
    Properties
    {
        _ShallowColor ("Shallow colour", Color) = (0.38, 0.78, 0.86, 1)
        _DeepColor ("Deep colour", Color) = (0.13, 0.42, 0.66, 1)
        _FoamColor ("Foam colour", Color) = (0.96, 0.99, 1, 1)
        _DepthFade ("Metres from the bank to deep colour", Float) = 9
        _WaveHeight ("Wave height (m)", Float) = 0.07
        _WaveLength ("Wave length (m)", Float) = 7
        _WaveSpeed ("Wave speed", Float) = 1
        _Flow ("Flow along the mesh (m/s)", Float) = 1.2
        _Streaks ("Flow streak strength", Range(0, 1)) = 0.35
        _FoamWidth ("Foam reach from the bank (m)", Float) = 3.2
        _Glint ("Sun glint", Range(0, 2)) = 0.9
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry+10" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        CBUFFER_START(UnityPerMaterial)
            half4 _ShallowColor, _DeepColor, _FoamColor;
            float _DepthFade, _WaveHeight, _WaveLength, _WaveSpeed, _Flow, _Streaks, _FoamWidth, _Glint;
        CBUFFER_END

        // Three crossing swells in world space, so two water meshes that meet (river mouth, sea) move as one surface.
        float WaveAt(float2 xz, float shore)
        {
            float k = 6.2831853 / max(0.5, _WaveLength), t = _Time.y * _WaveSpeed;
            float h = sin(dot(xz, float2(0.80, 0.60)) * k + t * 1.7)
                    + 0.6 * sin(dot(xz, float2(-0.55, 0.83)) * k * 1.7 + t * 2.3)
                    + 0.4 * sin(dot(xz, float2(0.25, -0.97)) * k * 2.9 + t * 3.1);
            return h * _WaveHeight * 0.5 * saturate(shore * 0.4);          // still at the very edge: no gap at the bank
        }
        float3 Displaced(float3 positionOS, float shore)
        {
            float3 positionWS = TransformObjectToWorld(positionOS);
            positionWS.y += WaveAt(positionWS.xz, shore);
            return positionWS;
        }
        ENDHLSL

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back ZWrite On

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; float2 uv1 : TEXCOORD1; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 flow : TEXCOORD1;                    // along, across, shore
                float fog : TEXCOORD2;
            };

            Varyings Vert(Attributes input)
            {
                Varyings o;
                o.positionWS = Displaced(input.positionOS.xyz, input.uv1.x);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.flow = float3(input.uv, input.uv1.x);
                o.fog = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            float Hash(float2 p) { return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453); }
            float Noise(float2 p)
            {
                float2 i = floor(p), f = frac(p); f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(Hash(i), Hash(i + float2(1, 0)), f.x), lerp(Hash(i + float2(0, 1)), Hash(i + float2(1, 1)), f.x), f.y);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                // one normal per triangle: the facets are the look
                float3 n = normalize(cross(ddy(input.positionWS), ddx(input.positionWS)));
                if (n.y < 0) n = -n;
                // exaggerate the tilt a little: the waves are centimetres high but should read from across the river
                n = normalize(float3(n.x * 3.0, n.y, n.z * 3.0));
                float along = input.flow.x, across = input.flow.y, shore = max(0, input.flow.z);
                float t = _Time.y;

                half3 colour = lerp(_ShallowColor.rgb, _DeepColor.rgb, saturate(shore / max(0.01, _DepthFade)));

                // lighter water drifting downstream in long streaks
                float streak = Noise(float2((along - t * _Flow) * 0.16, across * 0.55)) * 0.65 + Noise(float2((along - t * _Flow * 1.4) * 0.45, across * 1.3)) * 0.35;
                colour += smoothstep(0.62, 0.78, streak) * _Streaks * 0.22 * saturate(shore * 0.5);

                Light light = GetMainLight(TransformWorldToShadowCoord(input.positionWS));
                float3 view = normalize(GetWorldSpaceViewDir(input.positionWS));
                half ndl = saturate(dot(n, light.direction));
                half3 ambient = SampleSH(n);
                half3 lit = colour * (ambient + light.color * (0.35 + 0.65 * ndl) * lerp(0.55, 1.0, light.shadowAttenuation));
                // sky on the facets that face away, sun on the ones that catch it
                half fresnel = pow(1.0 - saturate(dot(n, view)), 3.0);
                lit = lerp(lit, SampleSH(float3(0, 1, 0)) * 1.15 + colour * 0.2, fresnel * 0.4);
                half glint = pow(saturate(dot(n, normalize(light.direction + view))), 90.0);
                lit += light.color * step(0.5, glint) * _Glint * 0.5 * light.shadowAttenuation;

                // foam: a solid lip at the waterline and chunky lines that lap towards it
                float reach = saturate(1.0 - shore / max(0.01, _FoamWidth));
                float wobble = Noise(input.positionWS.xz * 0.7 + t * 0.2);
                float lip = step(shore, 0.35 + 0.3 * wobble + 0.15 * sin(t * 1.3 + along * 0.4));
                float lap = step(0.55, sin(shore * 3.4 + t * 1.5 + wobble * 5.0) * reach + wobble * 0.5 * reach);
                half foam = saturate(lip + lap * step(0.02, reach));
                lit = lerp(lit, _FoamColor.rgb * (ambient + light.color * 0.8), foam * 0.9);

                lit = MixFog(lit, input.fog);
                return half4(lit, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On ColorMask R Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            struct Attributes { float4 positionOS : POSITION; float2 uv1 : TEXCOORD1; };
            float4 Vert(Attributes input) : SV_POSITION { return TransformWorldToHClip(Displaced(input.positionOS.xyz, input.uv1.x)); }
            half4 Frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On Cull Back

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            struct Attributes { float4 positionOS : POSITION; float2 uv1 : TEXCOORD1; };
            float4 Vert(Attributes input) : SV_POSITION { return TransformWorldToHClip(Displaced(input.positionOS.xyz, input.uv1.x)); }
            half4 Frag() : SV_Target { return half4(0, 1, 0, 0); }            // straight up is close enough for ambient occlusion
            ENDHLSL
        }
    }
    FallBack Off
}
