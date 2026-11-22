using System;
using System.IO;
using System.Reflection;
using KSP.UI.Screens;
using UnityEngine;
using UnityEngine.Rendering;

namespace LowEndVolumetricCloudsMod
{
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class LowEndVolumetricCloudsPlugin : MonoBehaviour
    {
        private Camera flightCamera;
        private CommandBuffer cloudCommandBuffer;
        private Material cloudMaterial;
        private Texture2D loadedWeatherMap;
        private Texture2D toolbarIconTexture;
        private ApplicationLauncherButton toolbarButton;

        private bool isPipelineInitialized = false;
        private bool showSettingsWindow = false;
        private bool isAppLauncherHooked = false;

        // Native Outer Planets Mod (OPM) auto-detection state
        private bool isOpmDetected = false;
        private bool opmCompatibilityEnabled = false;
        private bool opmDetectionCompleted = false;

        // Native Blackrack's Deferred Rendering auto-detection state
        private bool isDeferredDetected = false;
        private bool deferredIntegrationEnabled = false;
        private bool deferredDetectionCompleted = false;
        private bool lastDeferredState = false;
        private CameraEvent activeCameraEvent = CameraEvent.AfterSkybox;

        // Cached Shader Property IDs (Extra FPS Boost: avoids per-frame string hashing)
        private static readonly int CloudColorId = Shader.PropertyToID("_CloudColor");
        private static readonly int PressureColorId = Shader.PropertyToID("_PressureColor");
        private static readonly int IntensityModifierId = Shader.PropertyToID("_IntensityModifier");
        private static readonly int OptimParamsId = Shader.PropertyToID("_OptimParams");
        private static readonly int StormParamsId = Shader.PropertyToID("_StormParams");
        private static readonly int DeferredParamsId = Shader.PropertyToID("_DeferredParams");
        private static readonly int WeatherMapId = Shader.PropertyToID("_WeatherMap");
        private static readonly int LowResBufferId = Shader.PropertyToID("_LowResCloudTex");

        // User-tunable in-game settings (Alt + 0 or green cloud/globe stock toolbar button)
        private int downscaleFactor = 4;        // 1, 2, 4, 8 (4x = quarter-res)
        private int maxSamples = 12;            // 4..32 raymarch layers
        private float cavityThreshold = 0.12f;  // 0.05..0.50 early exit threshold
        private float swirlSpeed = 1.0f;        // 0.1..2.5 swirl speed
        private float opacityMultiplier = 1.0f; // 0.2..1.5 cloud opacity multiplier
        private bool extraFpsBoost = true;      // Extra up to +15 FPS optimization (interlacing + adaptive steps + orbit culling)
        private bool rainCyclesEnabled = true;  // 45-second rain & storm cycle

        // Track buffer dimensions so we only rebuild CommandBuffer when actually needed
        private int lastBufferWidth = -1;
        private int lastBufferHeight = -1;
        private int lastDownscaleFactor = -1;
        private string lastPlanetName = string.Empty;

        // 45-Second Rain & Storm Cycle constants
        private const float RAIN_CYCLE_DURATION = 45.0f;
        private float currentRainIntensity = 0.0f;
        private float currentCycleSeconds = 0.0f;
        private string currentWeatherPhase = "Calm";

        // Settings panel layout
        private Rect windowRect = new Rect(Screen.width - 340f, 70f, 320f, 468f);

        // Reflection bindings for IMGUI & Legacy Input (compiles cleanly with core 4 KSP/Unity DLLs)
        private static bool reflectionInitialized = false;
        private static MethodInfo inputGetKey;
        private static MethodInfo inputGetKeyDown;
        private static MethodInfo guiBox;
        private static MethodInfo guiLabel;
        private static MethodInfo guiButton;
        private static MethodInfo guiToggle;
        private static MethodInfo guiHorizontalSlider;
        private static PropertyInfo guiColorProp;

        void Start()
        {
            Debug.Log("[LowEndVolumetricCloudsMod] v1.1.0 Start - initializing assets, OPM & Deferred checks, toolbar & pipeline...");
            EnsureReflectionBindings();
            InitializeAssets();
            DetectOuterPlanetsMod();
            DetectDeferredRendering();
            HookToolbarEvents();
        }

        void Update()
        {
            // Alt + 0 hotkey toggles the in-game settings panel
            if (IsAltZeroPressed())
            {
                ToggleSettingsWindow();
            }

            if (!opmDetectionCompleted && FlightGlobals.Bodies != null && FlightGlobals.Bodies.Count > 0)
            {
                DetectOuterPlanetsMod();
            }

            if (!deferredDetectionCompleted && flightCamera != null)
            {
                DetectDeferredRendering();
            }

            if (!isPipelineInitialized)
            {
                TryInitializePipeline();
            }

            if (!isPipelineInitialized || cloudMaterial == null || FlightGlobals.currentMainBody == null) return;

            // Extra FPS Boost: Rebuild CommandBuffer only if screen size, downscale, or Deferred CameraEvent changed
            int targetWidth = Mathf.Max(1, Screen.width / downscaleFactor);
            int targetHeight = Mathf.Max(1, Screen.height / downscaleFactor);
            if (targetWidth != lastBufferWidth || targetHeight != lastBufferHeight || downscaleFactor != lastDownscaleFactor || deferredIntegrationEnabled != lastDeferredState)
            {
                RebuildCommandBuffer(targetWidth, targetHeight);
            }

            CelestialBody body = FlightGlobals.currentMainBody;
            string planetName = body.name;

            // Extra FPS Boost: Skip shader work entirely on atmosphere-less bodies or deep space orbit (> 2.5x atmosphere height)
            double altitude = FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.altitude : 0.0;
            bool inDeepSpace = !body.atmosphere || (body.atmosphereDepth > 0 && altitude > body.atmosphereDepth * 2.5);

            if (inDeepSpace)
            {
                if (lastPlanetName != "__NONE__")
                {
                    cloudMaterial.SetFloat(IntensityModifierId, 0.0f);
                    lastPlanetName = "__NONE__";
                }
                currentRainIntensity = 0.0f;
                currentWeatherPhase = body.atmosphere ? "High Orbit (Culled)" : "No Atmosphere";
                return;
            }

            // 45-Second Rain & Storm Cycle
            UpdateRainCycle(planetName);

            // Planet-specific atmospheric & storm profiles
            Color baseCloudColor;
            Color pressureColor;
            float baseIntensity;
            float hurricaneStrength;
            float pressureDistortion;

            if (planetName == "Eve")
            {
                // EVE PROFILE: Purple storms, swirling hurricanes, and obvious high atmospheric pressure
                baseCloudColor = new Color(0.62f, 0.24f, 0.90f, 1.0f);
                pressureColor = new Color(0.42f, 0.08f, 0.68f, 1.0f);
                baseIntensity = 0.94f;
                hurricaneStrength = 0.95f + currentRainIntensity * 0.45f;
                pressureDistortion = 0.88f;
            }
            else if (planetName == "Jool")
            {
                // JOOL PROFILE: Thick, toxic emerald gas-giant storm bands & cyclones
                baseCloudColor = new Color(0.14f, 0.62f, 0.30f, 1.0f);
                pressureColor = new Color(0.05f, 0.34f, 0.16f, 1.0f);
                baseIntensity = 0.90f;
                hurricaneStrength = 0.55f + currentRainIntensity * 0.30f;
                pressureDistortion = 0.45f;
            }
            else if (planetName == "Kerbin")
            {
                // KERBIN PROFILE: Crisp white-silver atmospheric decks with periodic rain squalls
                float stormDarken = 1.0f - currentRainIntensity * 0.28f;
                baseCloudColor = new Color(0.92f * stormDarken, 0.93f * stormDarken, 0.96f, 1.0f);
                pressureColor = new Color(0.48f, 0.56f, 0.68f, 1.0f);
                baseIntensity = 0.65f + currentRainIntensity * 0.20f;
                hurricaneStrength = 0.18f + currentRainIntensity * 0.25f;
                pressureDistortion = 0.08f + currentRainIntensity * 0.12f;
            }
            else if (opmCompatibilityEnabled && planetName == "Sarnus")
            {
                // OPM SARNUS: Pale gold & warm ochre banded gas giant
                baseCloudColor = new Color(0.88f, 0.76f, 0.48f, 1.0f);
                pressureColor = new Color(0.62f, 0.48f, 0.22f, 1.0f);
                baseIntensity = 0.86f;
                hurricaneStrength = 0.40f + currentRainIntensity * 0.20f;
                pressureDistortion = 0.38f;
            }
            else if (opmCompatibilityEnabled && planetName == "Tekto")
            {
                // OPM TEKTO: Thick emerald-teal methane haze & 45s methane rain cycles
                baseCloudColor = new Color(0.24f, 0.76f, 0.62f, 1.0f);
                pressureColor = new Color(0.12f, 0.45f, 0.38f, 1.0f);
                baseIntensity = 0.92f;
                hurricaneStrength = 0.30f + currentRainIntensity * 0.35f;
                pressureDistortion = 0.55f;
            }
            else if (opmCompatibilityEnabled && planetName == "Urlum")
            {
                // OPM URLUM: Pale cyan/aquamarine ice giant
                baseCloudColor = new Color(0.48f, 0.86f, 0.90f, 1.0f);
                pressureColor = new Color(0.22f, 0.58f, 0.68f, 1.0f);
                baseIntensity = 0.82f;
                hurricaneStrength = 0.48f + currentRainIntensity * 0.22f;
                pressureDistortion = 0.32f;
            }
            else if (opmCompatibilityEnabled && planetName == "Neidon")
            {
                // OPM NEIDON: Deep royal-indigo ice giant with high-velocity hurricanes
                baseCloudColor = new Color(0.44f, 0.28f, 0.88f, 1.0f);
                pressureColor = new Color(0.22f, 0.10f, 0.58f, 1.0f);
                baseIntensity = 0.90f;
                hurricaneStrength = 0.82f + currentRainIntensity * 0.40f;
                pressureDistortion = 0.72f;
            }
            else if (opmCompatibilityEnabled && planetName == "Thatmo")
            {
                // OPM THATMO: Thin icy nitrogen-haze wisps
                baseCloudColor = new Color(0.76f, 0.82f, 0.90f, 1.0f);
                pressureColor = new Color(0.50f, 0.58f, 0.70f, 1.0f);
                baseIntensity = 0.28f;
                hurricaneStrength = 0.10f;
                pressureDistortion = 0.06f;
            }
            else
            {
                // Generic atmospheric worlds (Duna, Laythe, etc.)
                baseCloudColor = new Color(0.82f, 0.76f, 0.72f, 1.0f);
                pressureColor = new Color(0.55f, 0.44f, 0.38f, 1.0f);
                baseIntensity = 0.42f;
                hurricaneStrength = 0.15f;
                pressureDistortion = 0.12f;
            }

            lastPlanetName = planetName;

            cloudMaterial.SetColor(CloudColorId, baseCloudColor);
            cloudMaterial.SetColor(PressureColorId, pressureColor);
            cloudMaterial.SetFloat(IntensityModifierId, Mathf.Clamp(baseIntensity * opacityMultiplier, 0.0f, 1.5f));
            cloudMaterial.SetVector(OptimParamsId, new Vector4(
                maxSamples,
                cavityThreshold,
                extraFpsBoost ? 1.0f : 0.0f,
                swirlSpeed
            ));
            cloudMaterial.SetVector(StormParamsId, new Vector4(
                currentRainIntensity,
                hurricaneStrength,
                pressureDistortion,
                currentCycleSeconds / RAIN_CYCLE_DURATION
            ));
            cloudMaterial.SetVector(DeferredParamsId, new Vector4(
                deferredIntegrationEnabled ? 1.0f : 0.0f,
                0.85f,
                0.30f,
                1.05f
            ));
        }

        // Automatically detects Blackrack's Deferred Rendering mod and switches integration ON/OFF
        private void DetectDeferredRendering()
        {
            bool foundFolder = false;
            try
            {
                string deferredPath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/Deferred");
                foundFolder = Directory.Exists(deferredPath);
            }
            catch
            {
                foundFolder = false;
            }

            bool foundAssembly = false;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                string asmName = assemblies[i].GetName().Name;
                if (string.Equals(asmName, "Deferred", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(asmName, "DeferredRendering", StringComparison.OrdinalIgnoreCase))
                {
                    foundAssembly = true;
                    break;
                }
            }

            bool cameraDeferred = flightCamera != null && flightCamera.actualRenderingPath == RenderingPath.DeferredShading;
            bool detected = foundFolder || foundAssembly || cameraDeferred;

            if (detected != isDeferredDetected || !deferredDetectionCompleted)
            {
                isDeferredDetected = detected;
                deferredIntegrationEnabled = detected;
                Debug.Log(detected
                    ? "[LowEndVolumetricCloudsMod] Blackrack Deferred Rendering detected — native G-Buffer & depth integration switched ON automatically."
                    : "[LowEndVolumetricCloudsMod] Blackrack Deferred Rendering not detected — using stock forward CameraEvent.AfterSkybox.");
            }
            if (flightCamera != null)
            {
                deferredDetectionCompleted = true;
            }
        }

        // Automatically detects Outer Planets Mod (OPM) and switches compatibility ON/OFF
        private void DetectOuterPlanetsMod()
        {
            bool foundFolder = false;
            try
            {
                string opmPath = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/OPM");
                foundFolder = Directory.Exists(opmPath);
            }
            catch
            {
                foundFolder = false;
            }

            bool foundBodies = false;
            if (FlightGlobals.Bodies != null && FlightGlobals.Bodies.Count > 0)
            {
                for (int i = 0; i < FlightGlobals.Bodies.Count; i++)
                {
                    string bName = FlightGlobals.Bodies[i].name;
                    if (bName == "Sarnus" || bName == "Urlum" || bName == "Neidon" || bName == "Tekto")
                    {
                        foundBodies = true;
                        break;
                    }
                }
                opmDetectionCompleted = true;
            }

            bool detected = foundFolder || foundBodies;
            if (detected != isOpmDetected || !opmDetectionCompleted)
            {
                isOpmDetected = detected;
                opmCompatibilityEnabled = detected;
                Debug.Log(detected
                    ? "[LowEndVolumetricCloudsMod] Outer Planets Mod (OPM) detected — native OPM compatibility switched ON automatically."
                    : "[LowEndVolumetricCloudsMod] OPM not detected — native OPM compatibility switched OFF.");
            }
        }

        private void UpdateRainCycle(string planetName)
        {
            if (!rainCyclesEnabled)
            {
                currentRainIntensity = 0.0f;
                currentCycleSeconds = 0.0f;
                currentWeatherPhase = "Cycle Paused";
                return;
            }

            currentCycleSeconds = Time.time % RAIN_CYCLE_DURATION;
            float t = currentCycleSeconds / RAIN_CYCLE_DURATION; // 0..1 over 45 seconds

            if (t < 0.27f)
            {
                currentRainIntensity = Mathf.Lerp(0.0f, 0.10f, t / 0.27f);
                currentWeatherPhase = "Calm Skies";
            }
            else if (t < 0.49f)
            {
                currentRainIntensity = Mathf.Lerp(0.10f, 0.58f, (t - 0.27f) / 0.22f);
                if (planetName == "Eve") currentWeatherPhase = "Acid Squall Building";
                else if (planetName == "Tekto") currentWeatherPhase = "Methane Drizzle";
                else currentWeatherPhase = "Light Rain";
            }
            else if (t < 0.76f)
            {
                float peakPhase = Mathf.Sin(((t - 0.49f) / 0.27f) * Mathf.PI);
                currentRainIntensity = Mathf.Lerp(0.65f, 1.0f, peakPhase);
                if (planetName == "Eve") currentWeatherPhase = "PURPLE HURRICANE";
                else if (planetName == "Neidon") currentWeatherPhase = "ICE HURRICANE";
                else if (planetName == "Tekto") currentWeatherPhase = "Methane Storm";
                else currentWeatherPhase = "Heavy Storm & Rain";
            }
            else
            {
                currentRainIntensity = Mathf.Lerp(0.58f, 0.0f, (t - 0.76f) / 0.24f);
                currentWeatherPhase = "Storm Passing";
            }
        }

        void OnGUI()
        {
            if (!showSettingsWindow || guiBox == null || guiLabel == null || guiButton == null) return;

            windowRect.height = 438f + (isOpmDetected ? 24f : 0f) + (isDeferredDetected ? 24f : 0f);

            // Keep panel clamped cleanly inside screen bounds
            windowRect.x = Mathf.Clamp(windowRect.x, 12f, Mathf.Max(20f, Screen.width - windowRect.width - 12f));
            windowRect.y = Mathf.Clamp(windowRect.y, 48f, Mathf.Max(60f, Screen.height - windowRect.height - 12f));

            float x = windowRect.x + 14f;
            float w = windowRect.width - 28f;
            float y = windowRect.y + 12f;

            DrawBox(windowRect, "");
            DrawLabel(new Rect(x, y, w - 32f, 22f), "<b>Low-End Volumetric Clouds v1.1</b>");
            if (DrawButton(new Rect(windowRect.x + windowRect.width - 36f, y, 24f, 20f), "X"))
            {
                SetSettingsWindowVisible(false);
            }
            y += 26f;

            string bodyLabel = FlightGlobals.currentMainBody != null ? FlightGlobals.currentMainBody.name : "None";
            DrawLabel(new Rect(x, y, w, 20f), $"Body: {bodyLabel} | {currentWeatherPhase} ({currentCycleSeconds:F0}s/45s)");
            y += 20f;

            string opmStatus = isOpmDetected
                ? (opmCompatibilityEnabled ? "AUTO-ON" : "OFF")
                : "AUTO-OFF";
            string deferredStatus = isDeferredDetected
                ? (deferredIntegrationEnabled ? "AUTO-ON" : "OFF")
                : "AUTO-OFF";
            DrawLabel(new Rect(x, y, w, 20f), $"OPM: {opmStatus} | Deferred: {deferredStatus}");
            y += 24f;

            // 1. Resolution Downscale selector
            DrawLabel(new Rect(x, y, w, 18f), $"Resolution Downscale: {downscaleFactor}x");
            y += 20f;
            float btnW = (w - 18f) / 4f;
            int[] factors = new int[] { 1, 2, 4, 8 };
            for (int idx = 0; idx < factors.Length; idx++)
            {
                int factor = factors[idx];
                string caption = factor == downscaleFactor ? $"[{factor}x]" : $"{factor}x";
                if (DrawButton(new Rect(x + idx * (btnW + 6f), y, btnW, 22f), caption))
                {
                    downscaleFactor = factor;
                }
            }
            y += 30f;

            // 2. Raymarch Sample Steps slider
            DrawLabel(new Rect(x, y, w, 18f), $"Raymarch Steps: {maxSamples} layers");
            y += 18f;
            maxSamples = Mathf.RoundToInt(DrawSlider(new Rect(x, y, w, 18f), maxSamples, 4f, 32f));
            y += 24f;

            // 3. Cavity Skip Threshold slider
            DrawLabel(new Rect(x, y, w, 18f), $"Cavity Skip Threshold: {(cavityThreshold * 100f):F0}%");
            y += 18f;
            cavityThreshold = DrawSlider(new Rect(x, y, w, 18f), cavityThreshold, 0.04f, 0.50f);
            y += 24f;

            // 4. Hurricane / Swirl Speed slider
            DrawLabel(new Rect(x, y, w, 18f), $"Storm Swirl Speed: {swirlSpeed:F1}x");
            y += 18f;
            swirlSpeed = DrawSlider(new Rect(x, y, w, 18f), swirlSpeed, 0.1f, 2.5f);
            y += 24f;

            // 5. Cloud Opacity slider
            DrawLabel(new Rect(x, y, w, 18f), $"Cloud Density Multiplier: {(opacityMultiplier * 100f):F0}%");
            y += 18f;
            opacityMultiplier = DrawSlider(new Rect(x, y, w, 18f), opacityMultiplier, 0.2f, 1.5f);
            y += 28f;

            // 6. Toggles: Extra FPS Boost, 45s Rain Cycles, Auto-Detected Integrations
            extraFpsBoost = DrawToggle(new Rect(x, y, w, 22f), extraFpsBoost, " Extra FPS Boost (+15 FPS Interlacing & LOD)");
            y += 24f;

            rainCyclesEnabled = DrawToggle(new Rect(x, y, w, 22f), rainCyclesEnabled, " 45-Second Rain & Storm Cycles");
            y += 24f;

            if (isOpmDetected)
            {
                opmCompatibilityEnabled = DrawToggle(new Rect(x, y, w, 22f), opmCompatibilityEnabled, " Native OPM Compatibility (Sarnus/Urlum/Neidon/Tekto)");
                y += 24f;
            }

            if (isDeferredDetected)
            {
                deferredIntegrationEnabled = DrawToggle(new Rect(x, y, w, 22f), deferredIntegrationEnabled, " Blackrack Deferred Integration (G-Buffer + Depth)");
                y += 24f;
            }

            y += 4f;

            if (DrawButton(new Rect(x, y, w, 24f), "Reset to Low-End Recommended Defaults"))
            {
                downscaleFactor = 4;
                maxSamples = 12;
                cavityThreshold = 0.12f;
                swirlSpeed = 1.0f;
                opacityMultiplier = 1.0f;
                extraFpsBoost = true;
                rainCyclesEnabled = true;
                opmCompatibilityEnabled = isOpmDetected;
                deferredIntegrationEnabled = isDeferredDetected;
            }
            y += 28f;

            DrawLabel(new Rect(x, y, w, 20f), "Toggle anytime: Alt + 0 or Green Globe Toolbar Icon");
        }

        private void InitializeAssets()
        {
            string texturesDirectory = Path.Combine(KSPUtil.ApplicationRootPath, "GameData/LowEndVolumetricCloudsMod/Textures");
            string mapPath = Path.Combine(texturesDirectory, "weather_map.png");

            try
            {
                if (!Directory.Exists(texturesDirectory))
                {
                    Directory.CreateDirectory(texturesDirectory);
                }

                if (!File.Exists(mapPath))
                {
                    WeatherMapGenerator.GenerateDefaultMap(mapPath);
                }

                byte[] fileData = File.ReadAllBytes(mapPath);
                loadedWeatherMap = new Texture2D(2, 2, TextureFormat.RGB24, false);
                loadedWeatherMap.LoadImage(fileData);
                loadedWeatherMap.filterMode = FilterMode.Bilinear;
                loadedWeatherMap.wrapMode = TextureWrapMode.Repeat;

                toolbarIconTexture = CreateGreenCloudGlobeIcon();
                Debug.Log("[LowEndVolumetricCloudsMod] Weather map & toolbar globe icon initialized.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LowEndVolumetricCloudsMod] Critical error loading assets: {ex.Message}");
            }
        }

        // Procedurally paints a crisp 32x32 green cloud/globe icon for the stock KSP toolbar
        private Texture2D CreateGreenCloudGlobeIcon()
        {
            const int size = 32;
            Texture2D icon = new Texture2D(size, size, TextureFormat.ARGB32, false);
            Color[] pixels = new Color[size * size];

            float center = (size - 1) * 0.5f;
            float radius = 12.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - center;
                    float dy = y - center;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);

                    if (dist > radius)
                    {
                        pixels[x + y * size] = new Color(0f, 0f, 0f, 0f);
                        continue;
                    }

                    float shade = Mathf.Clamp01(1.0f - (dist / radius) * 0.55f + (dy - dx) * 0.02f);
                    Color globeColor = new Color(0.10f * shade, 0.72f * shade, 0.34f * shade, 1.0f);

                    if (dist > radius - 1.3f)
                    {
                        globeColor = new Color(0.18f, 0.92f, 0.48f, 1.0f);
                    }

                    float cloudBandA = Mathf.Abs(dy - 2.0f - Mathf.Sin(dx * 0.35f) * 2.0f);
                    float cloudBandB = Mathf.Abs(dy + 4.0f - Mathf.Cos(dx * 0.30f) * 1.5f);
                    if (cloudBandA < 2.2f || (cloudBandB < 1.7f && dx > -6f))
                    {
                        globeColor = Color.Lerp(globeColor, new Color(0.86f, 1.0f, 0.90f, 1.0f), 0.78f);
                    }

                    pixels[x + y * size] = globeColor;
                }
            }

            icon.SetPixels(pixels);
            icon.Apply();
            return icon;
        }

        private void HookToolbarEvents()
        {
            if (!isAppLauncherHooked)
            {
                GameEvents.onGUIApplicationLauncherReady.Add(OnGUIAppLauncherReady);
                isAppLauncherHooked = true;
            }

            if (ApplicationLauncher.Ready)
            {
                OnGUIAppLauncherReady();
            }
        }

        private void OnGUIAppLauncherReady()
        {
            if (toolbarButton != null || ApplicationLauncher.Instance == null) return;

            toolbarButton = ApplicationLauncher.Instance.AddModApplication(
                OnToolbarToggleOn,
                OnToolbarToggleOff,
                null,
                null,
                null,
                null,
                ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW,
                toolbarIconTexture
            );
        }

        private void OnToolbarToggleOn()
        {
            showSettingsWindow = true;
        }

        private void OnToolbarToggleOff()
        {
            showSettingsWindow = false;
        }

        private void ToggleSettingsWindow()
        {
            SetSettingsWindowVisible(!showSettingsWindow);
        }

        private void SetSettingsWindowVisible(bool visible)
        {
            showSettingsWindow = visible;
            if (toolbarButton != null)
            {
                if (visible)
                    toolbarButton.SetTrue(false);
                else
                    toolbarButton.SetFalse(false);
            }
        }

        private void TryInitializePipeline()
        {
            flightCamera = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (flightCamera == null)
            {
                flightCamera = Camera.main;
            }

            if (flightCamera == null) return;

            DetectDeferredRendering();

            Shader cloudShader = Shader.Find("Hidden/LowEndVolumetricCloud");
            if (cloudShader == null)
            {
                Debug.LogWarning("[LowEndVolumetricCloudsMod] Hidden/LowEndVolumetricCloud shader not found; using fallback.");
                cloudShader = Shader.Find("Sprites/Default");
            }

            cloudMaterial = new Material(cloudShader);
            if (loadedWeatherMap != null)
            {
                cloudMaterial.SetTexture(WeatherMapId, loadedWeatherMap);
            }

            int targetWidth = Mathf.Max(1, Screen.width / downscaleFactor);
            int targetHeight = Mathf.Max(1, Screen.height / downscaleFactor);
            RebuildCommandBuffer(targetWidth, targetHeight);

            isPipelineInitialized = true;
            Debug.Log("[LowEndVolumetricCloudsMod] v1.1.0 Graphics pipeline initialized!");
        }

        private void RebuildCommandBuffer(int targetWidth, int targetHeight)
        {
            if (flightCamera == null) return;

            if (cloudCommandBuffer != null)
            {
                flightCamera.RemoveCommandBuffer(activeCameraEvent, cloudCommandBuffer);
                cloudCommandBuffer.Release();
            }

            activeCameraEvent = deferredIntegrationEnabled ? CameraEvent.BeforeForwardAlpha : CameraEvent.AfterSkybox;

            cloudCommandBuffer = new CommandBuffer();
            cloudCommandBuffer.name = deferredIntegrationEnabled ? "LowEndVolumetricClouds_Deferred" : "LowEndVolumetricClouds";
            cloudCommandBuffer.GetTemporaryRT(LowResBufferId, targetWidth, targetHeight, 0, FilterMode.Bilinear);
            cloudCommandBuffer.Blit(BuiltinRenderTextureType.CurrentActive, LowResBufferId, cloudMaterial);
            cloudCommandBuffer.Blit(LowResBufferId, BuiltinRenderTextureType.CurrentActive);
            cloudCommandBuffer.ReleaseTemporaryRT(LowResBufferId);

            flightCamera.AddCommandBuffer(activeCameraEvent, cloudCommandBuffer);

            lastBufferWidth = targetWidth;
            lastBufferHeight = targetHeight;
            lastDownscaleFactor = downscaleFactor;
            lastDeferredState = deferredIntegrationEnabled;
        }

        private static void EnsureReflectionBindings()
        {
            if (reflectionInitialized) return;
            reflectionInitialized = true;

            Type inputType = FindUnityType("UnityEngine.Input, UnityEngine.InputLegacyModule", "UnityEngine.Input, UnityEngine", "UnityEngine.Input");
            if (inputType != null)
            {
                inputGetKey = inputType.GetMethod("GetKey", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(KeyCode) }, null);
                inputGetKeyDown = inputType.GetMethod("GetKeyDown", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(KeyCode) }, null);
            }

            Type guiType = FindUnityType("UnityEngine.GUI, UnityEngine.IMGUIModule", "UnityEngine.GUI, UnityEngine", "UnityEngine.GUI");
            if (guiType != null)
            {
                guiBox = guiType.GetMethod("Box", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Rect), typeof(string) }, null);
                guiLabel = guiType.GetMethod("Label", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Rect), typeof(string) }, null);
                guiButton = guiType.GetMethod("Button", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Rect), typeof(string) }, null);
                guiToggle = guiType.GetMethod("Toggle", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Rect), typeof(bool), typeof(string) }, null);
                guiHorizontalSlider = guiType.GetMethod("HorizontalSlider", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Rect), typeof(float), typeof(float), typeof(float) }, null);
                guiColorProp = guiType.GetProperty("color", BindingFlags.Public | BindingFlags.Static);
            }
        }

        private static Type FindUnityType(params string[] candidateNames)
        {
            for (int i = 0; i < candidateNames.Length; i++)
            {
                Type t = Type.GetType(candidateNames[i]);
                if (t != null) return t;
            }

            string shortName = candidateNames[candidateNames.Length - 1];
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type t = assemblies[i].GetType(shortName);
                if (t != null) return t;
            }
            return null;
        }

        private static bool IsAltZeroPressed()
        {
            if (inputGetKey == null || inputGetKeyDown == null) return false;
            bool altHeld = (bool)inputGetKey.Invoke(null, new object[] { KeyCode.LeftAlt }) ||
                           (bool)inputGetKey.Invoke(null, new object[] { KeyCode.RightAlt });
            if (!altHeld) return false;
            return (bool)inputGetKeyDown.Invoke(null, new object[] { KeyCode.Alpha0 }) ||
                   (bool)inputGetKeyDown.Invoke(null, new object[] { KeyCode.Keypad0 });
        }

        private static void DrawBox(Rect rect, string text)
        {
            if (guiColorProp != null) guiColorProp.SetValue(null, new Color(0.12f, 0.14f, 0.19f, 0.94f), null);
            guiBox.Invoke(null, new object[] { rect, text });
            if (guiColorProp != null) guiColorProp.SetValue(null, Color.white, null);
        }

        private static void DrawLabel(Rect rect, string text)
        {
            guiLabel.Invoke(null, new object[] { rect, text });
        }

        private static bool DrawButton(Rect rect, string text)
        {
            return (bool)guiButton.Invoke(null, new object[] { rect, text });
        }

        private static bool DrawToggle(Rect rect, bool value, string text)
        {
            return (bool)guiToggle.Invoke(null, new object[] { rect, value, text });
        }

        private static float DrawSlider(Rect rect, float value, float leftValue, float rightValue)
        {
            return (float)guiHorizontalSlider.Invoke(null, new object[] { rect, value, leftValue, rightValue });
        }

        void OnDestroy()
        {
            if (isAppLauncherHooked)
            {
                GameEvents.onGUIApplicationLauncherReady.Remove(OnGUIAppLauncherReady);
                isAppLauncherHooked = false;
            }

            if (toolbarButton != null && ApplicationLauncher.Instance != null)
            {
                ApplicationLauncher.Instance.RemoveModApplication(toolbarButton);
                toolbarButton = null;
            }

            if (flightCamera != null && cloudCommandBuffer != null)
            {
                flightCamera.RemoveCommandBuffer(activeCameraEvent, cloudCommandBuffer);
                cloudCommandBuffer.Release();
            }

            if (cloudMaterial != null) Destroy(cloudMaterial);
            if (loadedWeatherMap != null) Destroy(loadedWeatherMap);
            if (toolbarIconTexture != null) Destroy(toolbarIconTexture);

            Debug.Log("[LowEndVolumetricCloudsMod] Graphics pipeline shut down safely.");
        }
    }
}
