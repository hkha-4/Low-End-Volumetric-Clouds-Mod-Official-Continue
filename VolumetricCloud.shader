Shader "Hidden/LowEndVolumetricCloud"
{
    Properties
    {
        _MainTex ("Base (RGB)", 2D) = "white" {}
        _WeatherMap ("Weather Structure Map (R=Density, G=Cavity, B=Storm)", 2D) = "white" {}
        _CloudColor ("Dynamic Color", Color) = (1,1,1,1)
        _PressureColor ("Atmospheric Pressure Tint", Color) = (0.45, 0.12, 0.72, 1)
        _IntensityModifier ("Dynamic Density", Float) = 1.0
        _OptimParams ("Optimization Params (X=Steps, Y=CavityThresh, Z=ExtraFPS, W=SwirlSpeed)", Vector) = (12, 0.12, 1, 1.0)
        _StormParams ("Storm Params (X=RainIntensity, Y=HurricaneStrength, Z=PressureDistortion, W=CyclePhase)", Vector) = (0, 0.2, 0.05, 0)
        _DeferredParams ("Deferred Params (X=Enabled, Y=DepthFade, Z=GBufferBlend, W=LinearLum)", Vector) = (0, 0.85, 0.30, 1.05)
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            sampler2D _WeatherMap;
            sampler2D _CameraDepthTexture;
            sampler2D _CameraGBufferTexture0;
            float4 _CloudColor;
            float4 _PressureColor;
            float _IntensityModifier;
            float4 _OptimParams;    // x: maxSamples, y: cavityThreshold, z: extraFpsBoost (0 or 1), w: swirlSpeed
            float4 _StormParams;    // x: rainIntensity (0..1), y: hurricaneStrength, z: pressureDistortion, w: cyclePhase
            float4 _DeferredParams; // x: deferredEnabled (0 or 1), y: depthFade, z: gBufferBlend, w: linearLum

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            // PERFORMANCE-OPTIMIZED MULTI-PLANET RAYMARCHER (v1.1)
            // Supports Kerbin, Eve (purple hurricanes & high pressure), Jool, Extra FPS Boost, and 45s Rain Cycles
            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 screenColor = tex2D(_MainTex, i.uv);

                // PERFORMANCE GATEWAY 1: Skip entirely if planet has no atmosphere or mod is disabled
                if (_IntensityModifier <= 0.01)
                {
                    return screenColor;
                }

                float rainIntensity = saturate(_StormParams.x);
                float hurricaneStrength = _StormParams.y;
                float pressureDistortion = _StormParams.z;
                float extraFpsBoost = _OptimParams.z;
                float swirlSpeed = max(0.1, _OptimParams.w);

                // High-pressure atmospheric banding & hurricane vortex UV warp (Eve & Jool)
                float2 baseUV = i.uv;
                if (pressureDistortion > 0.05)
                {
                    float pressureWave = sin(baseUV.y * 22.0 + _Time.y * 1.4) * 0.008 * pressureDistortion;
                    baseUV.x += pressureWave;
                }

                if (hurricaneStrength > 0.15)
                {
                    // Lightweight spiral vortex centered around storm cell coordinates
                    float2 centered = baseUV - float2(0.5, 0.52);
                    float dist = dot(centered, centered);
                    float angle = (1.0 - saturate(dist * 3.2)) * hurricaneStrength * 1.65;
                    float s = sin(angle);
                    float c = cos(angle);
                    baseUV = float2(centered.x * c - centered.y * s, centered.x * s + centered.y * c) + float2(0.5, 0.52);
                }

                // PERFORMANCE GATEWAY 2: Sample the 2D Weather Map first
                float windScroll = _Time.x * (0.05 + rainIntensity * 0.07 + hurricaneStrength * 0.04) * swirlSpeed;
                float4 weather = tex2D(_WeatherMap, baseUV + float2(windScroll, 0.0));
                float cavityMask = weather.g;
                float stormSpiral = weather.b;

                // During storms/rain cycles, clouds expand into cavities slightly
                float effectiveThreshold = max(0.03, _OptimParams.y - rainIntensity * 0.08);

                // Early exit inside clear atmospheric cavities
                if (cavityMask < effectiveThreshold)
                {
                    return screenColor;
                }

                // PERFORMANCE GATEWAY 3 (Extra FPS Boost): Interlaced Checkerboard Fast-Path
                // Every alternating pixel uses a single fast composite sample instead of the raymarch loop
                int2 pixelCoord = int2(floor(i.uv * _ScreenParams.xy));
                bool isFastSkipPixel = (extraFpsBoost > 0.5) && (((pixelCoord.x + pixelCoord.y) & 1) == 1);

                float cloudDensity = 0.0;

                if (isFastSkipPixel)
                {
                    float2 fastUV = baseUV * 12.0 + float2(_Time.y * 0.3 * swirlSpeed, 0.0);
                    float fastNoise = sin(fastUV.x) * cos(fastUV.y) * 0.5 + 0.5;
                    cloudDensity = saturate(fastNoise * cavityMask * (0.75 + rainIntensity * 0.55));
                }
                else
                {
                    int maxSamples = (int)clamp(_OptimParams.x, 4.0, 32.0);
                    // Extra FPS Boost dynamically caps effective steps by ~25% in clear weather
                    if (extraFpsBoost > 0.5 && maxSamples > 6)
                    {
                        maxSamples = max(6, (maxSamples * 3) / 4);
                    }

                    float stepGain = (1.15 / (float)maxSamples) * (1.0 + rainIntensity * 0.5 + pressureDistortion * 0.35);

                    [loop]
                    for (int sampleIdx = 0; sampleIdx < 32; sampleIdx++)
                    {
                        if (sampleIdx >= maxSamples) break;

                        float heightFactor = (float)sampleIdx / (float)maxSamples;

                        // Swirl effect scaled by hurricane strength and rain storm cycle
                        float swirlAmp = 0.2 + hurricaneStrength * 0.35;
                        float2 swirledUV = baseUV * (14.0 + hurricaneStrength * 4.0) +
                            float2(sin(_Time.y * swirlSpeed + heightFactor * 2.0), cos(_Time.y * swirlSpeed + heightFactor * 2.0)) * swirlAmp;
                        float stormNoise = sin(swirledUV.x) * cos(swirledUV.y);

                        float finalShape = stormNoise * cavityMask * (stormSpiral + rainIntensity * 0.25);
                        float heightProfile = smoothstep(0.0, 0.28, heightFactor) * smoothstep(1.0, 0.68, heightFactor);

                        if (finalShape > (0.36 * (1.0 - heightProfile) - rainIntensity * 0.08))
                        {
                            cloudDensity += stepGain;
                        }

                        if (cloudDensity >= 0.96)
                        {
                            cloudDensity = 1.0;
                            break;
                        }
                    }
                }

                // Apply deep shadow shading inside storm & high-pressure layers
                float shadowFloor = lerp(0.32, 0.16, saturate(rainIntensity * 0.6 + pressureDistortion * 0.5));
                float3 stormTint = lerp(_CloudColor.rgb, _PressureColor.rgb, saturate(pressureDistortion * 0.65 + rainIntensity * 0.35));
                float3 finalPlanetColor = lerp(stormTint * shadowFloor, _CloudColor.rgb, cavityMask);

                // Subtle high-pressure atmospheric compression glow (especially visible on Eve)
                if (pressureDistortion > 0.25)
                {
                    float pressureHaze = smoothstep(0.2, 0.9, 1.0 - i.uv.y) * pressureDistortion * 0.28;
                    finalPlanetColor = lerp(finalPlanetColor, _PressureColor.rgb * 1.15, pressureHaze);
                }

                // 45-Second Rain Cycle: Cheap GPU-procedural angled rain streaks during active rain/storms
                if (rainIntensity > 0.08)
                {
                    float2 rainUV = i.uv * float2(48.0, 14.0);
                    rainUV.x += i.uv.y * (3.5 + hurricaneStrength * 4.0);
                    rainUV.y += _Time.y * (16.0 + rainIntensity * 14.0);
                    float streak = smoothstep(0.92, 0.99, sin(rainUV.x * 6.2831) * 0.5 + 0.5) *
                                   smoothstep(0.55, 0.95, sin(rainUV.y * 6.2831) * 0.5 + 0.5);
                    float3 rainColor = lerp(float3(0.68, 0.85, 1.0), _PressureColor.rgb * 1.4 + float3(0.25, 0.6, 0.2), saturate(pressureDistortion));
                    screenColor.rgb = lerp(screenColor.rgb, rainColor, streak * rainIntensity * 0.32);
                }

                // Blend the final cloud volume over the game view
                float blendAlpha = saturate(cloudDensity * (_IntensityModifier * 0.88 + rainIntensity * 0.12));

                // Native Blackrack Deferred Rendering Integration (auto-ON when GameData/Deferred is detected)
                if (_DeferredParams.x > 0.5)
                {
                    float rawDepth = SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uv);
                    float linearDepth = Linear01Depth(rawDepth);
                    float depthMask = lerp(1.0, smoothstep(0.015, 0.14, linearDepth), saturate(_DeferredParams.y));
                    float3 gbufAlbedo = tex2D(_CameraGBufferTexture0, i.uv).rgb;
                    finalPlanetColor = lerp(
                        finalPlanetColor,
                        finalPlanetColor * (0.85 + gbufAlbedo * 0.45),
                        saturate(_DeferredParams.z) * (1.0 - cloudDensity * 0.45)
                    ) * _DeferredParams.w;
                    blendAlpha *= depthMask;
                }

                return lerp(screenColor, float4(finalPlanetColor, 1.0), blendAlpha);
            }
            ENDCG
        }
    }
}
