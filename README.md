# Low End Volumetric Clouds Mod

A performance-optimized, lightweight 3D true volumetric cloud engine designed specifically for Kerbal Space Program (KSP) running on low-end PCs, older dedicated GPUs, and integrated graphics processors (like Intel HD/UHD Graphics and AMD Radeon Vega iGPUs).

Now you can fly your rockets through deep cloud canyons, brave swirling weather cells, and dive into massive atmospheric air cavities without sacrificing your frame rates!

## ✨ What's New in v1.1.0
*   **Eve Overhaul**: Thick purple storm decks, swirling rotational hurricanes, and obvious high-pressure atmospheric haze & banding on the planet Eve.
*   **In-Game Settings Menu**: Press **`Alt + 0`** or click the **green cloud/globe icon** in the stock KSP flight toolbar to tune downscale factor, raymarch layers, cavity skip threshold, swirl speed, opacity, Extra FPS Boost, and Rain Cycles live.
*   **Extra FPS Boost (Up to +15 FPS)**: Adds interlaced checkerboard pixel skipping, adaptive raymarch step reduction, cached `Shader.PropertyToID` uniforms, and automatic high-orbit/no-atmosphere culling.
*   **45-Second Rain Cycles**: Dynamic weather loops through a continuous 45-second cycle (`Calm Skies -> Light Rain -> Heavy Storm / Hurricane -> Storm Passing`) with real-time GPU rain streaks and storm cloud thickening.
*   **Native OPM Auto-Compatibility**: Automatically detects **Outer Planets Mod (OPM)** at runtime (`GameData/OPM` or OPM celestial bodies) and switches OPM compatibility **ON** when installed or **OFF** automatically when not installed.
*   **Native Blackrack Deferred Auto-Integration**: Automatically detects **Blackrack's Deferred Rendering** (`GameData/Deferred` or `Deferred.dll`) and switches ON (`CameraEvent.BeforeForwardAlpha` with G-Buffer horizon bounce & `_CameraDepthTexture` occlusion) or OFF (`CameraEvent.AfterSkybox` stock forward rendering) automatically.

## 📉 Low-End Optimization Secrets
Standard true volumetric cloud mods run heavy 3D calculations that crush budget graphics cards. This mod maintains high frame rates using smart math tricks:
*   **Quarter-Resolution Rendering**: Clouds are rendered into a lightweight, downscaled buffer (1/4 of your native resolution) and seamlessly composited back onto your screen, cutting GPU fill-rate demands by up to 85%.
*   **Ultra-Low Sample Bounds**: Raymarching step paths are strictly capped at 12 layers by default (compared to the standard 64+ layers used by high-end mods).
*   **Early-Exit Cavity Performance Gateways**: The raymarching engine reads a static 2D Weather Map before computing pixels. When your ship hovers over a deep cloud canyon or eye of a storm, the GPU skips execution entirely, dropping rendering costs in empty spaces to absolute zero.
*   **Interlaced Fast-Path & Orbit Culling**: Extra FPS Boost cuts raymarch loops further via alternating pixel evaluation and turns rendering off completely above `2.5x` atmosphere altitude.

## 🪐 Supported Planetary Bodies
This mod features a dynamic, planet-aware engine loop that reads your vessel’s location and instantly adapts its weather profiles at runtime:
*   **Kerbin**: Crisp, fluffy white-and-grey atmospheric storm decks with dramatic vertical canyon walls and 45-second rain squalls.
*   **Eve**: Dense violet-purple hurricane cyclones, obvious high-pressure atmospheric compression haze, and intense toxic storm cycles.
*   **Jool**: Thick, cinematic, toxic green gas-giant cloud decks layered with sweeping rotational cyclone storm walls.
*   **OPM Worlds (Auto-Enabled when OPM is installed)**:
    *   **Sarnus**: Warm golden-ochre banded gas giant storm decks.
    *   **Tekto**: Dense emerald-teal methane haze with 45-second methane rain cycles.
    *   **Urlum**: Pale cyan/aquamarine ice-giant cloud layers.
    *   **Neidon**: Deep royal-indigo ice-giant storms and high-velocity hurricanes.
    *   **Thatmo**: Light wispy nitrogen haze.
*   **Atmosphere-less Bodies (Mun, Minmus, Plock, etc.)**: The rendering pipeline automatically deactivates to preserve 100% of your hardware performance.

## ⚙️ How to Install
This mod is packaged natively to be managed and routed flawlessly by **CKAN**.
1. Open your **CKAN** client application.
2. Search for `Low End Volumetric Clouds Mod` in the global index.
3. Check the box and click **Apply Changes**. CKAN will handle the path allocations and file routing automatically for both Steam and portable standalone installs!

## 📜 License & Open Source
This project is completely open-source and distributed under the **GNU GPLv3** license. You are free to inspect our low-end optimization techniques, share the mod, or modify the source code, provided that all derivative works remain entirely open-source under the exact same license terms.
