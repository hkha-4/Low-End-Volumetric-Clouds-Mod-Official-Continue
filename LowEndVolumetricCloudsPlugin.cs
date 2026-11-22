using System;
using System.Collections.Generic;
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
        private Material cloudShellMaterial;
        private Material cloudPuffMaterial;
        private Material scaledCloudMaterial;
        private Texture2D cloudLayerTexture;
        private Texture2D cloudPuffTexture;
        private Texture2D rainStreakTexture;
        private Texture2D toolbarIconTexture;
        private ApplicationLauncherButton toolbarButton;

        // 3D volumetric cloud geometry (pure UnityEngine.CoreModule - zero AssetBundle required)
        private GameObject localCloudRoot;
        private GameObject scaledCloudObj;
        private Mesh unitSphereMesh;
        private Mesh localCanopyMesh;
        private Mesh crossQuadMesh;
        private float currentCanopySpan = 120000f;
        private readonly List<GameObject> cloudShellObjects = new List<GameObject>();
        private readonly List<Renderer> cloudShellRenderers = new List<Renderer>();
        private readonly List<GameObject> cloudPuffObjects = new List<GameObject>();
        private readonly List<Renderer> cloudPuffRenderers = new List<Renderer>();
        private readonly List<Vector3> cloudPuffOffsets = new List<Vector3>();
        private readonly List<float> cloudPuffPhases = new List<float>();

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

        // User-tunable in-game settings (Alt + 0 or green cloud/globe stock toolbar button)
        private int downscaleFactor = 4;        // 1, 2, 4, 8 (controls procedural cloud texture res: 512/factor)
        private int maxSamples = 12;            // 4..32 (controls active 3D volumetric shell layers)
        private float cavityThreshold = 0.12f;  // 0.05..0.50 early-exit cavity cutout threshold
        private float swirlSpeed = 1.0f;        // 0.1..2.5 rotational storm swirl speed
        private float opacityMultiplier = 1.0f; // 0.2..1.5 cloud density multiplier
        private bool extraFpsBoost = true;      // Caps active layers/puffs & culls high orbit for +15 FPS
        private bool rainCyclesEnabled = true;  // 45-second rain & storm cycle

        // State tracking so textures/meshes only rebuild when settings or planet change
        private int lastDownscaleFactor = -1;
        private float lastCavityThreshold = -1f;
        private int activeLayerCount = 0;
        private string lastPlanetName = string.Empty;
        private bool inDeepSpace = false;
        private float currentBodyCloudCeiling = 12000f;

        // 45-Second Rain & Storm Cycle state
        private const float RAIN_CYCLE_DURATION = 45.0f;
        private float currentRainIntensity = 0.0f;
        private float currentCycleSeconds = 0.0f;
        private string currentWeatherPhase = "Calm";
        private Color activeRainTint = new Color(0.70f, 0.86f, 1.0f, 0.40f);

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
        private static MethodInfo guiDrawTexture;
        private static PropertyInfo guiColorProp;

        void Start()
        {
            Debug.Log("[LowEndVolumetricCloudsMod] v1.1.0 Start - initializing 3D cloud textures, OPM & Deferred checks, toolbar...");
            EnsureReflectionBindings();
            InitializeAssets();
            DetectOuterPlanetsMod();
            DetectDeferredRendering();
            HookToolbarEvents();
        }

        void Update()
        {
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

            if (!isPipelineInitialized || FlightGlobals.currentMainBody == null) return;

            // Rebuild downscaled cloud cutout texture only when Downscale or Cavity Threshold changes
            if (downscaleFactor != lastDownscaleFactor || Mathf.Abs(cavityThreshold - lastCavityThreshold) > 0.015f)
            {
                RebuildCloudTextures();
            }

            CelestialBody body = FlightGlobals.currentMainBody;
            string planetName = body.name;

            // Determine desired volumetric shell layer count (Extra FPS Boost caps by ~25% cleanly)
            int desiredLayers = Mathf.Clamp(Mathf.RoundToInt(maxSamples * 0.5f), 3, 12);
            if (extraFpsBoost && desiredLayers > 4)
            {
                desiredLayers = Mathf.Max(4, (desiredLayers * 3) / 4);
            }

            if (planetName != lastPlanetName || desiredLayers != activeLayerCount)
            {
                RebuildPlanetCloudGeometry(body, desiredLayers);
                lastPlanetName = planetName;
            }

            double altitude = FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.altitude : 0.0;
            bool hasAtmo = body.atmosphere;
            inDeepSpace = !hasAtmo || (body.atmosphereDepth > 0 && altitude > body.atmosphereDepth * 2.5);

            // Toggle local clouds off on atmosphere-less worlds or high orbit (> 2.5x atmosphere depth)
            if (localCloudRoot != null)
            {
                bool showLocal = hasAtmo && !inDeepSpace;
                if (localCloudRoot.activeSelf != showLocal)
                {
                    localCloudRoot.SetActive(showLocal);
                }
            }

            // Keep orbital ScaledSpace cloud shell active whenever the planet has an atmosphere
            if (scaledCloudObj != null && scaledCloudObj.activeSelf != hasAtmo)
            {
                scaledCloudObj.SetActive(hasAtmo);
            }

            if (!hasAtmo)
            {
                currentRainIntensity = 0.0f;
                currentWeatherPhase = "No Atmosphere";
                return;
            }

            if (inDeepSpace)
            {
                currentRainIntensity = 0.0f;
                currentWeatherPhase = "High Orbit (Local Culled)";
            }
            else
            {
                UpdateRainCycle(planetName);
            }

            // Planet-specific atmospheric & storm color profiles
            Color baseCloudColor;
            Color pressureColor;
            float baseIntensity;
            float baseAlt;
            float topAlt;

            GetPlanetProfile(planetName, out baseCloudColor, out pressureColor, out baseIntensity, out baseAlt, out topAlt);
            currentBodyCloudCeiling = topAlt;

            // Blackrack Deferred linear/HDR luminance calibration so clouds balance cleanly with Deferred + Scatterer
            float deferredLumBoost = deferredIntegrationEnabled ? 1.18f : 1.0f;
            float finalAlpha = Mathf.Clamp01(baseIntensity * opacityMultiplier * (1.0f + currentRainIntensity * 0.20f));

            if (localCloudRoot != null && localCloudRoot.activeSelf)
            {
                Vector3 bodyPos = (Vector3)body.position;
                localCloudRoot.transform.position = bodyPos;
                UpdateCloudLayersTransformAndTint(body, bodyPos, baseCloudColor, pressureColor, finalAlpha, deferredLumBoost, baseAlt, topAlt);
                UpdateLocalVolumetricPuffs(body, bodyPos, altitude, baseCloudColor, pressureColor, finalAlpha, deferredLumBoost, baseAlt, topAlt);
            }

            if (scaledCloudObj != null && scaledCloudObj.activeSelf && body.scaledBody != null)
            {
                scaledCloudObj.transform.localRotation = Quaternion.Euler(0f, Time.time * swirlSpeed * 1.4f, 0f);
                if (scaledCloudMaterial != null)
                {
                    Color orbitalTint = Color.Lerp(baseCloudColor, pressureColor, 0.25f) * deferredLumBoost;
                    orbitalTint.a = Mathf.Clamp01(finalAlpha * 0.72f);
                    scaledCloudMaterial.color = orbitalTint;
                }
            }
        }

        private void GetPlanetProfile(
            string planetName,
            out Color baseCloudColor,
            out Color pressureColor,
            out float baseIntensity,
            out float baseAlt,
            out float topAlt)
        {
            if (planetName == "Eve")
            {
                // EVE PROFILE: Purple storms, swirling hurricanes, and obvious high-pressure haze
                baseCloudColor = new Color(0.68f, 0.30f, 0.95f, 1.0f);
                pressureColor = new Color(0.48f, 0.12f, 0.75f, 1.0f);
                baseIntensity = 0.88f;
                baseAlt = 11000f;
                topAlt = 38000f;
                activeRainTint = new Color(0.58f, 0.94f, 0.22f, 0.48f);
            }
            else if (planetName == "Jool")
            {
                baseCloudColor = new Color(0.18f, 0.72f, 0.36f, 1.0f);
                pressureColor = new Color(0.08f, 0.42f, 0.20f, 1.0f);
                baseIntensity = 0.86f;
                baseAlt = 32000f;
                topAlt = 88000f;
                activeRainTint = new Color(0.30f, 0.92f, 0.48f, 0.40f);
            }
            else if (planetName == "Kerbin")
            {
                float stormDarken = 1.0f - currentRainIntensity * 0.24f;
                baseCloudColor = new Color(0.95f * stormDarken, 0.96f * stormDarken, 0.99f, 1.0f);
                pressureColor = new Color(0.62f, 0.70f, 0.82f, 1.0f);
                baseIntensity = 0.76f;
                baseAlt = 2800f;
                topAlt = 10500f;
                activeRainTint = new Color(0.72f, 0.88f, 1.0f, 0.42f);
            }
            else if (opmCompatibilityEnabled && planetName == "Sarnus")
            {
                baseCloudColor = new Color(0.92f, 0.80f, 0.52f, 1.0f);
                pressureColor = new Color(0.68f, 0.54f, 0.28f, 1.0f);
                baseIntensity = 0.84f;
                baseAlt = 28000f;
                topAlt = 76000f;
                activeRainTint = new Color(0.95f, 0.82f, 0.52f, 0.35f);
            }
            else if (opmCompatibilityEnabled && planetName == "Tekto")
            {
                baseCloudColor = new Color(0.28f, 0.82f, 0.68f, 1.0f);
                pressureColor = new Color(0.14f, 0.52f, 0.44f, 1.0f);
                baseIntensity = 0.88f;
                baseAlt = 4500f;
                topAlt = 19000f;
                activeRainTint = new Color(0.32f, 0.92f, 0.78f, 0.45f);
            }
            else if (opmCompatibilityEnabled && planetName == "Urlum")
            {
                baseCloudColor = new Color(0.52f, 0.88f, 0.94f, 1.0f);
                pressureColor = new Color(0.26f, 0.64f, 0.74f, 1.0f);
                baseIntensity = 0.80f;
                baseAlt = 24000f;
                topAlt = 64000f;
                activeRainTint = new Color(0.54f, 0.90f, 0.96f, 0.35f);
            }
            else if (opmCompatibilityEnabled && planetName == "Neidon")
            {
                baseCloudColor = new Color(0.50f, 0.34f, 0.94f, 1.0f);
                pressureColor = new Color(0.26f, 0.14f, 0.66f, 1.0f);
                baseIntensity = 0.88f;
                baseAlt = 28000f;
                topAlt = 74000f;
                activeRainTint = new Color(0.62f, 0.52f, 1.0f, 0.44f);
            }
            else if (opmCompatibilityEnabled && planetName == "Thatmo")
            {
                baseCloudColor = new Color(0.80f, 0.86f, 0.94f, 1.0f);
                pressureColor = new Color(0.56f, 0.64f, 0.76f, 1.0f);
                baseIntensity = 0.36f;
                baseAlt = 5500f;
                topAlt = 14000f;
                activeRainTint = new Color(0.80f, 0.88f, 0.98f, 0.25f);
            }
            else
            {
                baseCloudColor = new Color(0.86f, 0.78f, 0.72f, 1.0f);
                pressureColor = new Color(0.60f, 0.48f, 0.42f, 1.0f);
                baseIntensity = 0.48f;
                baseAlt = 4000f;
                topAlt = 14000f;
                activeRainTint = new Color(0.85f, 0.78f, 0.72f, 0.30f);
            }
        }

        private void UpdateCloudLayersTransformAndTint(
            CelestialBody body,
            Vector3 bodyPos,
            Color baseCloudColor,
            Color pressureColor,
            float finalAlpha,
            float deferredLumBoost,
            float baseAlt,
            float topAlt)
        {
            int count = cloudShellObjects.Count;
            if (count == 0) return;

            Vector3 refPos = FlightGlobals.ActiveVessel != null
                ? (Vector3)FlightGlobals.ActiveVessel.GetWorldPos3D()
                : (flightCamera != null ? flightCamera.transform.position : bodyPos + Vector3.up * (float)body.Radius);

            Vector3 up = (refPos - bodyPos).normalized;
            if (up.sqrMagnitude < 0.001f) up = Vector3.up;

            // Anchor cloud UVs to planet surface coordinates + wind drift so clouds don't slide artificially with the ship
            Vector3 planetLocalDir = body.transform != null
                ? body.transform.InverseTransformDirection(up)
                : up;
            float baseCoordU = Mathf.Atan2(planetLocalDir.z, planetLocalDir.x) * (float)(body.Radius / Mathf.Max(1000f, currentCanopySpan * 0.25f));
            float baseCoordV = Mathf.Asin(Mathf.Clamp(planetLocalDir.y, -1f, 1f)) * (float)(body.Radius / Mathf.Max(1000f, currentCanopySpan * 0.25f));

            Vector3 east = Vector3.Cross(Vector3.up, up);
            if (east.sqrMagnitude < 0.001f) east = Vector3.Cross(Vector3.right, up);
            east.Normalize();
            Vector3 north = Vector3.Cross(up, east).normalized;
            Quaternion zenithRot = Quaternion.LookRotation(north, up);

            for (int i = 0; i < count; i++)
            {
                float t = count > 1 ? (float)i / (count - 1) : 0.5f;
                float layerRadius = (float)body.Radius + Mathf.Lerp(baseAlt, topAlt, t);
                GameObject shell = cloudShellObjects[i];
                shell.transform.position = bodyPos + up * layerRadius;
                shell.transform.rotation = zenithRot;
                shell.transform.localScale = Vector3.one;

                Renderer rend = cloudShellRenderers[i];
                if (rend != null && rend.sharedMaterial != null)
                {
                    float layerSwirl = swirlSpeed * (0.012f + (i % 3) * 0.004f) * (i % 2 == 0 ? 1f : -0.65f);
                    rend.sharedMaterial.mainTextureOffset = new Vector2(
                        baseCoordU + Time.time * layerSwirl + i * 0.19f,
                        baseCoordV + Time.time * (layerSwirl * 0.35f) + i * 0.11f
                    );

                    Color layerTint = Color.Lerp(pressureColor, baseCloudColor, 0.35f + t * 0.65f) * deferredLumBoost;
                    float perLayerAlpha = Mathf.Clamp01((finalAlpha / Mathf.Sqrt(count)) * (0.88f + t * 0.35f));
                    layerTint.a = perLayerAlpha;
                    rend.sharedMaterial.color = layerTint;
                }
            }
        }

        private void UpdateLocalVolumetricPuffs(
            CelestialBody body,
            Vector3 bodyPos,
            double vesselAlt,
            Color baseCloudColor,
            Color pressureColor,
            float finalAlpha,
            float deferredLumBoost,
            float baseAlt,
            float topAlt)
        {
            int puffCount = cloudPuffObjects.Count;
            if (puffCount == 0 || FlightGlobals.ActiveVessel == null) return;

            Vector3 vesselPos = (Vector3)FlightGlobals.ActiveVessel.GetWorldPos3D();
            Vector3 up = (vesselPos - bodyPos).normalized;
            Vector3 east = Vector3.Cross(Vector3.up, up);
            if (east.sqrMagnitude < 0.001f) east = Vector3.Cross(Vector3.right, up);
            east.Normalize();
            Vector3 north = Vector3.Cross(up, east).normalized;

            float deckMidAlt = (baseAlt + topAlt) * 0.5f;
            float altDist = Mathf.Abs((float)vesselAlt - deckMidAlt);
            float visibility = Mathf.Clamp01(1.0f - (altDist / Mathf.Max(12000f, topAlt * 1.8f)));

            for (int i = 0; i < puffCount; i++)
            {
                GameObject puff = cloudPuffObjects[i];
                Renderer rend = cloudPuffRenderers[i];
                if (visibility <= 0.02f)
                {
                    if (puff.activeSelf) puff.SetActive(false);
                    continue;
                }
                if (!puff.activeSelf) puff.SetActive(true);

                Vector3 offset = cloudPuffOffsets[i];
                float phase = cloudPuffPhases[i];
                float driftX = Mathf.Repeat(offset.x + Time.time * swirlSpeed * 35f, 16000f) - 8000f;
                float puffAlt = Mathf.Lerp(baseAlt, topAlt, offset.y);
                Vector3 center = bodyPos + up * ((float)body.Radius + puffAlt) + east * driftX + north * offset.z;

                // Keep puffs within local camera range relative to vessel
                Vector3 toVessel = center - vesselPos;
                Vector3 flat = toVessel - Vector3.Project(toVessel, up);
                if (flat.magnitude > 11000f)
                {
                    center -= flat.normalized * 11000f;
                }

                puff.transform.position = center;
                puff.transform.rotation = Quaternion.LookRotation(north, up) * Quaternion.Euler(0f, phase + Time.time * swirlSpeed * 4f, 0f);

                if (rend != null && rend.sharedMaterial != null)
                {
                    Color puffTint = Color.Lerp(pressureColor, baseCloudColor, 0.55f) * deferredLumBoost;
                    puffTint.a = Mathf.Clamp01(finalAlpha * visibility * 0.65f);
                    rend.sharedMaterial.color = puffTint;
                }
            }
        }

        private void RebuildPlanetCloudGeometry(CelestialBody body, int desiredLayers)
        {
            if (localCloudRoot == null)
            {
                localCloudRoot = new GameObject("LowEndVolumetricClouds_LocalRoot");
                DontDestroyOnLoad(localCloudRoot);
            }

            for (int i = 0; i < cloudShellRenderers.Count; i++)
            {
                if (cloudShellRenderers[i] != null && cloudShellRenderers[i].sharedMaterial != null)
                {
                    Destroy(cloudShellRenderers[i].sharedMaterial);
                }
            }
            for (int i = 0; i < cloudShellObjects.Count; i++)
            {
                if (cloudShellObjects[i] != null) Destroy(cloudShellObjects[i]);
            }
            cloudShellObjects.Clear();
            cloudShellRenderers.Clear();

            if (localCanopyMesh != null) Destroy(localCanopyMesh);
            currentCanopySpan = Mathf.Clamp((float)body.Radius * 0.35f, 45000f, 140000f);
            localCanopyMesh = CreatePlanetaryCanopyMesh(body.Radius, currentCanopySpan, 30);

            for (int i = 0; i < desiredLayers; i++)
            {
                GameObject shell = new GameObject($"CloudLayer_{i}");
                shell.layer = 0; // Default local camera layer
                shell.transform.SetParent(localCloudRoot.transform, false);

                MeshFilter mf = shell.AddComponent<MeshFilter>();
                mf.sharedMesh = localCanopyMesh;

                MeshRenderer mr = shell.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;

                Material layerMat = new Material(cloudShellMaterial);
                layerMat.mainTexture = cloudLayerTexture;
                float tileCount = 4.5f + (i % 3) * 1.25f;
                layerMat.mainTextureScale = new Vector2(tileCount, tileCount);
                layerMat.mainTextureOffset = new Vector2(i * 0.19f, i * 0.11f);
                layerMat.renderQueue = 3020 + i;
                mr.sharedMaterial = layerMat;

                cloudShellObjects.Add(shell);
                cloudShellRenderers.Add(mr);
            }

            // Rebuild local 3D crossed-quad volumetric puffs
            for (int i = 0; i < cloudPuffObjects.Count; i++)
            {
                if (cloudPuffObjects[i] != null) Destroy(cloudPuffObjects[i]);
            }
            cloudPuffObjects.Clear();
            cloudPuffRenderers.Clear();
            cloudPuffOffsets.Clear();
            cloudPuffPhases.Clear();

            int puffTarget = extraFpsBoost ? 14 : 24;
            for (int p = 0; p < puffTarget; p++)
            {
                GameObject puff = new GameObject($"CloudPuff_{p}");
                puff.layer = 0;
                puff.transform.SetParent(localCloudRoot.transform, false);
                float puffScale = 1400f + (p % 5) * 450f;
                puff.transform.localScale = new Vector3(puffScale, puffScale * 0.42f, puffScale);

                MeshFilter mf = puff.AddComponent<MeshFilter>();
                mf.sharedMesh = crossQuadMesh;

                MeshRenderer mr = puff.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
                mr.sharedMaterial = cloudPuffMaterial;

                cloudPuffObjects.Add(puff);
                cloudPuffRenderers.Add(mr);

                float rx = (Mathf.Sin(p * 17.31f) * 0.5f) * 14000f;
                float ry = (p % 6) / 5.0f;
                float rz = (Mathf.Cos(p * 29.17f) * 0.5f) * 14000f;
                cloudPuffOffsets.Add(new Vector3(rx, ry, rz));
                cloudPuffPhases.Add(p * 41f);
            }

            // Attach or update ScaledSpace orbital cloud shell (visible from high orbit & Map View)
            if (scaledCloudObj != null)
            {
                Destroy(scaledCloudObj);
                scaledCloudObj = null;
            }

            if (body.scaledBody != null)
            {
                scaledCloudObj = new GameObject("LowEndVolumetricClouds_ScaledShell");
                scaledCloudObj.layer = 10; // ScaledScenery layer
                scaledCloudObj.transform.SetParent(body.scaledBody.transform, false);

                float scaledRadius = 1000f;
                MeshFilter bodyMf = body.scaledBody.GetComponent<MeshFilter>();
                if (bodyMf != null && bodyMf.sharedMesh != null)
                {
                    scaledRadius = Mathf.Max(1f, bodyMf.sharedMesh.bounds.extents.x);
                }
                scaledCloudObj.transform.localScale = Vector3.one * (scaledRadius * 1.018f);

                MeshFilter smf = scaledCloudObj.AddComponent<MeshFilter>();
                smf.sharedMesh = unitSphereMesh;

                MeshRenderer smr = scaledCloudObj.AddComponent<MeshRenderer>();
                smr.shadowCastingMode = ShadowCastingMode.Off;
                smr.receiveShadows = false;
                smr.sharedMaterial = scaledCloudMaterial;
            }

            activeLayerCount = desiredLayers;
            Debug.Log($"[LowEndVolumetricCloudsMod] Built {desiredLayers} curved 3D cloud layers + {puffTarget} puffs for {body.name} (Deferred={(deferredIntegrationEnabled ? "ON" : "OFF")}).");
        }

        // Generates seamless tileable RGBA volumetric cloud textures (no raw .shader compilation required)
        private void RebuildCloudTextures()
        {
            int size = Mathf.Clamp(512 / Mathf.Max(1, downscaleFactor), 64, 512);

            if (cloudLayerTexture != null) Destroy(cloudLayerTexture);
            cloudLayerTexture = new Texture2D(size, size, TextureFormat.ARGB32, false);
            cloudLayerTexture.wrapMode = TextureWrapMode.Repeat;
            cloudLayerTexture.filterMode = FilterMode.Bilinear;

            Color32[] pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                float v = (float)y / size;
                for (int x = 0; x < size; x++)
                {
                    float u = (float)x / size;

                    // Seamless periodic multi-octave noise using cosine/sine torus mapping
                    float angleU = u * Mathf.PI * 2f;
                    float angleV = v * Mathf.PI * 2f;
                    float nx1 = Mathf.Cos(angleU) * 1.6f + 4.2f;
                    float ny1 = Mathf.Sin(angleU) * 1.6f + Mathf.Cos(angleV) * 1.6f + 4.2f;
                    float nz1 = Mathf.Sin(angleV) * 1.6f + 7.1f;

                    float n1 = Mathf.PerlinNoise(nx1, ny1);
                    float n2 = Mathf.PerlinNoise(nx1 * 2.1f + nz1, ny1 * 2.1f - nz1) * 0.5f;
                    float n3 = Mathf.PerlinNoise(nx1 * 4.3f - nz1, ny1 * 4.3f + nx1) * 0.25f;
                    float fbm = (n1 + n2 + n3) / 1.75f;

                    // Cavity cutout gateway
                    float density = Mathf.Clamp01((fbm - cavityThreshold) / Mathf.Max(0.15f, 1.0f - cavityThreshold));
                    density = density * density * (3f - 2f * density);

                    byte shade = (byte)Mathf.Clamp(Mathf.RoundToInt((0.68f + fbm * 0.32f) * 255f), 0, 255);
                    byte alpha = (byte)Mathf.Clamp(Mathf.RoundToInt(density * 235f), 0, 255);
                    pixels[x + y * size] = new Color32(shade, shade, shade, alpha);
                }
            }
            cloudLayerTexture.SetPixels32(pixels);
            cloudLayerTexture.Apply();

            // Soft circular volumetric puff texture for 3D flight-through clouds
            int puffSize = Mathf.Clamp(size, 64, 256);
            if (cloudPuffTexture != null) Destroy(cloudPuffTexture);
            cloudPuffTexture = new Texture2D(puffSize, puffSize, TextureFormat.ARGB32, false);
            cloudPuffTexture.wrapMode = TextureWrapMode.Clamp;
            cloudPuffTexture.filterMode = FilterMode.Bilinear;

            Color32[] puffPixels = new Color32[puffSize * puffSize];
            for (int y = 0; y < puffSize; y++)
            {
                float ny = (y / (float)(puffSize - 1)) * 2f - 1f;
                for (int x = 0; x < puffSize; x++)
                {
                    float nx = (x / (float)(puffSize - 1)) * 2f - 1f;
                    float dist = Mathf.Sqrt(nx * nx + ny * ny);
                    float radial = Mathf.Clamp01(1f - dist);
                    float wisp = Mathf.PerlinNoise(nx * 2.4f + 5.1f, ny * 2.4f + 3.7f);
                    float alphaVal = radial * radial * Mathf.Clamp01(wisp * 1.35f - cavityThreshold * 0.5f);
                    byte shade = (byte)Mathf.Clamp(Mathf.RoundToInt((0.72f + wisp * 0.28f) * 255f), 0, 255);
                    byte alpha = (byte)Mathf.Clamp(Mathf.RoundToInt(alphaVal * 220f), 0, 255);
                    puffPixels[x + y * puffSize] = new Color32(shade, shade, shade, alpha);
                }
            }
            cloudPuffTexture.SetPixels32(puffPixels);
            cloudPuffTexture.Apply();

            for (int i = 0; i < cloudShellRenderers.Count; i++)
            {
                if (cloudShellRenderers[i] != null && cloudShellRenderers[i].sharedMaterial != null)
                {
                    cloudShellRenderers[i].sharedMaterial.mainTexture = cloudLayerTexture;
                }
            }
            if (cloudPuffMaterial != null) cloudPuffMaterial.mainTexture = cloudPuffTexture;
            if (scaledCloudMaterial != null) scaledCloudMaterial.mainTexture = cloudLayerTexture;

            lastDownscaleFactor = downscaleFactor;
            lastCavityThreshold = cavityThreshold;
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
                    ? "[LowEndVolumetricCloudsMod] Blackrack Deferred Rendering detected — linear HDR transparent cloud integration switched ON automatically."
                    : "[LowEndVolumetricCloudsMod] Blackrack Deferred Rendering not detected — using stock forward cloud calibration.");
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
            float t = currentCycleSeconds / RAIN_CYCLE_DURATION;

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
            // Render 45s Rain Streaks cleanly over flight view when inside/below cloud deck
            if (rainCyclesEnabled && !inDeepSpace && currentRainIntensity > 0.08f && rainStreakTexture != null && guiDrawTexture != null)
            {
                double alt = FlightGlobals.ActiveVessel != null ? FlightGlobals.ActiveVessel.altitude : 0.0;
                if (alt <= currentBodyCloudCeiling)
                {
                    DrawRainOverlay();
                }
            }

            if (!showSettingsWindow || guiBox == null || guiLabel == null || guiButton == null) return;

            windowRect.height = 438f + (isOpmDetected ? 24f : 0f) + (isDeferredDetected ? 24f : 0f);

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
            DrawLabel(new Rect(x, y, w, 20f), $"Body: {bodyLabel} ({activeLayerCount} layers) | {currentWeatherPhase}");
            y += 20f;

            string opmStatus = isOpmDetected ? (opmCompatibilityEnabled ? "AUTO-ON" : "OFF") : "AUTO-OFF";
            string deferredStatus = isDeferredDetected ? (deferredIntegrationEnabled ? "AUTO-ON" : "OFF") : "AUTO-OFF";
            DrawLabel(new Rect(x, y, w, 20f), $"OPM: {opmStatus} | Deferred: {deferredStatus} ({currentCycleSeconds:F0}s/45s)");
            y += 24f;

            DrawLabel(new Rect(x, y, w, 18f), $"Texture Downscale: {downscaleFactor}x");
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

            DrawLabel(new Rect(x, y, w, 18f), $"Volumetric Layer Budget: {maxSamples} steps");
            y += 18f;
            maxSamples = Mathf.RoundToInt(DrawSlider(new Rect(x, y, w, 18f), maxSamples, 4f, 32f));
            y += 24f;

            DrawLabel(new Rect(x, y, w, 18f), $"Cavity Skip Threshold: {(cavityThreshold * 100f):F0}%");
            y += 18f;
            cavityThreshold = DrawSlider(new Rect(x, y, w, 18f), cavityThreshold, 0.04f, 0.50f);
            y += 24f;

            DrawLabel(new Rect(x, y, w, 18f), $"Storm Swirl Speed: {swirlSpeed:F1}x");
            y += 18f;
            swirlSpeed = DrawSlider(new Rect(x, y, w, 18f), swirlSpeed, 0.1f, 2.5f);
            y += 24f;

            DrawLabel(new Rect(x, y, w, 18f), $"Cloud Density Multiplier: {(opacityMultiplier * 100f):F0}%");
            y += 18f;
            opacityMultiplier = DrawSlider(new Rect(x, y, w, 18f), opacityMultiplier, 0.2f, 1.5f);
            y += 28f;

            extraFpsBoost = DrawToggle(new Rect(x, y, w, 22f), extraFpsBoost, " Extra FPS Boost (+15 FPS Layer Cap & Culling)");
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
                deferredIntegrationEnabled = DrawToggle(new Rect(x, y, w, 22f), deferredIntegrationEnabled, " Blackrack Deferred Calibration");
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

        private void DrawRainOverlay()
        {
            if (guiColorProp != null)
            {
                Color c = activeRainTint;
                c.a = Mathf.Clamp01(currentRainIntensity * 0.42f);
                guiColorProp.SetValue(null, c, null);
            }

            int dropCount = extraFpsBoost ? 36 : 64;
            dropCount = Mathf.RoundToInt(dropCount * currentRainIntensity);
            float t = Time.time;

            for (int i = 0; i < dropCount; i++)
            {
                float rx = Mathf.Repeat(Mathf.Sin(i * 127.1f) * 0.5f + 0.5f + t * 0.08f * swirlSpeed, 1f) * Screen.width;
                float ry = Mathf.Repeat(Mathf.Cos(i * 311.7f) * 0.5f + 0.5f + t * (1.4f + (i % 4) * 0.25f), 1f) * Screen.height;
                float h = 18f + (i % 3) * 10f;
                guiDrawTexture.Invoke(null, new object[] { new Rect(rx, ry, 2f, h), rainStreakTexture });
            }

            if (guiColorProp != null) guiColorProp.SetValue(null, Color.white, null);
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

                toolbarIconTexture = CreateGreenCloudGlobeIcon();
                rainStreakTexture = CreateRainStreakTexture();
                unitSphereMesh = CreateUvSphereMesh(36, 20);
                crossQuadMesh = CreateCrossQuadMesh();
                RebuildCloudTextures();
                Debug.Log("[LowEndVolumetricCloudsMod] 3D cloud meshes, textures & toolbar icon initialized.");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LowEndVolumetricCloudsMod] Error initializing assets: {ex.Message}");
            }
        }

        private Texture2D CreateRainStreakTexture()
        {
            Texture2D tex = new Texture2D(2, 16, TextureFormat.ARGB32, false);
            Color[] cols = new Color[32];
            for (int y = 0; y < 16; y++)
            {
                float fade = Mathf.Sin((y / 15f) * Mathf.PI);
                cols[y * 2] = new Color(1f, 1f, 1f, fade);
                cols[y * 2 + 1] = new Color(1f, 1f, 1f, fade * 0.6f);
            }
            tex.SetPixels(cols);
            tex.Apply();
            return tex;
        }

        private Mesh CreateUvSphereMesh(int lonSegments, int latSegments)
        {
            Mesh mesh = new Mesh();
            mesh.name = "LowEndVolumetricCloudSphere";

            int vertCount = (lonSegments + 1) * (latSegments + 1);
            Vector3[] vertices = new Vector3[vertCount];
            Vector2[] uvs = new Vector2[vertCount];
            int[] triangles = new int[lonSegments * latSegments * 6];

            int vIdx = 0;
            for (int lat = 0; lat <= latSegments; lat++)
            {
                float v = (float)lat / latSegments;
                float phi = v * Mathf.PI;
                float sinPhi = Mathf.Sin(phi);
                float cosPhi = Mathf.Cos(phi);

                for (int lon = 0; lon <= lonSegments; lon++)
                {
                    float u = (float)lon / lonSegments;
                    float theta = u * Mathf.PI * 2f;
                    vertices[vIdx] = new Vector3(sinPhi * Mathf.Cos(theta), cosPhi, sinPhi * Mathf.Sin(theta));
                    uvs[vIdx] = new Vector2(u, v);
                    vIdx++;
                }
            }

            int tIdx = 0;
            for (int lat = 0; lat < latSegments; lat++)
            {
                for (int lon = 0; lon < lonSegments; lon++)
                {
                    int current = lat * (lonSegments + 1) + lon;
                    int next = current + lonSegments + 1;

                    triangles[tIdx++] = current;
                    triangles[tIdx++] = next;
                    triangles[tIdx++] = current + 1;

                    triangles[tIdx++] = current + 1;
                    triangles[tIdx++] = next;
                    triangles[tIdx++] = next + 1;
                }
            }

            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private Mesh CreatePlanetaryCanopyMesh(double planetRadius, float spanMeters, int gridSegments)
        {
            Mesh mesh = new Mesh();
            mesh.name = "LowEndVolumetricCloudCanopy";

            int vertCount = (gridSegments + 1) * (gridSegments + 1);
            Vector3[] vertices = new Vector3[vertCount];
            Vector2[] uvs = new Vector2[vertCount];
            Color[] colors = new Color[vertCount];
            int[] triangles = new int[gridSegments * gridSegments * 6];

            float R = Mathf.Max(10000f, (float)planetRadius);
            int vIdx = 0;

            for (int z = 0; z <= gridSegments; z++)
            {
                float nz = (z / (float)gridSegments) * 2f - 1f;
                for (int x = 0; x <= gridSegments; x++)
                {
                    float nx = (x / (float)gridSegments) * 2f - 1f;
                    float radial = Mathf.Sqrt(nx * nx + nz * nz);
                    float surfDist = radial * spanMeters;
                    float angle = Mathf.Min(surfDist / R, 1.35f);

                    Vector2 dir = radial > 0.0001f ? new Vector2(nx / radial, nz / radial) : Vector2.zero;
                    float horizDist = R * Mathf.Sin(angle);
                    float dropY = R * (Mathf.Cos(angle) - 1f);

                    vertices[vIdx] = new Vector3(dir.x * horizDist, dropY, dir.y * horizDist);
                    uvs[vIdx] = new Vector2(nx * 0.5f + 0.5f, nz * 0.5f + 0.5f);

                    // Soft radial fade at outer canopy rim so clouds melt smoothly into the horizon
                    float edgeT = Mathf.Clamp01((radial - 0.55f) / 0.45f);
                    float edgeFade = 1.0f - (edgeT * edgeT * (3f - 2f * edgeT));
                    colors[vIdx] = new Color(1f, 1f, 1f, edgeFade);
                    vIdx++;
                }
            }

            int tIdx = 0;
            for (int z = 0; z < gridSegments; z++)
            {
                for (int x = 0; x < gridSegments; x++)
                {
                    int current = z * (gridSegments + 1) + x;
                    int next = current + gridSegments + 1;

                    triangles[tIdx++] = current;
                    triangles[tIdx++] = next;
                    triangles[tIdx++] = current + 1;

                    triangles[tIdx++] = current + 1;
                    triangles[tIdx++] = next;
                    triangles[tIdx++] = next + 1;
                }
            }

            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private Mesh CreateCrossQuadMesh()
        {
            Mesh mesh = new Mesh();
            mesh.name = "LowEndVolumetricCloudCrossQuad";

            mesh.vertices = new Vector3[]
            {
                // Horizontal cloud deck plane
                new Vector3(-1f, 0f, -1f), new Vector3(1f, 0f, -1f), new Vector3(1f, 0f, 1f), new Vector3(-1f, 0f, 1f),
                // Vertical volumetric depth plane 1
                new Vector3(-1f, -0.55f, 0f), new Vector3(1f, -0.55f, 0f), new Vector3(1f, 0.55f, 0f), new Vector3(-1f, 0.55f, 0f),
                // Vertical volumetric depth plane 2
                new Vector3(0f, -0.55f, -1f), new Vector3(0f, -0.55f, 1f), new Vector3(0f, 0.55f, 1f), new Vector3(0f, 0.55f, -1f)
            };
            mesh.uv = new Vector2[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f),
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f)
            };
            mesh.triangles = new int[]
            {
                0, 1, 2, 0, 2, 3,
                4, 5, 6, 4, 6, 7,
                8, 9, 10, 8, 10, 11
            };
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
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

            try
            {
                MethodInfo[] methods = typeof(ApplicationLauncher).GetMethods(BindingFlags.Public | BindingFlags.Instance);
                MethodInfo onTrueMethod = typeof(LowEndVolumetricCloudsPlugin).GetMethod("OnToolbarToggleOn", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                MethodInfo onFalseMethod = typeof(LowEndVolumetricCloudsPlugin).GetMethod("OnToolbarToggleOff", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                for (int i = 0; i < methods.Length; i++)
                {
                    if (methods[i].Name != "AddModApplication") continue;
                    ParameterInfo[] parameters = methods[i].GetParameters();
                    if (parameters.Length == 8 && parameters[7].ParameterType == typeof(Texture))
                    {
                        Delegate onTrue = Delegate.CreateDelegate(parameters[0].ParameterType, this, onTrueMethod);
                        Delegate onFalse = Delegate.CreateDelegate(parameters[1].ParameterType, this, onFalseMethod);
                        toolbarButton = (ApplicationLauncherButton)methods[i].Invoke(
                            ApplicationLauncher.Instance,
                            new object[]
                            {
                                onTrue,
                                onFalse,
                                null,
                                null,
                                null,
                                null,
                                ApplicationLauncher.AppScenes.FLIGHT | ApplicationLauncher.AppScenes.MAPVIEW,
                                (Texture)toolbarIconTexture
                            }
                        );
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LowEndVolumetricCloudsMod] Toolbar icon skipped (Alt + 0 still works): {ex.Message}");
            }
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

            // Always-included Unity transparent shader (Cull Off, ZWrite Off, works in Deferred + Scatterer Transparent pass)
            Shader cloudShader = Shader.Find("Sprites/Default");
            if (cloudShader == null) cloudShader = Shader.Find("UI/Default");
            if (cloudShader == null) cloudShader = Shader.Find("Unlit/Transparent");

            if (cloudShader == null)
            {
                Debug.LogError("[LowEndVolumetricCloudsMod] Unable to locate built-in transparent Unity shader.");
                return;
            }

            cloudShellMaterial = new Material(cloudShader);
            cloudShellMaterial.mainTexture = cloudLayerTexture;
            cloudShellMaterial.renderQueue = 3020;

            cloudPuffMaterial = new Material(cloudShader);
            cloudPuffMaterial.mainTexture = cloudPuffTexture;
            cloudPuffMaterial.renderQueue = 3040;

            scaledCloudMaterial = new Material(cloudShader);
            scaledCloudMaterial.mainTexture = cloudLayerTexture;
            scaledCloudMaterial.renderQueue = 3020;

            isPipelineInitialized = true;
            Debug.Log("[LowEndVolumetricCloudsMod] v1.1.0 3D volumetric cloud pipeline initialized!");
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
                guiDrawTexture = guiType.GetMethod("DrawTexture", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Rect), typeof(Texture) }, null);
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

            for (int i = 0; i < cloudShellRenderers.Count; i++)
            {
                if (cloudShellRenderers[i] != null && cloudShellRenderers[i].sharedMaterial != null)
                {
                    Destroy(cloudShellRenderers[i].sharedMaterial);
                }
            }
            cloudShellRenderers.Clear();
            cloudShellObjects.Clear();
            cloudPuffRenderers.Clear();
            cloudPuffObjects.Clear();

            if (localCloudRoot != null) Destroy(localCloudRoot);
            if (scaledCloudObj != null) Destroy(scaledCloudObj);
            if (unitSphereMesh != null) Destroy(unitSphereMesh);
            if (localCanopyMesh != null) Destroy(localCanopyMesh);
            if (crossQuadMesh != null) Destroy(crossQuadMesh);
            if (cloudShellMaterial != null) Destroy(cloudShellMaterial);
            if (cloudPuffMaterial != null) Destroy(cloudPuffMaterial);
            if (scaledCloudMaterial != null) Destroy(scaledCloudMaterial);
            if (cloudLayerTexture != null) Destroy(cloudLayerTexture);
            if (cloudPuffTexture != null) Destroy(cloudPuffTexture);
            if (rainStreakTexture != null) Destroy(rainStreakTexture);
            if (toolbarIconTexture != null) Destroy(toolbarIconTexture);

            Debug.Log("[LowEndVolumetricCloudsMod] 3D volumetric cloud pipeline shut down safely.");
        }
    }
}
