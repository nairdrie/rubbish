// Rubbish day/night skybox: crossfades two "6 Sided" skyboxes (A -> B by _Blend) and paints procedural extras
// on top: twinkly stars, a shooting star, a warm sun glow + horizon tint, and a big derpy smiling moon with
// googly eyes that wander and blink. Driven every frame by DayNightSky (Assets/Scripts/World/DayNightSky.cs).
// Six passes like the built-in Skybox/6 Sided, so Unity draws it with the six-faced sky mesh.
Shader "Rubbish/SkyBlend6Sided"
{
    Properties
    {
        _Tint ("Tint Color", Color) = (.5, .5, .5, .5)
        [Gamma] _Exposure ("Exposure", Range(0, 8)) = 1.0
        _Rotation ("Rotation", Range(0, 360)) = 0
        _Blend ("Blend A -> B", Range(0, 1)) = 0

        [NoScaleOffset] _FrontTex ("Front A [+Z]", 2D) = "grey" {}
        [NoScaleOffset] _BackTex ("Back A [-Z]", 2D) = "grey" {}
        [NoScaleOffset] _LeftTex ("Left A [+X]", 2D) = "grey" {}
        [NoScaleOffset] _RightTex ("Right A [-X]", 2D) = "grey" {}
        [NoScaleOffset] _UpTex ("Up A [+Y]", 2D) = "grey" {}
        [NoScaleOffset] _DownTex ("Down A [-Y]", 2D) = "grey" {}

        [NoScaleOffset] _FrontTexB ("Front B [+Z]", 2D) = "grey" {}
        [NoScaleOffset] _BackTexB ("Back B [-Z]", 2D) = "grey" {}
        [NoScaleOffset] _LeftTexB ("Left B [+X]", 2D) = "grey" {}
        [NoScaleOffset] _RightTexB ("Right B [-X]", 2D) = "grey" {}
        [NoScaleOffset] _UpTexB ("Up B [+Y]", 2D) = "grey" {}
        [NoScaleOffset] _DownTexB ("Down B [-Y]", 2D) = "grey" {}

        _HorizonColor ("Horizon Tint (rgb, a = amount)", Color) = (1, 0.6, 0.4, 0)
        _ZenithColor ("Gradient Zenith (rgb, a = gradient override amount)", Color) = (0.2, 0.25, 0.6, 0)
        [HDR] _SunGlowColor ("Sun Glow", Color) = (0, 0, 0, 1)
        _SunDir ("Sun Direction", Vector) = (0, 1, 0, 0)

        _StarAmount ("Stars", Range(0, 1)) = 0
        _MoonAmount ("Moon", Range(0, 1)) = 0
        _MoonDir ("Moon Direction", Vector) = (0, 0.5, -1, 0)
        _MoonSize ("Moon Radius (radians)", Float) = 0.085
        [HDR] _MoonColor ("Moon Color", Color) = (1.3, 1.25, 1.05, 1)
        _MoonLook ("Moon pupils (xy L, zw R)", Vector) = (0, 0, 0, 0)
        _MoonBlink ("Moon Blink", Range(0, 1)) = 0
        _ShootA ("Shooting star start (xyz), strength (w)", Vector) = (1, 0.5, 0, 0)
        _ShootB ("Shooting star end (xyz), progress (w)", Vector) = (0, 0.6, 1, 0)
    }

    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }
        Cull Off ZWrite Off

        CGINCLUDE
        #include "UnityCG.cginc"

        half4 _Tint;
        half _Exposure;
        float _Rotation;
        half _Blend;
        half4 _HorizonColor;
        half4 _ZenithColor;
        half4 _SunGlowColor;
        float4 _SunDir;
        half _StarAmount;
        half _MoonAmount;
        float4 _MoonDir;
        float _MoonSize;
        half4 _MoonColor;
        float4 _MoonLook;
        half _MoonBlink;
        float4 _ShootA;
        float4 _ShootB;

        float3 RotateAroundYInDegrees(float3 vertex, float degrees)
        {
            float alpha = degrees * UNITY_PI / 180.0;
            float sina, cosa;
            sincos(alpha, sina, cosa);
            float2x2 m = float2x2(cosa, -sina, sina, cosa);
            return float3(mul(m, vertex.xz), vertex.y).xzy;
        }

        struct appdata_t
        {
            float4 vertex : POSITION;
            float2 texcoord : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct v2f
        {
            float4 vertex : SV_POSITION;
            float2 texcoord : TEXCOORD0;
            float3 dir : TEXCOORD1;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        v2f vert(appdata_t v)
        {
            v2f o;
            UNITY_SETUP_INSTANCE_ID(v);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            float3 rotated = RotateAroundYInDegrees(v.vertex.xyz, _Rotation);
            o.vertex = UnityObjectToClipPos(rotated);
            o.texcoord = v.texcoord;
            o.dir = v.vertex.xyz; // sky mesh is camera-centred: object position == world direction
            return o;
        }

        float3 Hash33(float3 p)
        {
            p = frac(p * float3(443.897, 441.423, 437.195));
            p += dot(p, p.yxz + 19.19);
            return frac((p.xxy + p.yxx) * p.zyx);
        }

        // Sparse twinkly stars on a 3D grid over the sphere (one candidate star per cell).
        float3 Stars(float3 d)
        {
            float3 p = d * 62.0;
            float3 cell = floor(p);
            float3 h = Hash33(cell);
            if (h.x < 0.86) return 0;
            float3 centre = normalize(cell + 0.3 + Hash33(cell + 17.0) * 0.4);
            float ang = 1.0 - dot(d, centre);                    // ~ theta^2 / 2
            float radius = lerp(0.0011, 0.0036, h.y * h.y * h.y); // radians
            float s = saturate(1.0 - ang / (radius * radius * 0.5));
            s *= s;
            float tw = 0.55 + 0.45 * sin(_Time.y * (1.3 + h.z * 3.7) + h.y * 40.0);
            float3 col = lerp(float3(0.75, 0.85, 1.0), float3(1.0, 0.92, 0.7), h.z);
            return col * s * tw * (0.6 + 1.4 * h.y);
        }

        float3 ShootingStar(float3 d)
        {
            if (_ShootA.w <= 0.001) return 0;
            float3 a = normalize(_ShootA.xyz);
            float3 b = normalize(_ShootB.xyz);
            float t = _ShootB.w;
            float3 head = normalize(lerp(a, b, t));
            float3 tail = normalize(lerp(a, b, max(0.0, t - 0.45)));
            float3 pa = d - tail, ba = head - tail;
            float k = saturate(dot(pa, ba) / max(dot(ba, ba), 1e-6));
            float dist = length(pa - ba * k);
            float streak = 1.0 - smoothstep(0.0, 0.0034, dist * (2.0 - k));
            float fade = sin(saturate(t) * UNITY_PI);
            return float3(1.0, 0.95, 0.8) * streak * k * k * fade * 4.0 * _ShootA.w;
        }

        float Circle(float2 p, float r, float aa)
        {
            return 1.0 - smoothstep(r - aa, r + aa, length(p));
        }

        // The derpy moon: pale disc, craters, rosy cheeks, googly eyes (wandering pupils, blinks) and a smile.
        float3 Moon(float3 d, float3 c)
        {
            if (_MoonAmount <= 0.001) return c;
            float3 m = normalize(_MoonDir.xyz);
            float facing = dot(d, m);
            if (facing <= 0.0) return c;
            float3 right = normalize(cross(float3(0, 1, 0), m));
            float3 up = cross(m, right);
            float2 uv = float2(dot(d, right), dot(d, up)) / _MoonSize;
            float r = length(uv);
            float aa = max(fwidth(r), 0.004) * 1.2;

            // soft halo
            float halo = exp(-max(r - 1.0, 0.0) * 2.4);
            c += _MoonColor.rgb * halo * 0.22 * _MoonAmount;
            if (r > 1.05) return c;

            float3 body = _MoonColor.rgb;
            // craters
            body *= 1.0 - 0.13 * Circle(uv - float2(-0.50, 0.55), 0.16, aa);
            body *= 1.0 - 0.10 * Circle(uv - float2(0.58, 0.50), 0.11, aa);
            body *= 1.0 - 0.09 * Circle(uv - float2(0.12, -0.78), 0.09, aa);
            body *= 1.0 - 0.07 * Circle(uv - float2(-0.78, -0.35), 0.07, aa);
            // edge shading so it reads as a ball
            body *= lerp(1.0, 0.82, smoothstep(0.55, 1.0, r));
            // rosy cheeks
            float cheeks = Circle(uv - float2(-0.56, -0.22), 0.15, 0.08) + Circle(uv - float2(0.56, -0.22), 0.15, 0.08);
            body = lerp(body, _MoonColor.rgb * float3(1.0, 0.55, 0.55), saturate(cheeks) * 0.6);

            // googly eyes (left one a bit bigger, for derp)
            float open = max(0.06, 1.0 - _MoonBlink);
            float2 eL = float2(-0.30, 0.20), eR = float2(0.31, 0.22);
            float rL = 0.215, rR = 0.185;
            float2 qL = (uv - eL) / float2(1.0, open);
            float2 qR = (uv - eR) / float2(1.0, open);
            float ringL = Circle(qL, rL + 0.035, aa);
            float ringR = Circle(qR, rR + 0.035, aa);
            float whiteL = Circle(qL, rL, aa);
            float whiteR = Circle(qR, rR, aa);
            float3 ink = float3(0.10, 0.07, 0.09);
            body = lerp(body, ink, saturate(ringL + ringR));
            body = lerp(body, _MoonColor.rgb * 1.05 + 0.05, saturate(whiteL + whiteR));
            float pL = Circle(qL - _MoonLook.xy * (rL - 0.10), 0.10, aa) * whiteL;
            float pR = Circle(qR - _MoonLook.zw * (rR - 0.085), 0.085, aa) * whiteR;
            body = lerp(body, ink, saturate(pL + pR));
            // eye shine
            float shine = Circle(qL - _MoonLook.xy * (rL - 0.10) - float2(0.035, 0.04), 0.028, aa) * whiteL
                        + Circle(qR - _MoonLook.zw * (rR - 0.085) - float2(0.03, 0.035), 0.024, aa) * whiteR;
            body = lerp(body, float3(1.2, 1.2, 1.2), saturate(shine));

            // big goofy smile (lower arc) with a little tongue
            float2 sc = uv - float2(0.0, 0.02);
            float sr = length(sc);
            float mouth = (1.0 - smoothstep(0.035 - aa, 0.035 + aa, abs(sr - 0.40))) * smoothstep(-0.10, -0.16, sc.y);
            float tongue = Circle((uv - float2(0.10, -0.43)) / float2(1.0, 0.7), 0.075, aa) * step(uv.y, -0.40);
            body = lerp(body, ink, saturate(mouth));
            body = lerp(body, _MoonColor.rgb * float3(1.0, 0.45, 0.5), saturate(tongue));

            float disc = 1.0 - smoothstep(1.0 - aa, 1.0 + aa, r);
            return lerp(c, body, disc * _MoonAmount);
        }

        half4 SkyFrag(v2f i, sampler2D a, half4 aHDR, sampler2D b, half4 bHDR)
        {
            half3 ca = DecodeHDR(tex2D(a, i.texcoord), aHDR);
            half3 cb = DecodeHDR(tex2D(b, i.texcoord), bHDR);
            float3 c = lerp(ca, cb, _Blend);
            c = c * _Tint.rgb * unity_ColorSpaceDouble.rgb * _Exposure;

            float3 d = normalize(i.dir);
            // painted dusk/dawn gradient over the texture (the pack's sunset texture is a bit murky)
            float3 grad = lerp(_HorizonColor.rgb, _ZenithColor.rgb, smoothstep(0.0, 0.6, saturate(d.y)));
            grad = d.y < 0.0 ? _HorizonColor.rgb * lerp(1.0, 0.55, saturate(-d.y * 3.0)) : grad;
            c = lerp(c, grad, _ZenithColor.a);
            float hz = pow(saturate(1.0 - abs(d.y)), 4.0);
            c = lerp(c, _HorizonColor.rgb, hz * _HorizonColor.a);

            float sd = saturate(dot(d, normalize(_SunDir.xyz)));
            c += _SunGlowColor.rgb * (pow(sd, 5.0) * 0.45 + pow(sd, 90.0) * 1.6);

            float above = saturate(d.y * 5.0 + 0.15);
            c += Stars(d) * _StarAmount * above;
            c += ShootingStar(d) * above;
            c = Moon(d, c);
            return half4(c, 1);
        }
        ENDCG

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            sampler2D _FrontTex; half4 _FrontTex_HDR;
            sampler2D _FrontTexB; half4 _FrontTexB_HDR;
            half4 frag(v2f i) : SV_Target { return SkyFrag(i, _FrontTex, _FrontTex_HDR, _FrontTexB, _FrontTexB_HDR); }
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            sampler2D _BackTex; half4 _BackTex_HDR;
            sampler2D _BackTexB; half4 _BackTexB_HDR;
            half4 frag(v2f i) : SV_Target { return SkyFrag(i, _BackTex, _BackTex_HDR, _BackTexB, _BackTexB_HDR); }
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            sampler2D _LeftTex; half4 _LeftTex_HDR;
            sampler2D _LeftTexB; half4 _LeftTexB_HDR;
            half4 frag(v2f i) : SV_Target { return SkyFrag(i, _LeftTex, _LeftTex_HDR, _LeftTexB, _LeftTexB_HDR); }
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            sampler2D _RightTex; half4 _RightTex_HDR;
            sampler2D _RightTexB; half4 _RightTexB_HDR;
            half4 frag(v2f i) : SV_Target { return SkyFrag(i, _RightTex, _RightTex_HDR, _RightTexB, _RightTexB_HDR); }
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            sampler2D _UpTex; half4 _UpTex_HDR;
            sampler2D _UpTexB; half4 _UpTexB_HDR;
            half4 frag(v2f i) : SV_Target { return SkyFrag(i, _UpTex, _UpTex_HDR, _UpTexB, _UpTexB_HDR); }
            ENDCG
        }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            sampler2D _DownTex; half4 _DownTex_HDR;
            sampler2D _DownTexB; half4 _DownTexB_HDR;
            half4 frag(v2f i) : SV_Target { return SkyFrag(i, _DownTex, _DownTex_HDR, _DownTexB, _DownTexB_HDR); }
            ENDCG
        }
    }
    Fallback Off
}
