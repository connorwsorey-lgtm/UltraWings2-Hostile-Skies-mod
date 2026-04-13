/*
 * Ultrawings 2 - MelonLoader Trainer Mod v6.0
 * ============================================
 * Full rebuild with VR controller support and verified features.
 * v6: Experimental tab (wind control, engine diagnostics, direct physics access)
 *
 * CONTROLS:
 *   Spacebar / F1      = Toggle menu + pause
 *   Left Thumbstick    = Navigate menu (up/down) + adjust sliders (left/right)
 *   Right Thumbstick   = Switch tabs (left/right)
 *   A Button (R)       = Activate / confirm
 *   B Button (R)       = Back / close menu
 *   Keyboard fallback: Arrows=nav, Enter=activate, A/D=sliders
 */

using MelonLoader;
using MelonLoader.Utils;
using Il2CppInterop.Runtime;
using UnityEngine;
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;

using Input = UnityEngine.Input;

// Core game types (proven safe)
using Il2Cpp;           // GameManager, PlayerDataDO, PlayerDataModel, OVRInput, ScreenProjectorSignals, ControllerAircraft
using Il2CppAi;         // AircraftControllerConfigDO
using Il2CppWeapon;     // FiringMechanismConfigDO, Hardpoint
using Il2CppCryptoTools; // CryptoConfigDO
using Il2CppLighting;   // SKY_CONDITION
using Il2CppVehicle;    // AircraftEngine, IEngine — live engine thrust/RPM
using Il2CppTargetableSystem; // RigidTargetableAircraft, AircraftPhysicsController — live physics sim

[assembly: MelonInfo(typeof(UW2Mod.UW2TrainerMod), "UW2 Trainer", "6.0.0", "UW2Modder")]
[assembly: MelonGame("Bit Planet Games, LLC", "Ultrawings 2")]

namespace UW2Mod
{
    public class UW2TrainerMod : MelonMod
    {
        // === STATE ===
        private bool showMenu = false;
        private int activeTab = 0;
        private readonly string[] tabNames = { "Cheats", "Flight", "Combat", "Physics", "Aircraft", "Explore", "Info", "Diag" };
        private bool initialized = false;
        private float initRetryTimer = 0f;

        // === GAME OBJECTS (proven types only) ===
        private GameManager gameManager = null;
        private PlayerDataDO playerData = null;
        private List<AircraftControllerConfigDO> aircraftConfigs = new List<AircraftControllerConfigDO>();
        private CryptoConfigDO cryptoConfig = null;

        // === PHYSICS ORIGINALS ===
        // order: Power, Lift, Stall, Roll, Pitch, Yaw, Drag, Throttle, BankedYaw, BankedTurn, AeroEffect, MaxAeroSpeed, AutoTurnPitch, AutoRoll, AutoPitch, AirBrakes, WheelTorque, WheelBrake, ZeroLift
        private Dictionary<string, float[]> origPhysics = new Dictionary<string, float[]>();

        // === CHEAT STATE (persisted to file) ===
        private bool unlimitedFuel = false;
        private bool unlimitedAmmo = false;
        private bool godMode = false;
        private bool allVehiclesUnlocked = false;
        private float targetTimeScale = 1f;
        private float continuousBoost = 0f; // extra forward force per frame
        private float enginePowerMultiplier = 1f; // multiplier applied to live engines every frame
        private float dragMultiplier = 1f; // applied every frame to rigidbody drag (1 = normal, 0.5 = half, 0.25 = quarter)
        private float maxDragClamp = -1f; // -1 = disabled. When set, rb.drag is clamped to this max value every frame
        private Dictionary<string, float> origEngineForce = new Dictionary<string, float>(); // original m_forceAtMaxRPM per engine
        private string modSettingsPath;

        // === HUD ===
        private bool showHUD = false;
        private GUIStyle sHudLabel, sHudValue, sHudHeader;
        private bool hudStylesInit = false;

        // === TABLET UI ===
        private bool tabletModded = false;
        private string lastAircraftName = "";

        // === WRIST PANEL (VR world-space UI) ===
        private GameObject wristPanelRoot;
        private bool wristPanelVisible = false;
        private int wristSelectedRow = 0;
        private int wristItemCount = 0;
        private int wristScrollOffset = 0;
        private UnityEngine.UI.Text[] wristLabels;
        private UnityEngine.UI.Image[] wristHighlights;
        private UnityEngine.UI.Text wristStatusText;
        private UnityEngine.UI.Text wristTitleText;
        private float wristNavCooldown = 0f;

        // === DRONE AI ===
        private class DroneAI
        {
            public GameObject go;
            public Rigidbody rb;
            public float speed = 200f;       // thrust force — must overcome drag
            public float turnSpeed = 4f;      // snappy turning
            public float maxSpeed = 90f;      // velocity cap m/s
            public float cruiseSpeed = 55f;   // target cruise velocity
            public float fireInterval = 0.25f;
            public float fireTimer = 0f;
            public float fireRange = 500f;
            public float fireAngle = 30f;
            public float minAlt = 40f;
            public bool alive = true;
            // Attack pattern state
            public float attackPhase = 0f;    // 0=approach, 1=commit, 2=break
            public float breakTimer = 0f;
            public Vector3 breakDir;
            public float bankAngle = 0f;      // visual banking
        }
        private List<DroneAI> activeDrones = new List<DroneAI>();

        // === COMBAT SPAWNER ===
        private List<GameObject> cachedFighterPrefabs = new List<GameObject>();
        private List<GameObject> cachedTurretPrefabs = new List<GameObject>();
        private List<GameObject> cachedShipPrefabs = new List<GameObject>();
        private bool combatPrefabsScanned = false;
        private List<GameObject> spawnedCombatants = new List<GameObject>();
        private float combatSpawnTimer = 0f;
        private float combatSpawnInterval = 0f; // 0 = no auto-respawn
        private int combatMaxFighters = 0;
        private int combatActiveFighters = 0;
        private string combatModeName = "OFF";

        // === ENEMY PREFAB CACHE (loaded via scene hook) ===
        private GameObject cachedEnemyMesser = null;
        private GameObject cachedEnemyWolf = null;
        private bool enemyPrefabsLoading = false;
        private bool enemyPrefabsReady = false;
        private string savedCombatSceneName = "";  // persisted — scene that had enemy prefabs
        private int savedCombatSceneIndex = -1;    // persisted — build index of that scene
        private bool combatSceneAutoLoaded = false; // true once we've auto-loaded on startup

        // === HOSTILE SKIES MUSIC ===
        private GameObject hsMusicObject = null;
        private AudioSource hsMusicSource = null;
        private List<AudioClip> hsMusicClips = new List<AudioClip>();
        private int hsMusicCurrentTrack = 0;
        private bool hsMusicLoaded = false;
        private bool hsMusicPlaying = false;
        private float hsMusicVolume = 0.5f;
        private float originalBGMVolume = -1f; // -1 = not stored yet

        // === HOSTILE SKIES ===
        private bool hostileSkiesActive = false;
        private int threatLevel = 1;
        private int threatLevelUnlocked = 2; // start with levels 1-2 unlocked
        private int totalKills = 0;
        private int sessionKills = 0;
        private int killStreak = 0;
        private int bestStreak = 0;
        private int islandsDiscovered = 0;
        private bool[] islandDiscoveryMap = new bool[10]; // Island_00 through Island_08
        private bool grenadesUnlocked = false;
        private bool dartsUnlocked = false;
        private bool rapidFireUnlocked = false;
        private string combatSavePath;
        private float zoneCheckTimer = 0f;
        private float ambientSpawnTimer = 0f;
        private float killCheckTimer = 0f;
        private int previousCombatantCount = 0;

        private struct CombatZone
        {
            public Vector3 Position;
            public float Radius;
            public string Name;
            public int ZoneType; // 0=Airfield, 1=Flyover, 2=Naval, 3=Mountain
            public bool Discovered;
            public float CooldownRemaining;
        }
        private List<CombatZone> combatZones = new List<CombatZone>();
        private bool zonesInitialized = false;

        // === TELEMETRY LOGGING ===
        private float telemetryTimer = 0f;
        private const float TELEMETRY_INTERVAL = 0.5f; // log every 0.5 seconds when HUD is active

        // === ANTI-FALL-THROUGH ===
        private string lastKnownAircraftName = "";
        private float spawnProtectionTimer = 0f;
        private bool spawnProtectionActive = false;
        private Vector3 lastSafePosition;
        private bool hasSafePosition = false;

        // === GLIDE TRACKER ===
        private bool glideTracking = false;
        private Vector3 glideStartPos;
        private float glideStartAlt;
        private float glideStartSpeed;
        private float glideMaxDist;
        private bool wasEngineOn = false;

        // === GUI ===
        private string statusMessage = "";
        private float statusTimer = 0f;
        private int selectedRow = 0;
        private int maxRows = 0;
        private float navCooldown = 0f;
        private float actionCooldown = 0f;
        private const float NAV_DELAY = 0.15f;
        private const float ACTION_DELAY = 0.3f;
        private float menuHoldTimer = 0f;
        private bool menuHoldTriggered = false;
        private GUIStyle sNormal, sHighlight, sHeader, sStatus;
        private bool stylesInit = false;

        private struct MI { public string Label; public bool IsHeader; public Action OnActivate; public Action OnLeft; public Action OnRight; }
        private List<MI> items = new List<MI>();

        // ================================================================
        // INIT
        // ================================================================
        public override void OnInitializeMelon()
        {
            modSettingsPath = Path.Combine(MelonEnvironment.GameRootDirectory, "Mods", "UW2Trainer_State.txt");
            combatSavePath = Path.Combine(MelonEnvironment.GameRootDirectory, "Mods", "UW2Trainer_Combat.txt");
            LoadModState();
            LoadCombatState();
            LoggerInstance.Msg("=== UW2 Trainer v6.0 — Live Engine + Experimental ===");
            LoggerInstance.Msg("Spacebar=Menu | VR: A=Select B=Close LStick=Nav RStick=Tabs");
            LoggerInstance.Msg($"[STATE] fuel={unlimitedFuel} ammo={unlimitedAmmo} god={godMode} allVehicles={allVehiclesUnlocked}");
        }

        // ================================================================
        // SCENE LOAD HOOK — auto-cache enemy prefabs from combat missions
        // ================================================================
        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            LoggerInstance.Msg($"[SCENE] Loaded: index={buildIndex} name='{sceneName}'");

            // Delay the scan slightly — scene objects may not be fully initialized yet
            pendingSceneScan = true;
            pendingSceneName = sceneName;
            pendingSceneIndex = buildIndex;

            // Log if we have a saved combat scene available
            if (sceneName == "AlwaysLoaded" && !string.IsNullOrEmpty(savedCombatSceneName))
            {
                LoggerInstance.Msg($"[SCENE] Saved combat scene available: '{savedCombatSceneName}' index={savedCombatSceneIndex} — use 'Load Enemy Prefabs' in Combat tab");
            }
        }

        private bool pendingSceneScan = false;
        private string pendingSceneName = "";
        private int pendingSceneIndex = -1;
        private float sceneScanDelay = 0f;

        private bool IsValidCachedPrefab(GameObject go)
        {
            try { if (go == null) return false; var n = go.name; return n != null; }
            catch { return false; }
        }

        private GameObject CloneAsPrefabTemplate(GameObject source, string templateName)
        {
            try
            {
                var clone = UnityEngine.Object.Instantiate(source);
                clone.name = templateName;
                clone.SetActive(false);
                clone.transform.position = new Vector3(0, -5000, 0);
                UnityEngine.Object.DontDestroyOnLoad(clone);
                LoggerInstance.Msg($"[SCENE]   *** Cloned '{source.name}' → '{templateName}' (DontDestroyOnLoad) ***");
                return clone;
            }
            catch (Exception ex)
            {
                LoggerInstance.Msg($"[SCENE]   Clone failed for '{source?.name ?? "null"}': {ex.Message}");
                return null;
            }
        }

        private void ScanSceneForEnemyPrefabs()
        {
            // Skip free flight and non-combat scenes — only scan mission scenes
            string sn = pendingSceneName ?? "";
            if (sn.StartsWith("FreeFlight") || sn == "LoadFirst" || sn == "Main" || sn == "AlwaysLoaded" || sn.StartsWith("Office_") || sn.StartsWith("Island_"))
            {
                LoggerInstance.Msg($"[SCENE] Skipping non-combat scene '{sn}'");
                return;
            }

            LoggerInstance.Msg($"[SCENE] === Scanning scene '{pendingSceneName}' (index={pendingSceneIndex}) for combat prefabs ===");
            int found = 0;

            // 1. AirDefensePatrolGroup — holds m_enemyAircraftPrefab (fighter prefab)
            try
            {
                var allPatrol = Resources.FindObjectsOfTypeAll<Il2CppGameplay.Defense.AirDefensePatrolGroup>();
                for (int i = 0; i < (allPatrol?.Length ?? 0); i++)
                {
                    try
                    {
                        var prefab = allPatrol[i].m_enemyAircraftPrefab;
                        if (prefab == null) continue;
                        string pName = prefab.name ?? "";
                        LoggerInstance.Msg($"[SCENE]   PatrolGroup: enemyPrefab='{pName}'");

                        if (!IsValidCachedPrefab(cachedEnemyMesser))
                        {
                            cachedEnemyMesser = CloneAsPrefabTemplate(prefab, "HS_Prefab_Messer");
                            if (cachedEnemyMesser != null) { enemyPrefabsReady = true; found++; }
                        }
                        else if (!IsValidCachedPrefab(cachedEnemyWolf) && pName != "HS_Prefab_Messer")
                        {
                            cachedEnemyWolf = CloneAsPrefabTemplate(prefab, "HS_Prefab_Wolf");
                            if (cachedEnemyWolf != null) found++;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { LoggerInstance.Msg($"[SCENE] PatrolGroup scan: {ex.Message}"); }

            // 2. AirDefenseMissionController — holds m_aircraftBomberPrefab
            try
            {
                var allDef = Resources.FindObjectsOfTypeAll<Il2CppGameplay.Defense.AirDefenseMissionController>();
                for (int i = 0; i < (allDef?.Length ?? 0); i++)
                {
                    try
                    {
                        var bomberPrefab = allDef[i].m_aircraftBomberPrefab;
                        if (bomberPrefab != null)
                        {
                            LoggerInstance.Msg($"[SCENE]   AirDefense: bomberPrefab='{bomberPrefab.name}'");
                            // Store bombers in the fighter prefab list for now
                            if (!cachedFighterPrefabs.Contains(bomberPrefab))
                            {
                                cachedFighterPrefabs.Add(bomberPrefab);
                                found++;
                                LoggerInstance.Msg($"[SCENE]   *** CACHED bomber prefab: '{bomberPrefab.name}' ***");
                            }
                        }

                        // Also grab enemy waves if available
                        var waves = allDef[i].m_enemyWaves;
                        LoggerInstance.Msg($"[SCENE]   AirDefense: enemyWaves={waves?.Count ?? 0}");
                    }
                    catch { }
                }
            }
            catch (Exception ex) { LoggerInstance.Msg($"[SCENE] AirDefense scan: {ex.Message}"); }

            // 3. AiAircraftController — any AI fighter loaded in this scene
            try
            {
                var allAI = Resources.FindObjectsOfTypeAll<Il2CppAi.AiAircraftController>();
                for (int i = 0; i < (allAI?.Length ?? 0); i++)
                {
                    try
                    {
                        var ai = allAI[i];
                        var rootGO = ai.transform.root.gameObject;
                        string rootName = rootGO.name;
                        // Skip player aircraft and our own prefab clones
                        try { var playerAc = GameManager.ControllerAircraft; if (playerAc != null && rootName == playerAc.gameObject.name) continue; } catch { }
                        if (rootName.StartsWith("HS_Prefab")) continue;

                        LoggerInstance.Msg($"[SCENE]   AiAircraft: '{rootName}' active={rootGO.activeSelf} hasWeapons={ai.HasWeaponSystems}");

                        if (!IsValidCachedPrefab(cachedEnemyMesser))
                        {
                            cachedEnemyMesser = CloneAsPrefabTemplate(rootGO, "HS_Prefab_Messer");
                            if (cachedEnemyMesser != null) { enemyPrefabsReady = true; found++; }
                        }
                        else if (!IsValidCachedPrefab(cachedEnemyWolf) && rootName != cachedEnemyMesser.name)
                        {
                            cachedEnemyWolf = CloneAsPrefabTemplate(rootGO, "HS_Prefab_Wolf");
                            if (cachedEnemyWolf != null) found++;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { LoggerInstance.Msg($"[SCENE] AiAircraft scan: {ex.Message}"); }

            // 4. TargetSpawner — dev tool, grab its m_target prefab
            try
            {
                var allTS = Resources.FindObjectsOfTypeAll<Il2CppGameplay.TargetElimination2.Tester.TargetSpawner>();
                for (int i = 0; i < (allTS?.Length ?? 0); i++)
                {
                    try
                    {
                        var target = allTS[i].m_target;
                        if (target != null)
                        {
                            LoggerInstance.Msg($"[SCENE]   TargetSpawner: target='{target.name}'");
                            if (!cachedFighterPrefabs.Contains(target))
                            {
                                cachedFighterPrefabs.Add(target);
                                found++;
                                LoggerInstance.Msg($"[SCENE]   *** CACHED TargetSpawner target: '{target.name}' ***");
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { LoggerInstance.Msg($"[SCENE] TargetSpawner scan: {ex.Message}"); }

            // 5. CombatMission — holds m_vehicleGroups (lists of enemy vehicle groups)
            try
            {
                var allCM = Resources.FindObjectsOfTypeAll<Il2CppGameplay.TargetElimination2.CombatMission>();
                for (int i = 0; i < (allCM?.Length ?? 0); i++)
                {
                    try
                    {
                        LoggerInstance.Msg($"[SCENE]   CombatMission: '{allCM[i].gameObject.name}'");
                        var groups = allCM[i].m_vehicleGroups;
                        LoggerInstance.Msg($"[SCENE]     vehicleGroups: {groups?.Count ?? 0}");
                    }
                    catch { }
                }
            }
            catch (Exception ex) { LoggerInstance.Msg($"[SCENE] CombatMission scan: {ex.Message}"); }

            // 6. FlakWeapon — cache for player ammo swap
            try
            {
                var allFlak = Resources.FindObjectsOfTypeAll<Il2CppWeapon.FlakWeapon>();
                for (int i = 0; i < (allFlak?.Length ?? 0); i++)
                {
                    LoggerInstance.Msg($"[SCENE]   FlakWeapon: '{allFlak[i].gameObject.name}' root='{allFlak[i].transform.root.gameObject.name}'");
                    found++;
                }
            }
            catch { }

            // 7. SimpleTurret — turret prefabs
            try
            {
                var allTurrets = Resources.FindObjectsOfTypeAll<Il2CppTurret.SimpleTurret>();
                for (int i = 0; i < (allTurrets?.Length ?? 0); i++)
                {
                    var rootGO = allTurrets[i].transform.root.gameObject;
                    LoggerInstance.Msg($"[SCENE]   SimpleTurret: '{allTurrets[i].gameObject.name}' root='{rootGO.name}'");
                    if (!cachedTurretPrefabs.Contains(rootGO))
                    {
                        cachedTurretPrefabs.Add(rootGO);
                        found++;
                    }
                }
            }
            catch { }

            // 8. Scan all GameObjects for escort/enemy-named objects with AI
            try
            {
                var allGO = Resources.FindObjectsOfTypeAll<GameObject>();
                for (int i = 0; i < allGO.Length; i++)
                {
                    string n = allGO[i]?.name?.ToLower() ?? "";
                    if ((n.Contains("escort") || n.Contains("wolf") || n.Contains("messer") || n.Contains("enemy")) && !n.Contains("trophy") && !n.StartsWith("hs_prefab"))
                    {
                        var ai = allGO[i].GetComponentInChildren<Il2CppAi.AiAircraftController>(true);
                        if (ai != null)
                        {
                            LoggerInstance.Msg($"[SCENE]   Enemy GO: '{allGO[i].name}' active={allGO[i].activeSelf} hasWeapons={ai.HasWeaponSystems}");
                            if (!IsValidCachedPrefab(cachedEnemyMesser))
                            {
                                cachedEnemyMesser = CloneAsPrefabTemplate(allGO[i], "HS_Prefab_Messer");
                                if (cachedEnemyMesser != null) { enemyPrefabsReady = true; found++; }
                            }
                            else if (!IsValidCachedPrefab(cachedEnemyWolf))
                            {
                                cachedEnemyWolf = CloneAsPrefabTemplate(allGO[i], "HS_Prefab_Wolf");
                                if (cachedEnemyWolf != null) found++;
                            }
                        }
                    }
                }
            }
            catch { }

            if (found > 0)
            {
                LoggerInstance.Msg($"[SCENE] === Cached {found} combat prefabs! enemyPrefabsReady={enemyPrefabsReady} ===");
                combatPrefabsScanned = true;

                // Save this scene info so we can auto-load it on next startup
                if (enemyPrefabsReady && !string.IsNullOrEmpty(pendingSceneName) && pendingSceneName != savedCombatSceneName)
                {
                    savedCombatSceneName = pendingSceneName;
                    savedCombatSceneIndex = pendingSceneIndex;
                    SaveCombatState();
                    LoggerInstance.Msg($"[SCENE] Saved combat scene: '{savedCombatSceneName}' index={savedCombatSceneIndex} for auto-load on next launch");
                }

                // If this was a manually triggered additive load, unload the scene
                // (prefabs were already cloned+DontDestroyOnLoad during caching above)
                if (combatSceneAutoLoaded && pendingSceneName == savedCombatSceneName)
                {
                    try
                    {
                        LoggerInstance.Msg($"[SCENE] Unloading additively-loaded combat scene index={savedCombatSceneIndex}...");
                        UnityEngine.SceneManagement.SceneManager.UnloadSceneAsync(savedCombatSceneIndex);
                        combatSceneAutoLoaded = false;
                    }
                    catch (Exception ex) { LoggerInstance.Msg($"[SCENE] Cleanup: {ex.Message}"); }
                }
            }
            else
            {
                LoggerInstance.Msg($"[SCENE] === No combat prefabs in this scene ===");
            }
        }

        // ================================================================
        // UPDATE — VR + Keyboard input
        // ================================================================
        public override void OnUpdate()
        {
            // Timers (use unscaled so they work while paused)
            if (navCooldown > 0) navCooldown -= Time.unscaledDeltaTime;
            if (actionCooldown > 0) actionCooldown -= Time.unscaledDeltaTime;
            if (statusTimer > 0) statusTimer -= Time.unscaledDeltaTime;

            // Delayed scene scan for combat prefabs (wait 1s for objects to initialize)
            if (pendingSceneScan)
            {
                sceneScanDelay += Time.unscaledDeltaTime;
                if (sceneScanDelay >= 1.0f)
                {
                    pendingSceneScan = false;
                    sceneScanDelay = 0f;
                    try { ScanSceneForEnemyPrefabs(); } catch (Exception ex) { LoggerInstance.Error($"[SCENE] Scan error: {ex}"); }
                }
            }

            // === HUD TOGGLE: F2 ===
            if (Input.GetKeyDown(KeyCode.F2)) showHUD = !showHUD;

            // === NUMPAD HOTKEYS (work while flying, no menu needed) ===
            if (initialized && !showMenu)
            {
                try
                {
                    var ac = GameManager.ControllerAircraft;
                    if (ac != null)
                    {
                        // Numpad 0 — RESET TO STOCK (restore all defaults)
                        if (Input.GetKeyDown(KeyCode.Keypad0))
                        {
                            maxDragClamp = -1f;
                            dragMultiplier = 1f;
                            enginePowerMultiplier = 1f;
                            continuousBoost = 0f;
                            ResetLiveAircraft();
                            SetStatus("RESET — all mods off");
                        }

                        // Numpad 1 — Power 2x
                        if (Input.GetKeyDown(KeyCode.Keypad1))
                        {
                            var eng = ac.m_aircraftEngines?[0]?.TryCast<AircraftEngine>();
                            if (eng != null) { eng.m_forceAtMaxRPM *= 2f; SetStatus($"Power 2x → {eng.m_forceAtMaxRPM:F0}"); }
                        }

                        // Numpad 2 — Power 5x
                        if (Input.GetKeyDown(KeyCode.Keypad2))
                        {
                            var eng = ac.m_aircraftEngines?[0]?.TryCast<AircraftEngine>();
                            if (eng != null) { eng.m_forceAtMaxRPM *= 5f; SetStatus($"Power 5x → {eng.m_forceAtMaxRPM:F0}"); }
                        }

                        // Numpad 3 — Realistic Glide toggle
                        if (Input.GetKeyDown(KeyCode.Keypad3))
                        {
                            maxDragClamp = maxDragClamp >= 0f ? -1f : 0.01f;
                            SetStatus(maxDragClamp >= 0f ? "Realistic Glide ON" : "Realistic Glide OFF");
                        }

                        // Numpad 4 — Air Resistance: Reduced (0.5x)
                        if (Input.GetKeyDown(KeyCode.Keypad4))
                        {
                            dragMultiplier = 0.5f; maxDragClamp = -1f;
                            SetStatus("Air Resistance: Reduced");
                        }

                        // Numpad 5 — Air Resistance: Low (0.25x)
                        if (Input.GetKeyDown(KeyCode.Keypad5))
                        {
                            dragMultiplier = 0.25f; maxDragClamp = -1f;
                            SetStatus("Air Resistance: Low");
                        }

                        // Numpad 6 — Air Resistance: Minimal (0.1x)
                        if (Input.GetKeyDown(KeyCode.Keypad6))
                        {
                            dragMultiplier = 0.1f; maxDragClamp = -1f;
                            SetStatus("Air Resistance: Minimal");
                        }

                        // Numpad 7 — Force Spawn Weapons
                        if (Input.GetKeyDown(KeyCode.Keypad7))
                        {
                            var wa = ac.gameObject.GetComponentInChildren<Il2CppWeapon.WeaponAttachment>();
                            if (wa != null)
                            {
                                wa.m_forceSpawnAll = true;
                                wa.SpawnWeapons();
                                var vs = GameManager.VehicleSetup;
                                if (vs != null) { if (vs.m_controllerAircraft == null) vs.m_controllerAircraft = ac; vs.m_isInfiniteAmmo = true; vs.InitializeWeaponSystems(); }
                                SetStatus("Weapons spawned!");
                            }
                        }

                        // Numpad 8 — Unlimited Fuel toggle
                        if (Input.GetKeyDown(KeyCode.Keypad8))
                        {
                            unlimitedFuel = !unlimitedFuel;
                            if (playerData != null) playerData.isAircraftHasUnlimitedFuel = unlimitedFuel ? 1 : 0;
                            try { ac.SetUnlimitedFuel(unlimitedFuel); ac.RefuelVehicle(); } catch { }
                            SetStatus($"Fuel: {(unlimitedFuel ? "UNLIMITED" : "Normal")}");
                        }

                        // Numpad 9 — God Mode toggle
                        if (Input.GetKeyDown(KeyCode.Keypad9))
                        {
                            godMode = !godMode;
                            if (gameManager != null) gameManager.m_playerIsInvulnerable = godMode;
                            try { var cm = GameManager.CrashManager; if (cm != null) cm.EnableCrash(!godMode); } catch { }
                            SetStatus($"God Mode: {(godMode ? "ON" : "OFF")}");
                        }

                        // Numpad * — Boost toggle (medium)
                        if (Input.GetKeyDown(KeyCode.KeypadMultiply))
                        {
                            continuousBoost = continuousBoost > 0 ? 0 : 10000;
                            SetStatus(continuousBoost > 0 ? "Boost ON" : "Boost OFF");
                        }

                        // Numpad + — ALL MAX (5x power + realistic glide + weapons)
                        if (Input.GetKeyDown(KeyCode.KeypadPlus))
                        {
                            var engs = ac.m_aircraftEngines;
                            if (engs != null)
                            {
                                for (int i = 0; i < engs.Count; i++)
                                {
                                    var e = engs[i]?.TryCast<AircraftEngine>();
                                    if (e != null) e.m_forceAtMaxRPM *= 5f;
                                }
                            }
                            maxDragClamp = 0.01f; dragMultiplier = 1f;
                            var wa2 = ac.gameObject.GetComponentInChildren<Il2CppWeapon.WeaponAttachment>();
                            if (wa2 != null) { wa2.m_forceSpawnAll = true; wa2.SpawnWeapons(); }
                            SetStatus("ALL MAX — 5x + glide + weapons!");
                        }

                        // Numpad - — Laser Designator activate
                        if (Input.GetKeyDown(KeyCode.KeypadMinus))
                        {
                            var wa3 = ac.gameObject.GetComponentInChildren<Il2CppWeapon.WeaponAttachment>();
                            if (wa3 != null && wa3.m_laserDesignator != null)
                            {
                                wa3.m_laserDesignator.SetActive(true);
                                SetStatus("Laser designator activated!");
                            }
                            else SetStatus("No laser on this aircraft");
                        }
                    }
                }
                catch { }
            }

            // === MENU TOGGLE: Spacebar, F1, or hold Left Menu button ===
            bool toggleMenu = Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.F1);
            // Quest left menu button (Start) — hold for 0.5s to toggle (prevents accidental opens)
            try
            {
                if (OVRInput.Get(OVRInput.Button.Start))
                {
                    menuHoldTimer += Time.unscaledDeltaTime;
                    if (menuHoldTimer >= 1.0f && !menuHoldTriggered)
                    {
                        toggleMenu = true;
                        menuHoldTriggered = true;
                    }
                }
                else
                {
                    menuHoldTimer = 0f;
                    menuHoldTriggered = false;
                }
            }
            catch { }
            // B button closes menu
            try { if (showMenu && OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch)) toggleMenu = true; } catch { }

            if (toggleMenu)
            {
                // Left menu button hold → wrist panel (no pause, works while flying)
                // Space/F1 → IMGUI menu (pauses game, full feature set)
                bool fromVRMenuButton = menuHoldTriggered;
                if (fromVRMenuButton)
                {
                    ToggleWristPanel();
                    // Close IMGUI menu if it was open
                    if (showMenu) { showMenu = false; Time.timeScale = targetTimeScale; }
                }
                else
                {
                    showMenu = !showMenu;
                    selectedRow = 0;
                    Time.timeScale = showMenu ? 0f : targetTimeScale;
                    // Close wrist panel if it was open
                    if (wristPanelVisible) { wristPanelVisible = false; if (wristPanelRoot != null) wristPanelRoot.SetActive(false); }
                }
            }

            // === MENU NAVIGATION ===
            if (showMenu && navCooldown <= 0)
            {
                float navY = 0f, navX = 0f, tabX = 0f;

                // Keyboard
                if (Input.GetKey(KeyCode.UpArrow)) navY = 1f;
                if (Input.GetKey(KeyCode.DownArrow)) navY = -1f;
                if (Input.GetKeyDown(KeyCode.LeftArrow)) tabX = -1f;
                if (Input.GetKeyDown(KeyCode.RightArrow)) tabX = 1f;

                // VR left stick = navigate + slider
                try
                {
                    Vector2 ls = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
                    if (Mathf.Abs(ls.y) > 0.5f) navY = ls.y;
                    if (Mathf.Abs(ls.x) > 0.7f) navX = ls.x;
                }
                catch { }

                // VR right stick = switch tabs
                try
                {
                    Vector2 rs = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick, OVRInput.Controller.RTouch);
                    if (Mathf.Abs(rs.x) > 0.7f) tabX = rs.x;
                }
                catch { }

                // Apply navigation
                if (navY > 0.4f) { selectedRow = (selectedRow - 1 + maxRows) % Math.Max(1, maxRows); navCooldown = NAV_DELAY; }
                if (navY < -0.4f) { selectedRow = (selectedRow + 1) % Math.Max(1, maxRows); navCooldown = NAV_DELAY; }
                if (tabX < -0.4f) { activeTab = (activeTab - 1 + tabNames.Length) % tabNames.Length; selectedRow = 0; scrollOffset = 0; navCooldown = NAV_DELAY * 2; }
                if (tabX > 0.4f) { activeTab = (activeTab + 1) % tabNames.Length; selectedRow = 0; scrollOffset = 0; navCooldown = NAV_DELAY * 2; }

                // Slider adjust (keyboard A/D or left stick X)
                if (Input.GetKey(KeyCode.A)) navX = -1f;
                if (Input.GetKey(KeyCode.D)) navX = 1f;
                if (Mathf.Abs(navX) > 0.5f) TrySlider(navX > 0);
            }

            // === ACTION: Enter key or A button ===
            if (showMenu && actionCooldown <= 0)
            {
                bool activate = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
                try { if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch)) activate = true; } catch { }

                if (activate) TryActivate();
            }

            // === DEFERRED INIT ===
            if (!initialized)
            {
                initRetryTimer += Time.unscaledDeltaTime;
                if (initRetryTimer > 2f) { initRetryTimer = 0f; TryInitialize(); }
            }
            // Keep scanning for aircraft configs even after init (they load when you enter a plane)
            else if (aircraftConfigs.Count == 0)
            {
                initRetryTimer += Time.unscaledDeltaTime;
                if (initRetryTimer > 5f)
                {
                    initRetryTimer = 0f;
                    try
                    {
                        var all = Resources.FindObjectsOfTypeAll<AircraftControllerConfigDO>();
                        if (all != null)
                        {
                            for (int i = 0; i < all.Count; i++)
                            {
                                var c = all[i];
                                if (c != null && !aircraftConfigs.Contains(c))
                                {
                                    aircraftConfigs.Add(c);
                                    string n = c.name ?? "?";
                                    origPhysics[n] = new float[] {
                                        c.MaxEnginePower, c.Lift, c.StallSpeed, c.RollEffect, c.PitchEffect,
                                        c.YawEffect, c.DragIncreaseFactor, c.ThrottleChangeSpeed,
                                        c.BankedYawEffect, c.BankedTurnEffect, c.AerodynamicEffect,
                                        c.MaxAerodynamicEffectSpeed, c.AutoTurnPitch, c.AutoRollLevel,
                                        c.AutoPitchLevel, c.AirBrakesEffect, c.WheelTorque, c.WheelBrakeTorque,
                                        c.ZeroLiftSpeed
                                    };
                                    LoggerInstance.Msg($"[AUTO] Found aircraft '{n}': Power={c.MaxEnginePower:F0}");
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            // === CONTINUOUS EFFECTS ===
            if (initialized && playerData != null)
            {
                try
                {
                    if (unlimitedFuel)
                    {
                        playerData.isAircraftHasUnlimitedFuel = 1;
                        try { var ac = GameManager.ControllerAircraft; if (ac != null) { ac.m_unlimitedFuel = true; ac.m_fuelOnBoard = ac.m_fuelTankSize; } } catch { }
                    }
                    if (unlimitedAmmo) playerData.isAircraftHasUnlimitedAmmo = 1;
                    if (godMode && gameManager != null)
                    {
                        gameManager.m_playerIsInvulnerable = true;
                        try { var cm = GameManager.CrashManager; if (cm != null) cm.EnableCrash(false); } catch { }
                    }
                    if (allVehiclesUnlocked)
                    {
                        playerData.vehicleUnlocked = playerData.vehicleUnlocked | 63;
                        playerData.vehicleOwned = playerData.vehicleOwned | 63;
                    }
                }
                catch { }

                // Anti-fall-through: detect aircraft falling below ground and rescue it
                try
                {
                    var ac = GameManager.ControllerAircraft;
                    if (ac != null)
                    {
                        string acName = ac.gameObject.name;
                        var rb = ac.gameObject.GetComponent<Rigidbody>();

                        // Detect new aircraft spawn (name changed = new plane)
                        if (acName != lastKnownAircraftName)
                        {
                            lastKnownAircraftName = acName;
                            spawnProtectionActive = true;
                            spawnProtectionTimer = 0f;
                            hasSafePosition = false;
                            LoggerInstance.Msg($"[SPAWN] New aircraft detected: '{acName}' — spawn protection ON");
                        }

                        if (spawnProtectionActive)
                        {
                            spawnProtectionTimer += Time.unscaledDeltaTime;
                            float y = ac.transform.position.y;
                            float vy = rb != null ? rb.velocity.y : 0f;

                            // Track last safe position (above sea level and not falling fast)
                            if (y > 5f && vy > -10f)
                            {
                                lastSafePosition = ac.transform.position;
                                hasSafePosition = true;
                            }

                            // Rescue if well below ground or falling extremely fast
                            // Game stages aircraft at y=-10 before placement, so use -15 threshold
                            if (y < -15f || (y < 10f && vy < -30f))
                            {
                                LoggerInstance.Msg($"[SPAWN] Aircraft falling through ground! y={y:F1} vy={vy:F1} — RESCUING");

                                if (rb != null)
                                {
                                    rb.velocity = Vector3.zero;
                                    rb.angularVelocity = Vector3.zero;
                                }

                                // Try to find ground with a raycast from high up
                                Vector3 rescuePos;
                                float rayX = hasSafePosition ? lastSafePosition.x : ac.transform.position.x;
                                float rayZ = hasSafePosition ? lastSafePosition.z : ac.transform.position.z;
                                Vector3 rayOrigin = new Vector3(rayX, 500f, rayZ);

                                RaycastHit hit;
                                if (Physics.Raycast(rayOrigin, Vector3.down, out hit, 1000f))
                                {
                                    // Place 3m above the ground hit point
                                    rescuePos = hit.point + Vector3.up * 3f;
                                    LoggerInstance.Msg($"[SPAWN] Ground found at y={hit.point.y:F1} — placing at y={rescuePos.y:F1}");
                                }
                                else if (hasSafePosition)
                                {
                                    rescuePos = lastSafePosition;
                                    LoggerInstance.Msg($"[SPAWN] No ground hit — using last safe position y={rescuePos.y:F1}");
                                }
                                else
                                {
                                    // Fallback: place at reasonable altitude
                                    rescuePos = new Vector3(rayX, 50f, rayZ);
                                    LoggerInstance.Msg("[SPAWN] No ground, no safe pos — placing at y=50");
                                }

                                ac.transform.position = rescuePos;
                                SetStatus("Caught fall-through — repositioned!");
                            }

                            // Disable protection after 10 seconds (aircraft is stable by then)
                            if (spawnProtectionTimer > 10f)
                            {
                                spawnProtectionActive = false;
                                LoggerInstance.Msg("[SPAWN] Spawn protection expired");
                            }
                        }
                        else
                        {
                            // Outside spawn protection, still track safe position for emergencies
                            float y = ac.transform.position.y;
                            if (y > 5f)
                            {
                                lastSafePosition = ac.transform.position;
                                hasSafePosition = true;
                            }

                            // Emergency rescue if somehow way below ground (y < -50)
                            if (y < -50f)
                            {
                                LoggerInstance.Msg($"[SPAWN] Emergency: aircraft at y={y:F1} — rescuing");
                                if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }

                                Vector3 rayOrigin = new Vector3(ac.transform.position.x, 500f, ac.transform.position.z);
                                RaycastHit hit;
                                Vector3 rescuePos = hasSafePosition ? lastSafePosition : new Vector3(ac.transform.position.x, 50f, ac.transform.position.z);
                                if (Physics.Raycast(rayOrigin, Vector3.down, out hit, 1000f))
                                    rescuePos = hit.point + Vector3.up * 3f;

                                ac.transform.position = rescuePos;
                                SetStatus("Emergency rescue — repositioned!");
                            }
                        }
                    }
                    else
                    {
                        lastKnownAircraftName = "";
                        spawnProtectionActive = false;
                    }
                }
                catch { }

                // Continuous engine power enforcement (re-apply multiplier every frame so game can't reset it)
                try
                {
                    if (enginePowerMultiplier != 1f)
                    {
                        var ac = GameManager.ControllerAircraft;
                        if (ac != null)
                        {
                            var engines = ac.m_aircraftEngines;
                            if (engines != null)
                            {
                                for (int i = 0; i < engines.Count; i++)
                                {
                                    var eng = engines[i]?.TryCast<AircraftEngine>();
                                    if (eng == null) continue;
                                    string key = $"engine_{ac.gameObject.name}_{i}";
                                    if (origEngineForce.TryGetValue(key, out float origForce))
                                    {
                                        float target = origForce * enginePowerMultiplier;
                                        if (Math.Abs(eng.m_forceAtMaxRPM - target) > 0.1f)
                                            eng.m_forceAtMaxRPM = target;
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }

                // Continuous drag control (every frame, after game sets rb.drag)
                try
                {
                    if (dragMultiplier < 1f || maxDragClamp >= 0f)
                    {
                        var ac = GameManager.ControllerAircraft;
                        if (ac != null)
                        {
                            var rb = ac.gameObject.GetComponent<Rigidbody>();
                            if (rb != null)
                            {
                                float d = rb.drag;
                                // Apply multiplier first (if set)
                                if (dragMultiplier < 1f) d *= dragMultiplier;
                                // Then clamp to max (prevents engine-off drag penalty)
                                if (maxDragClamp >= 0f && d > maxDragClamp) d = maxDragClamp;
                                rb.drag = d;
                            }
                        }
                    }
                }
                catch { }

                // Continuous boost (separate try so it doesn't break other effects)
                try
                {
                    if (continuousBoost > 0 && !showMenu)
                    {
                        var ac = GameManager.ControllerAircraft;
                        if (ac != null)
                        {
                            var rb = ac.gameObject.GetComponent<Rigidbody>();
                            if (rb != null) rb.AddForce(ac.transform.forward * continuousBoost * Time.deltaTime, ForceMode.Force);
                        }
                    }
                }
                catch { }

                // Anti-pitch compensation — counteracts nose-up torque from boosted thrust
                // Only active when engine power is modded or boost is on (stock flight unaffected)
                try
                {
                    if (enginePowerMultiplier > 1f || continuousBoost > 0)
                    {
                        var ac = GameManager.ControllerAircraft;
                        if (ac != null)
                        {
                            var rb = ac.gameObject.GetComponent<Rigidbody>();
                            if (rb != null)
                            {
                                // Get current pitch rate (angular velocity around the right axis)
                                float pitchRate = Vector3.Dot(rb.angularVelocity, ac.transform.right);
                                // Only correct nose-UP pitch (positive pitch rate = nose going up)
                                if (pitchRate > 0.01f)
                                {
                                    // Scale correction with how much we've boosted
                                    float correctionStrength = 0f;
                                    if (enginePowerMultiplier > 1f) correctionStrength += (enginePowerMultiplier - 1f) * 1.5f;
                                    if (continuousBoost > 0) correctionStrength += continuousBoost * 0.0002f;
                                    correctionStrength = Math.Min(correctionStrength, 10f); // cap it
                                    // Apply nose-down torque proportional to pitch-up rate
                                    rb.AddTorque(-ac.transform.right * pitchRate * correctionStrength * rb.mass, ForceMode.Force);
                                }
                            }
                        }
                    }
                }
                catch { }

                // Glide tracker — auto-detects engine cut and tracks glide distance
                try
                {
                    if (showHUD)
                    {
                        var ac = GameManager.ControllerAircraft;
                        if (ac != null)
                        {
                            var engines = ac.m_aircraftEngines;
                            bool engineOn = false;
                            if (engines != null && engines.Count > 0)
                            {
                                var eng = engines[0]?.TryCast<AircraftEngine>();
                                if (eng != null) engineOn = eng.m_currentRPM > eng.m_idleRPM * 0.5f;
                            }

                            var rb = ac.gameObject.GetComponent<Rigidbody>();
                            float vel = rb != null ? rb.velocity.magnitude : 0;

                            if (wasEngineOn && !engineOn && vel > 10f)
                            {
                                // Engine just cut — start tracking
                                glideTracking = true;
                                glideStartPos = ac.transform.position;
                                glideStartAlt = ac.transform.position.y;
                                glideStartSpeed = vel;
                                glideMaxDist = 0f;
                            }
                            else if (glideTracking && engineOn)
                            {
                                // Engine restarted — stop tracking
                                glideTracking = false;
                            }

                            if (glideTracking)
                            {
                                float dist = Vector3.Distance(ac.transform.position, glideStartPos);
                                if (dist > glideMaxDist) glideMaxDist = dist;
                                // Stop tracking if basically stopped
                                if (vel < 2f) glideTracking = false;
                            }

                            wasEngineOn = engineOn;
                        }
                    }
                }
                catch { }

                // Wrist panel update (world-space VR UI)
                if (wristPanelVisible) { try { UpdateWristPanel(); } catch { } }

                // Combat spawner — respawn enemies when killed
                try { UpdateCombatSpawner(); } catch { }

                // Drone AI — chase and attack player
                try { UpdateDrones(); } catch { }
                try { UpdateHSMusic(); } catch { }

                // Hostile Skies — zone-based combat encounters
                try { UpdateHostileSkies(); } catch { }

                // Telemetry logging (when HUD is active, log flight data periodically for post-session analysis)
                if (showHUD)
                {
                    telemetryTimer += Time.unscaledDeltaTime;
                    if (telemetryTimer >= TELEMETRY_INTERVAL)
                    {
                        telemetryTimer = 0f;
                        try
                        {
                            var ac = GameManager.ControllerAircraft;
                            if (ac != null)
                            {
                                var rb = ac.gameObject.GetComponent<Rigidbody>();
                                float tSpeed = 0, tSpeedKts = 0, tThrust = 0, tForce = 0, tRpm = 0, tMaxRpm = 0;
                                float tAirSpdLimit = 0, tDrag = 0, tAngDrag = 0, tBaseDrag = 0;
                                float tCfgDragInc = 0, tAeroFactor = 0;
                                float tVelMag = rb != null ? rb.velocity.magnitude : 0;

                                var engines = ac.m_aircraftEngines;
                                if (engines != null && engines.Count > 0)
                                {
                                    var eng = engines[0]?.TryCast<AircraftEngine>();
                                    if (eng != null)
                                    {
                                        tRpm = eng.m_currentRPM;
                                        tMaxRpm = eng.m_maxRPM;
                                        tThrust = eng.m_thrust;
                                        tForce = eng.m_forceAtMaxRPM;
                                        tAirSpdLimit = eng.AirSpeedThrustLimit;
                                    }
                                }

                                var rta = ac.gameObject.GetComponent<RigidTargetableAircraft>();
                                if (rta != null)
                                {
                                    var phys = rta.m_aircraftSystem?.TryCast<AircraftPhysicsController>();
                                    if (phys != null)
                                    {
                                        tSpeed = phys.m_forwardSpeed;
                                        tSpeedKts = phys.m_forwardSpeedInKnots;
                                        tBaseDrag = phys.m_originalDrag;
                                        tAeroFactor = phys.m_aeroFactor;
                                        var cfg = phys.m_config;
                                        if (cfg != null) tCfgDragInc = cfg.DragIncreaseFactor;
                                    }
                                }

                                if (rb != null)
                                {
                                    tDrag = rb.drag;
                                    tAngDrag = rb.angularDrag;
                                }

                                float actualForce = tForce * (tMaxRpm > 0 ? tRpm / tMaxRpm : 0) * tAirSpdLimit;
                                LoggerInstance.Msg($"[TEL] spd={tSpeedKts:F1}kts vel={tVelMag:F1} thrust={tThrust:F2} force={tForce:F0} actualF={actualForce:F0} rpm={tRpm:F0}/{tMaxRpm:F0} airLim={tAirSpdLimit:F3} | drag={tDrag:F4} angDrag={tAngDrag:F4} baseDrag={tBaseDrag:F4} cfgDragInc={tCfgDragInc:F3} aero={tAeroFactor:F3} | mods: pwr={enginePowerMultiplier:F1}x drag={dragMultiplier:F2}x boost={continuousBoost:F0}");
                            }
                        }
                        catch { }
                    }
                }
            }
        }

        private void TryActivate()
        {
            int si = 0;
            foreach (var item in items)
            {
                if (item.IsHeader) continue;
                if (si == selectedRow && item.OnActivate != null)
                {
                    item.OnActivate();
                    actionCooldown = ACTION_DELAY;
                    return;
                }
                si++;
            }
        }

        private void TrySlider(bool right)
        {
            int si = 0;
            foreach (var item in items)
            {
                if (item.IsHeader) continue;
                if (si == selectedRow)
                {
                    if (right && item.OnRight != null) { item.OnRight(); navCooldown = NAV_DELAY; }
                    else if (!right && item.OnLeft != null) { item.OnLeft(); navCooldown = NAV_DELAY; }
                    return;
                }
                si++;
            }
        }

        // ================================================================
        // INITIALIZATION
        // ================================================================
        private void TryInitialize()
        {
            try
            {
                if (gameManager == null)
                {
                    gameManager = GameObject.FindObjectOfType<GameManager>();
                    if (gameManager != null) LoggerInstance.Msg("[INIT] GameManager found");
                }

                if (playerData == null && gameManager != null)
                {
                    try
                    {
                        var pd = GameManager.PlayersData;
                        if (pd != null)
                        {
                            playerData = pd.TryCast<PlayerDataDO>();
                            if (playerData != null)
                            {
                                unlimitedFuel = playerData.isAircraftHasUnlimitedFuel != 0;
                                unlimitedAmmo = playerData.isAircraftHasUnlimitedAmmo != 0;
                                LoggerInstance.Msg($"[INIT] PlayerData: ${playerData.money}, vehicles={playerData.vehicleUnlocked}, fuel={unlimitedFuel}, ammo={unlimitedAmmo}");
                            }
                        }
                    }
                    catch (Exception ex) { LoggerInstance.Warning($"[INIT] PlayerData: {ex.Message}"); }
                }

                if (aircraftConfigs.Count == 0)
                {
                    try
                    {
                        var all = Resources.FindObjectsOfTypeAll<AircraftControllerConfigDO>();
                        if (all != null)
                        {
                            for (int i = 0; i < all.Count; i++)
                            {
                                var c = all[i];
                                if (c != null && !aircraftConfigs.Contains(c))
                                {
                                    aircraftConfigs.Add(c);
                                    string n = c.name ?? "?";
                                    origPhysics[n] = new float[] {
                                        c.MaxEnginePower, c.Lift, c.StallSpeed, c.RollEffect, c.PitchEffect,
                                        c.YawEffect, c.DragIncreaseFactor, c.ThrottleChangeSpeed,
                                        c.BankedYawEffect, c.BankedTurnEffect, c.AerodynamicEffect,
                                        c.MaxAerodynamicEffectSpeed, c.AutoTurnPitch, c.AutoRollLevel,
                                        c.AutoPitchLevel, c.AirBrakesEffect, c.WheelTorque, c.WheelBrakeTorque,
                                        c.ZeroLiftSpeed
                                    };
                                    LoggerInstance.Msg($"[INIT] Aircraft '{n}': Power={c.MaxEnginePower:F0} Stall={c.StallSpeed:F1}");
                                }
                            }
                        }
                    }
                    catch (Exception ex) { LoggerInstance.Warning($"[INIT] Aircraft: {ex.Message}"); }
                }

                if (cryptoConfig == null)
                {
                    try
                    {
                        var all = Resources.FindObjectsOfTypeAll<CryptoConfigDO>();
                        if (all != null && all.Count > 0) cryptoConfig = all[0];
                    }
                    catch { }
                }

                if (gameManager != null && (playerData != null || aircraftConfigs.Count > 0))
                {
                    initialized = true;
                    SetStatus("Trainer ready!");
                    LoggerInstance.Msg("[INIT] === COMPLETE ===");
                }
            }
            catch (Exception ex) { LoggerInstance.Warning($"[INIT] {ex.Message}"); }
        }

        // ================================================================
        // CHEATS
        // ================================================================
        private void AddMoney(int amount)
        {
            if (playerData == null) { SetStatus("Not loaded yet"); return; }
            try
            {
                int cur = playerData.money;
                // Fix negative money
                if (cur < 0) cur = 0;
                // Cap at 9,999,999 to avoid overflow issues
                int newMoney = Math.Min(cur + amount, 9999999);
                playerData.money = newMoney;
                SetStatus($"${newMoney}");
            }
            catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); }
        }

        private void UnlockVehicles()
        {
            if (playerData == null) return;
            try
            {
                playerData.vehicleUnlocked = playerData.vehicleUnlocked | 63;
                playerData.vehicleOwned = playerData.vehicleOwned | 63;
                SetStatus("All 6 vehicles unlocked!");
            }
            catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); }
        }

        private void SwapVehicle(int v, string name)
        {
            if (playerData == null) return;
            try
            {
                playerData.currentVehicle = v;
                playerData.vehicleOwned = playerData.vehicleOwned | v;
                playerData.vehicleUnlocked = playerData.vehicleUnlocked | v;
                SetStatus($"Vehicle: {name}");
            }
            catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); }
        }

        private void SetWeather(int cond, string name)
        {
            try
            {
                // Direct sky change via AlwaysLoadedManager (works mid-flight)
                AlwaysLoadedManager.SetSkyState((SKY_CONDITION)cond);
                // Also set the force flags so it persists across scene changes
                if (gameManager != null)
                {
                    gameManager.m_forceWeather = true;
                    gameManager.m_forcedSkyCondition = (SKY_CONDITION)cond;
                }
                SetStatus($"Weather: {name}");
            }
            catch (Exception ex) { SetStatus($"Weather failed: {ex.Message}"); }
        }

        private void TriggerSave()
        {
            try
            {
                var pdi = GameManager.PlayerDataInterface;
                if (pdi == null) { SetStatus("SAVE FAILED: no interface"); return; }
                var model = pdi.TryCast<PlayerDataModel>();
                if (model == null) { SetStatus("SAVE FAILED: cast failed"); return; }
                int slot = PlayerDataModel.SaveGameIndex;
                model.SetAndSavePlayerData(slot);
                int money = playerData != null ? playerData.money : -1;
                SetStatus($"SAVED slot {slot} (${money})");
                LoggerInstance.Msg($"[SAVE] slot {slot} money=${money}");
            }
            catch (Exception ex) { SetStatus($"SAVE FAILED: {ex.Message}"); }
        }

        private void ResetPhysics(AircraftControllerConfigDO c)
        {
            string n = c.name ?? "?";
            if (!origPhysics.TryGetValue(n, out var o)) return;
            c.MaxEnginePower = o[0]; c.Lift = o[1]; c.StallSpeed = o[2];
            c.RollEffect = o[3]; c.PitchEffect = o[4]; c.YawEffect = o[5];
            c.DragIncreaseFactor = o[6]; c.ThrottleChangeSpeed = o[7];
            c.BankedYawEffect = o[8]; c.BankedTurnEffect = o[9]; c.AerodynamicEffect = o[10];
            c.MaxAerodynamicEffectSpeed = o[11]; c.AutoTurnPitch = o[12]; c.AutoRollLevel = o[13];
            c.AutoPitchLevel = o[14]; c.AirBrakesEffect = o[15]; c.WheelTorque = o[16];
            c.WheelBrakeTorque = o[17]; c.ZeroLiftSpeed = o[18];
        }

        // ================================================================
        // LIVE ENGINE + PHYSICS — modify the ACTUAL running aircraft
        // ================================================================

        /// <summary>
        /// Gets live engine info string for display. Returns null if no aircraft active.
        /// </summary>
        private string GetLiveEngineInfo()
        {
            try
            {
                var ac = GameManager.ControllerAircraft;
                if (ac == null) return null;

                var engines = ac.m_aircraftEngines;
                if (engines == null || engines.Count == 0) return "No engines";

                string info = "";
                for (int i = 0; i < engines.Count; i++)
                {
                    var eng = engines[i]?.TryCast<AircraftEngine>();
                    if (eng == null) continue;
                    info += $"E{i}: force={eng.m_forceAtMaxRPM:F0} thrust={eng.m_thrust:F1} RPM={eng.m_currentRPM:F0}/{eng.m_maxRPM:F0} ";
                }

                // Also get physics controller info
                var rta = ac.gameObject.GetComponent<RigidTargetableAircraft>();
                if (rta != null)
                {
                    var phys = rta.m_aircraftSystem;
                    if (phys != null)
                    {
                        var pc = phys.TryCast<AircraftPhysicsController>();
                        if (pc != null)
                            info += $"| Spd={pc.m_forwardSpeed:F1} Pwr={pc.m_enginePower:F1} Thr={pc.m_thrust:F1}";
                    }
                    var cfg = rta.m_aircraftControllerConfig;
                    if (cfg != null)
                        info += $" | CFG.Power={cfg.MaxEnginePower:F0}";
                }

                return info;
            }
            catch (Exception ex) { return $"Error: {ex.Message}"; }
        }

        /// <summary>
        /// Apply a power multiplier to the live aircraft's engines and physics config.
        /// Call this to ACTUALLY change aircraft speed mid-flight.
        /// </summary>
        private void ApplyLivePowerMultiplier(float multiplier)
        {
            try
            {
                var ac = GameManager.ControllerAircraft;
                if (ac == null) { SetStatus("Not in aircraft"); return; }

                int modified = 0;

                // === ENGINE THRUST (the actual force) ===
                var engines = ac.m_aircraftEngines;
                if (engines != null)
                {
                    for (int i = 0; i < engines.Count; i++)
                    {
                        var eng = engines[i]?.TryCast<AircraftEngine>();
                        if (eng == null) continue;

                        string key = $"engine_{ac.gameObject.name}_{i}";
                        // Cache original on first access
                        if (!origEngineForce.ContainsKey(key))
                            origEngineForce[key] = eng.m_forceAtMaxRPM;

                        float origForce = origEngineForce[key];
                        eng.m_forceAtMaxRPM = origForce * multiplier;
                        modified++;
                        LoggerInstance.Msg($"[ENGINE] {key}: forceAtMaxRPM {origForce:F0} -> {eng.m_forceAtMaxRPM:F0}");
                    }
                }

                // === PHYSICS CONFIG (affects aerodynamics calculations) ===
                var rta = ac.gameObject.GetComponent<RigidTargetableAircraft>();
                if (rta != null)
                {
                    var cfg = rta.m_aircraftControllerConfig;
                    if (cfg != null)
                    {
                        string cfgName = cfg.name ?? "?";
                        if (origPhysics.TryGetValue(cfgName, out var o))
                        {
                            cfg.MaxEnginePower = o[0] * multiplier;
                            LoggerInstance.Msg($"[PHYSICS] {cfgName}: MaxEnginePower {o[0]:F0} -> {cfg.MaxEnginePower:F0}");
                        }
                        else
                        {
                            // Config not in our cache — cache it now from the live aircraft
                            origPhysics[cfgName] = new float[] {
                                cfg.MaxEnginePower, cfg.Lift, cfg.StallSpeed, cfg.RollEffect, cfg.PitchEffect,
                                cfg.YawEffect, cfg.DragIncreaseFactor, cfg.ThrottleChangeSpeed,
                                cfg.BankedYawEffect, cfg.BankedTurnEffect, cfg.AerodynamicEffect,
                                cfg.MaxAerodynamicEffectSpeed, cfg.AutoTurnPitch, cfg.AutoRollLevel,
                                cfg.AutoPitchLevel, cfg.AirBrakesEffect, cfg.WheelTorque, cfg.WheelBrakeTorque,
                                cfg.ZeroLiftSpeed
                            };
                            cfg.MaxEnginePower *= multiplier;
                            LoggerInstance.Msg($"[PHYSICS] {cfgName}: cached + set MaxEnginePower -> {cfg.MaxEnginePower:F0}");
                        }
                    }
                }

                enginePowerMultiplier = multiplier;
                SetStatus($"{modified} engine(s) set to {multiplier}x power");
            }
            catch (Exception ex) { SetStatus($"Engine mod failed: {ex.Message}"); LoggerInstance.Error($"[ENGINE] {ex}"); }
        }

        /// <summary>
        /// Reset live aircraft engines + physics to original values.
        /// </summary>
        private void TrySetupTablet()
        {
            try
            {
                var ac = GameManager.ControllerAircraft;
                if (ac == null) return;
                string acName = ac.gameObject.name;
                if (acName == lastAircraftName && tabletModded) return; // already done for this aircraft
                lastAircraftName = acName;

                var tabletMgr = ac.m_tabletManager;
                if (tabletMgr == null) return;

                // Find the MainTabletScreenView
                var screenView = ac.gameObject.GetComponentInChildren<MainTabletScreenView>(true);
                if (screenView == null) return;

                var buttons = screenView.m_menuButtons;
                if (buttons == null || buttons.Length < 2) return;

                // Clone the last button to create mod buttons
                var templateBtn = buttons[buttons.Length - 1]; // use last button as template
                if (templateBtn == null) return;

                // Create mod buttons by cloning
                var parent = templateBtn.transform.parent;
                if (parent == null) return;

                // Power 2x button
                var btn2x = UnityEngine.Object.Instantiate(templateBtn.gameObject, parent);
                btn2x.name = "btn_mod_power2x";
                var bv2x = btn2x.GetComponent<ButtonView>();
                if (bv2x != null)
                {
                    bv2x.UpdateTextValue("POWER 2x");
                    bv2x.button.onClick.RemoveAllListeners();
                    bv2x.button.onClick.AddListener((UnityEngine.Events.UnityAction)(() => {
                        var e = GameManager.ControllerAircraft?.m_aircraftEngines?[0]?.TryCast<AircraftEngine>();
                        if (e != null) e.m_forceAtMaxRPM *= 2f;
                    }));
                    bv2x.ActivateCollider(true);
                }

                // Force Spawn Weapons button
                var btnWeap = UnityEngine.Object.Instantiate(templateBtn.gameObject, parent);
                btnWeap.name = "btn_mod_weapons";
                var bvWeap = btnWeap.GetComponent<ButtonView>();
                if (bvWeap != null)
                {
                    bvWeap.UpdateTextValue("SPAWN WEAPONS");
                    bvWeap.button.onClick.RemoveAllListeners();
                    bvWeap.button.onClick.AddListener((UnityEngine.Events.UnityAction)(() => {
                        var aircraft = GameManager.ControllerAircraft;
                        if (aircraft == null) return;
                        var wa = aircraft.gameObject.GetComponentInChildren<Il2CppWeapon.WeaponAttachment>();
                        if (wa != null) { wa.m_forceSpawnAll = true; wa.SpawnWeapons(); }
                        var vs = GameManager.VehicleSetup;
                        if (vs != null) { if (vs.m_controllerAircraft == null) vs.m_controllerAircraft = aircraft; vs.m_isInfiniteAmmo = true; vs.InitializeWeaponSystems(); }
                    }));
                    bvWeap.ActivateCollider(true);
                }

                // Realistic Glide button
                var btnGlide = UnityEngine.Object.Instantiate(templateBtn.gameObject, parent);
                btnGlide.name = "btn_mod_glide";
                var bvGlide = btnGlide.GetComponent<ButtonView>();
                if (bvGlide != null)
                {
                    bvGlide.UpdateTextValue("REALISTIC GLIDE");
                    bvGlide.button.onClick.RemoveAllListeners();
                    bvGlide.button.onClick.AddListener((UnityEngine.Events.UnityAction)(() => {
                        maxDragClamp = maxDragClamp >= 0f ? -1f : 0.01f; // toggle
                    }));
                    bvGlide.ActivateCollider(true);
                }

                tabletModded = true;
                LoggerInstance.Msg($"[TABLET] Mod buttons added to tablet on '{acName}'");
            }
            catch (Exception ex) { LoggerInstance.Msg($"[TABLET] Setup failed: {ex.Message}"); }
        }

        // ================================================================
        // WRIST PANEL — World-space VR UI
        // ================================================================
        private struct WristItem
        {
            public string Label;
            public Action OnActivate;
            public Func<string> DynamicLabel; // if set, updates label each frame
        }

        private List<WristItem> wristItems = new List<WristItem>();

        private void CreateWristPanel()
        {
            if (wristPanelRoot != null) return; // already created

            try
            {
                // Root object
                wristPanelRoot = new GameObject("UW2_WristPanel");
                UnityEngine.Object.DontDestroyOnLoad(wristPanelRoot);

                // Canvas — world space
                var canvas = wristPanelRoot.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                wristPanelRoot.AddComponent<UnityEngine.UI.CanvasScaler>();

                // Scale for VR (tiny in world units)
                wristPanelRoot.transform.localScale = new Vector3(0.00035f, 0.00035f, 0.00035f);

                // Grab a font from the game's existing UI (CreateDynamicFontFromOSFont fails in VR)
                Font gameFont = null;
                try
                {
                    var existingTexts = Resources.FindObjectsOfTypeAll<UnityEngine.UI.Text>();
                    for (int i = 0; i < existingTexts.Length; i++)
                    {
                        if (existingTexts[i] != null && existingTexts[i].font != null)
                        {
                            gameFont = existingTexts[i].font;
                            LoggerInstance.Msg($"[WRIST] Borrowed font: '{gameFont.name}'");
                            break;
                        }
                    }
                    if (gameFont == null)
                    {
                        gameFont = Font.CreateDynamicFontFromOSFont("Arial", 24);
                        LoggerInstance.Msg("[WRIST] Fallback to Arial");
                    }
                }
                catch { }
                if (gameFont == null)
                {
                    LoggerInstance.Warning("[WRIST] No font found — text will be invisible!");
                }

                // Panel dimensions
                float panelW = 500f;
                float rowH = 36f;
                int maxItems = 14;
                float panelH = 60f + (maxItems * rowH) + 40f; // title + items + status

                // Background panel
                var panelGO = new GameObject("Background");
                panelGO.transform.SetParent(wristPanelRoot.transform, false);
                var panelImg = panelGO.AddComponent<UnityEngine.UI.Image>();
                panelImg.color = new Color(0.05f, 0.05f, 0.12f, 0.92f);
                var panelRect = panelGO.GetComponent<RectTransform>();
                panelRect.sizeDelta = new Vector2(panelW, panelH);
                panelRect.anchoredPosition = Vector2.zero;

                // Title
                var titleGO = new GameObject("Title");
                titleGO.transform.SetParent(panelGO.transform, false);
                wristTitleText = titleGO.AddComponent<UnityEngine.UI.Text>();
                wristTitleText.text = "UW2 TRAINER";
                wristTitleText.fontSize = 28;
                wristTitleText.fontStyle = FontStyle.Bold;
                wristTitleText.color = new Color(0.3f, 0.85f, 1f);
                wristTitleText.alignment = TextAnchor.MiddleCenter;
                if (gameFont != null) wristTitleText.font = gameFont;
                var titleRect = titleGO.GetComponent<RectTransform>();
                titleRect.anchorMin = new Vector2(0, 1);
                titleRect.anchorMax = new Vector2(1, 1);
                titleRect.pivot = new Vector2(0.5f, 1);
                titleRect.sizeDelta = new Vector2(0, 50);
                titleRect.anchoredPosition = new Vector2(0, -5);

                // Create item rows
                wristLabels = new UnityEngine.UI.Text[maxItems];
                wristHighlights = new UnityEngine.UI.Image[maxItems];

                for (int i = 0; i < maxItems; i++)
                {
                    float yPos = -55f - (i * rowH);

                    // Highlight background
                    var hlGO = new GameObject("HL_" + i);
                    hlGO.transform.SetParent(panelGO.transform, false);
                    wristHighlights[i] = hlGO.AddComponent<UnityEngine.UI.Image>();
                    wristHighlights[i].color = new Color(0.2f, 0.5f, 1f, 0f); // transparent until selected
                    var hlRect = hlGO.GetComponent<RectTransform>();
                    hlRect.anchorMin = new Vector2(0, 1);
                    hlRect.anchorMax = new Vector2(1, 1);
                    hlRect.pivot = new Vector2(0.5f, 1);
                    hlRect.sizeDelta = new Vector2(-20, rowH - 2);
                    hlRect.anchoredPosition = new Vector2(0, yPos);

                    // Label
                    var lblGO = new GameObject("Lbl_" + i);
                    lblGO.transform.SetParent(hlGO.transform, false);
                    wristLabels[i] = lblGO.AddComponent<UnityEngine.UI.Text>();
                    wristLabels[i].text = "";
                    wristLabels[i].fontSize = 24;
                    wristLabels[i].color = Color.white;
                    wristLabels[i].alignment = TextAnchor.MiddleLeft;
                    if (gameFont != null) wristLabels[i].font = gameFont;
                    var lblRect = lblGO.GetComponent<RectTransform>();
                    lblRect.anchorMin = Vector2.zero;
                    lblRect.anchorMax = Vector2.one;
                    lblRect.offsetMin = new Vector2(15, 0);
                    lblRect.offsetMax = new Vector2(-10, 0);
                }

                // Status text at bottom
                var statusGO = new GameObject("Status");
                statusGO.transform.SetParent(panelGO.transform, false);
                wristStatusText = statusGO.AddComponent<UnityEngine.UI.Text>();
                wristStatusText.text = "";
                wristStatusText.fontSize = 20;
                wristStatusText.color = new Color(0.4f, 1f, 0.4f);
                wristStatusText.alignment = TextAnchor.MiddleCenter;
                if (gameFont != null) wristStatusText.font = gameFont;
                var statusRect = statusGO.GetComponent<RectTransform>();
                statusRect.anchorMin = new Vector2(0, 0);
                statusRect.anchorMax = new Vector2(1, 0);
                statusRect.pivot = new Vector2(0.5f, 0);
                statusRect.sizeDelta = new Vector2(0, 35);
                statusRect.anchoredPosition = new Vector2(0, 5);

                wristPanelRoot.SetActive(false);
                LoggerInstance.Msg("[WRIST] Panel created");
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"[WRIST] Create failed: {ex}");
                wristPanelRoot = null;
            }
        }

        private void BuildWristItems()
        {
            wristItems.Clear();

            // Engine power
            wristItems.Add(new WristItem {
                Label = "Power 2x",
                OnActivate = () => {
                    var e = GameManager.ControllerAircraft?.m_aircraftEngines?[0]?.TryCast<AircraftEngine>();
                    if (e != null) { e.m_forceAtMaxRPM *= 2f; SetStatus($"Power -> {e.m_forceAtMaxRPM:F0}"); }
                }
            });
            wristItems.Add(new WristItem {
                Label = "Power 5x",
                OnActivate = () => {
                    var e = GameManager.ControllerAircraft?.m_aircraftEngines?[0]?.TryCast<AircraftEngine>();
                    if (e != null) { e.m_forceAtMaxRPM *= 5f; SetStatus($"Power -> {e.m_forceAtMaxRPM:F0}"); }
                }
            });

            // Air resistance
            wristItems.Add(new WristItem {
                DynamicLabel = () => "Realistic Glide [" + (maxDragClamp >= 0f ? "ON" : "OFF") + "]",
                OnActivate = () => {
                    maxDragClamp = maxDragClamp >= 0f ? -1f : 0.01f;
                    dragMultiplier = 1f;
                    SetStatus(maxDragClamp >= 0f ? "Realistic Glide ON" : "Realistic Glide OFF");
                }
            });
            wristItems.Add(new WristItem {
                DynamicLabel = () => {
                    string mode = dragMultiplier >= 1f ? "Stock" : dragMultiplier >= 0.5f ? "Reduced" : dragMultiplier >= 0.25f ? "Low" : "Minimal";
                    if (maxDragClamp >= 0f) mode = "Glide";
                    return "Drag: " + mode;
                },
                OnActivate = () => {
                    maxDragClamp = -1f;
                    if (dragMultiplier >= 1f) dragMultiplier = 0.5f;
                    else if (dragMultiplier >= 0.5f) dragMultiplier = 0.25f;
                    else if (dragMultiplier >= 0.25f) dragMultiplier = 0.1f;
                    else dragMultiplier = 1f;
                    string mode = dragMultiplier >= 1f ? "Stock" : dragMultiplier >= 0.5f ? "Reduced" : dragMultiplier >= 0.25f ? "Low" : "Minimal";
                    SetStatus("Drag: " + mode);
                }
            });

            // Weapons
            wristItems.Add(new WristItem {
                Label = "Spawn Weapons",
                OnActivate = () => {
                    try {
                        var ac = GameManager.ControllerAircraft;
                        if (ac == null) return;
                        var wa = ac.gameObject.GetComponentInChildren<Il2CppWeapon.WeaponAttachment>();
                        if (wa != null) { wa.m_forceSpawnAll = true; wa.SpawnWeapons(); }
                        var allT = ac.gameObject.GetComponentsInChildren<Transform>(true);
                        for (int i = 0; i < allT.Length; i++)
                            if (!allT[i].gameObject.activeSelf && allT[i].gameObject.name.ToLower().Contains("machine gun point"))
                                allT[i].gameObject.SetActive(true);
                        var vs = GameManager.VehicleSetup;
                        if (vs != null) { if (vs.m_controllerAircraft == null) vs.m_controllerAircraft = ac; vs.m_isInfiniteAmmo = true; vs.m_gunInitComplete = false; vs.InitializeWeaponSystems(); vs.m_gunInitComplete = true; }
                        SetStatus("Weapons spawned!");
                    } catch (Exception ex) { SetStatus("Spawn failed: " + ex.Message); }
                }
            });

            // Ammo swap
            wristItems.Add(new WristItem {
                Label = "Ammo: Bullets",
                OnActivate = () => { SwapProjectile("cfg_friendly_stallion"); }
            });
            wristItems.Add(new WristItem {
                Label = "Ammo: Grenades",
                OnActivate = () => { SwapProjectile("cfg_friendly_grenadelauncher"); }
            });
            wristItems.Add(new WristItem {
                Label = "Ammo: Darts",
                OnActivate = () => { SwapProjectile("cfg_friendly_dartgun"); }
            });
            wristItems.Add(new WristItem {
                Label = "Ammo: Default (reset)",
                OnActivate = () => {
                    try {
                        var ac = GameManager.ControllerAircraft;
                        if (ac == null) return;
                        var wa = ac.gameObject.GetComponentInChildren<Il2CppWeapon.WeaponAttachment>();
                        if (wa != null) { wa.m_forceSpawnAll = true; wa.SpawnWeapons(); }
                        var vs = GameManager.VehicleSetup;
                        if (vs != null) { if (vs.m_controllerAircraft == null) vs.m_controllerAircraft = ac; vs.m_isInfiniteAmmo = true; vs.m_gunInitComplete = false; vs.InitializeWeaponSystems(); vs.m_gunInitComplete = true; }
                        SetStatus("Weapons reset to default!");
                    } catch { SetStatus("Reset failed"); }
                }
            });

            // Toggles
            wristItems.Add(new WristItem {
                DynamicLabel = () => "Fuel: " + (unlimitedFuel ? "UNLIMITED" : "Normal"),
                OnActivate = () => {
                    unlimitedFuel = !unlimitedFuel;
                    if (playerData != null) playerData.isAircraftHasUnlimitedFuel = unlimitedFuel ? 1 : 0;
                    try { var ac = GameManager.ControllerAircraft; if (ac != null) { ac.SetUnlimitedFuel(unlimitedFuel); ac.RefuelVehicle(); } } catch { }
                    SaveModState();
                    SetStatus("Fuel: " + (unlimitedFuel ? "UNLIMITED" : "Normal"));
                }
            });
            wristItems.Add(new WristItem {
                DynamicLabel = () => "God Mode: " + (godMode ? "ON" : "OFF"),
                OnActivate = () => {
                    godMode = !godMode;
                    if (gameManager != null) gameManager.m_playerIsInvulnerable = godMode;
                    try { var cm = GameManager.CrashManager; if (cm != null) cm.EnableCrash(!godMode); } catch { }
                    SaveModState();
                    SetStatus("God Mode: " + (godMode ? "ON" : "OFF"));
                }
            });

            // Boost
            wristItems.Add(new WristItem {
                DynamicLabel = () => {
                    if (continuousBoost <= 0) return "Boost: OFF";
                    if (continuousBoost <= 2000) return "Boost: Light";
                    if (continuousBoost <= 10000) return "Boost: Medium";
                    if (continuousBoost <= 50000) return "Boost: Heavy";
                    return "Boost: RIDICULOUS";
                },
                OnActivate = () => {
                    if (continuousBoost <= 0) continuousBoost = 2000;
                    else if (continuousBoost <= 2000) continuousBoost = 10000;
                    else if (continuousBoost <= 10000) continuousBoost = 50000;
                    else if (continuousBoost <= 50000) continuousBoost = 200000;
                    else continuousBoost = 0;
                    SetStatus(continuousBoost > 0 ? "Boost: " + continuousBoost : "Boost OFF");
                }
            });

            // HUD
            wristItems.Add(new WristItem {
                DynamicLabel = () => "HUD: " + (showHUD ? "ON" : "OFF"),
                OnActivate = () => { showHUD = !showHUD; SetStatus("HUD " + (showHUD ? "ON" : "OFF")); }
            });

            // Hostile Skies
            wristItems.Add(new WristItem {
                DynamicLabel = () => "Hostile Skies: " + (hostileSkiesActive ? "ON" : "OFF"),
                OnActivate = () => {
                    hostileSkiesActive = !hostileSkiesActive;
                    if (!hostileSkiesActive) { ClearCombatants(); combatModeName = "OFF"; combatSpawnInterval = 0; }
                    SetStatus(hostileSkiesActive ? "Hostile Skies ACTIVE!" : "Hostile Skies OFF");
                }
            });
            wristItems.Add(new WristItem {
                DynamicLabel = () => {
                    string stars = new string('*', threatLevel) + new string('-', 5 - threatLevel);
                    return "Threat: [" + stars + "] " + (threatLevel > threatLevelUnlocked ? "LOCKED" : "");
                },
                OnActivate = () => {
                    threatLevel++;
                    if (threatLevel > threatLevelUnlocked) threatLevel = 1;
                    SetStatus("Threat Level " + threatLevel);
                }
            });
            wristItems.Add(new WristItem {
                DynamicLabel = () => "Kills: " + totalKills + " | Streak: " + killStreak + " | Best: " + bestStreak,
                OnActivate = () => { SetStatus("Session: " + sessionKills + " | Total: " + totalKills + " | Islands: " + islandsDiscovered); }
            });

            // Battle modes (instant action)
            wristItems.Add(new WristItem {
                DynamicLabel = () => "Quick Battle: " + combatModeName + " (" + combatActiveFighters + ")",
                OnActivate = () => {
                    if (combatModeName == "OFF") StartBattleMode("skirmish");
                    else if (combatModeName == "SKIRMISH") StartBattleMode("small");
                    else if (combatModeName == "SMALL BATTLE") StartBattleMode("invasion");
                    else if (combatModeName == "INVASION") StartBattleMode("allout");
                    else { ClearCombatants(); combatModeName = "OFF"; combatSpawnInterval = 0; SetStatus("Battle ended"); }
                }
            });

            // Reset
            wristItems.Add(new WristItem {
                Label = ">> RESET ALL <<",
                OnActivate = () => {
                    maxDragClamp = -1f; dragMultiplier = 1f; enginePowerMultiplier = 1f; continuousBoost = 0;
                    ClearCombatants(); combatModeName = "OFF"; combatSpawnInterval = 0;
                    ResetLiveAircraft();
                    SetStatus("All mods reset to stock");
                }
            });

            wristItemCount = wristItems.Count;
        }

        private void UpdateWristPanel()
        {
            if (wristPanelRoot == null) return;

            // Position panel in front of camera, slightly left and down
            var cam = Camera.main;
            if (cam != null)
            {
                Vector3 pos = cam.transform.position
                    + cam.transform.forward * 0.6f
                    + cam.transform.right * -0.15f
                    + cam.transform.up * -0.1f;
                wristPanelRoot.transform.position = pos;
                wristPanelRoot.transform.rotation = cam.transform.rotation;
            }

            // Scroll so selected row is always visible
            int maxVisible = wristLabels.Length;
            if (wristSelectedRow < wristScrollOffset)
                wristScrollOffset = wristSelectedRow;
            if (wristSelectedRow >= wristScrollOffset + maxVisible)
                wristScrollOffset = wristSelectedRow - maxVisible + 1;

            // Update labels with scroll offset
            for (int i = 0; i < maxVisible; i++)
            {
                int itemIdx = i + wristScrollOffset;
                if (itemIdx < wristItemCount)
                {
                    var item = wristItems[itemIdx];
                    wristLabels[i].text = (itemIdx == wristSelectedRow ? "> " : "  ") +
                        (item.DynamicLabel != null ? item.DynamicLabel() : item.Label);
                    wristLabels[i].color = itemIdx == wristSelectedRow ? new Color(1f, 1f, 0.3f) : Color.white;
                    wristHighlights[i].color = itemIdx == wristSelectedRow ? new Color(0.2f, 0.5f, 1f, 0.3f) : new Color(0, 0, 0, 0);
                    wristLabels[i].gameObject.SetActive(true);
                    wristHighlights[i].gameObject.SetActive(true);
                }
                else
                {
                    wristLabels[i].gameObject.SetActive(false);
                    wristHighlights[i].gameObject.SetActive(false);
                }
            }

            // Show scroll indicator in title
            if (wristTitleText != null)
            {
                if (wristItemCount > maxVisible)
                    wristTitleText.text = "UW2 TRAINER  [" + (wristScrollOffset + 1) + "-" + Math.Min(wristScrollOffset + maxVisible, wristItemCount) + "/" + wristItemCount + "]";
                else
                    wristTitleText.text = "UW2 TRAINER";
            }

            // Status
            if (wristStatusText != null && statusTimer > 0)
                wristStatusText.text = statusMessage;
            else if (wristStatusText != null)
                wristStatusText.text = "";

            // Navigation — face buttons only (sticks are flight controls)
            // X (left) = next, Y (left) = previous, A (right) = activate
            if (wristNavCooldown > 0) { wristNavCooldown -= Time.unscaledDeltaTime; return; }

            bool navDown = false, navUp = false, activate = false;

            // VR face buttons — Button.One on LTouch = X, Button.Two on LTouch = Y
            try { if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch)) navDown = true; } catch { }   // X = next
            try { if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch)) navUp = true; } catch { }     // Y = previous
            try { if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch)) activate = true; } catch { }   // A = activate

            // Keyboard fallback
            if (Input.GetKeyDown(KeyCode.DownArrow)) navDown = true;
            if (Input.GetKeyDown(KeyCode.UpArrow)) navUp = true;
            if (Input.GetKeyDown(KeyCode.Return)) activate = true;

            if (navUp) { wristSelectedRow = (wristSelectedRow - 1 + wristItemCount) % Math.Max(1, wristItemCount); wristNavCooldown = NAV_DELAY; }
            if (navDown) { wristSelectedRow = (wristSelectedRow + 1) % Math.Max(1, wristItemCount); wristNavCooldown = NAV_DELAY; }

            if (activate && wristSelectedRow < wristItemCount)
            {
                try { wristItems[wristSelectedRow].OnActivate?.Invoke(); }
                catch (Exception ex) { SetStatus("Error: " + ex.Message); }
            }
        }

        private void ToggleWristPanel()
        {
            if (wristPanelRoot == null)
            {
                CreateWristPanel();
                BuildWristItems();
            }

            wristPanelVisible = !wristPanelVisible;
            if (wristPanelRoot != null)
            {
                wristPanelRoot.SetActive(wristPanelVisible);
                wristSelectedRow = 0;
                wristScrollOffset = 0;
            }
            // Don't pause game — wrist panel works while flying
        }

        private void SwapProjectile(string projConfigName)
        {
            try
            {
                var ac = GameManager.ControllerAircraft;
                if (ac == null) { SetStatus("Not in aircraft"); return; }

                // Find all WeaponConfigDO in memory to find a matching bullet
                var allWeaponCfg = Resources.FindObjectsOfTypeAll<Il2CppWeapon.WeaponConfigDO>();
                LoggerInstance.Msg($"[WEAP] All WeaponConfigDO in memory: {allWeaponCfg?.Length ?? 0}");
                Il2CppWeapon.WeaponConfigDO sourceCfg = null;
                for (int i = 0; i < (allWeaponCfg?.Length ?? 0); i++)
                {
                    LoggerInstance.Msg($"[WEAP]   WCfg[{i}]: '{allWeaponCfg[i].name}' bullet='{allWeaponCfg[i].m_bulletInstance?.name ?? "null"}'");
                    if (allWeaponCfg[i].name == projConfigName)
                    {
                        sourceCfg = allWeaponCfg[i];
                    }
                }

                // Also search ProjectileConfigDO for the name
                var allProjCfg = Resources.FindObjectsOfTypeAll<Il2CppWeapon.ProjectileConfigDO>();
                for (int i = 0; i < (allProjCfg?.Length ?? 0); i++)
                {
                    LoggerInstance.Msg($"[WEAP]   PCfg[{i}]: '{allProjCfg[i].name}'");
                }

                // Find all Weapon components on the aircraft and swap their config's bullet
                var weapons = ac.gameObject.GetComponentsInChildren<Il2CppWeapon.Weapon>();
                LoggerInstance.Msg($"[WEAP] Weapons on aircraft: {weapons?.Length ?? 0}");
                int swapped = 0;
                for (int i = 0; i < (weapons?.Length ?? 0); i++)
                {
                    var w = weapons[i];
                    LoggerInstance.Msg($"[WEAP]   W[{i}]: '{w.gameObject.name}' pooler={w.m_pooler != null}");

                    // Try to use SetConfig if we have a source
                    if (sourceCfg != null)
                    {
                        try
                        {
                            w.SetConfig(sourceCfg);
                            LoggerInstance.Msg($"[WEAP]   SetConfig applied: '{sourceCfg.name}'");
                            swapped++;
                        }
                        catch (Exception ex) { LoggerInstance.Msg($"[WEAP]   SetConfig failed: {ex.Message}"); }
                    }
                }

                SetStatus(swapped > 0 ? $"Swapped {swapped} weapons to {projConfigName}!" : "Config not found — check log");
            }
            catch (Exception ex) { SetStatus($"Swap failed: {ex.Message}"); LoggerInstance.Error($"[WEAP] {ex}"); }
        }

        private void EquipWeapon(string holsteredName)
        {
            var ac = GameManager.ControllerAircraft;
            if (ac == null) { SetStatus("Not in aircraft"); return; }

            // Find the weapon system and mount point on the aircraft
            var weapAttach = ac.gameObject.GetComponentInChildren<Il2CppWeapon.WeaponAttachment>();
            if (weapAttach == null) { SetStatus("No weapon system on aircraft"); return; }
            var mountPoint = weapAttach.transform.Find("Sidearm Mounting Point");
            if (mountPoint == null) mountPoint = weapAttach.transform; // fallback to Weapon System itself

            // Find the holstered weapon in the scene
            var allGO = Resources.FindObjectsOfTypeAll<GameObject>();
            GameObject holstered = null;
            for (int i = 0; i < allGO.Length; i++)
            {
                if (allGO[i].name == holsteredName)
                {
                    holstered = allGO[i];
                    break;
                }
            }
            if (holstered == null) { SetStatus($"'{holsteredName}' not found in scene"); return; }

            // Reparent the holstered weapon under the aircraft's sidearm mount point
            holstered.transform.SetParent(mountPoint, false);
            holstered.SetActive(true);
            holstered.transform.localPosition = Vector3.zero;
            holstered.transform.localRotation = Quaternion.identity;
            LoggerInstance.Msg($"[WEAP] Parented '{holsteredName}' under '{mountPoint.gameObject.name}' on '{ac.gameObject.name}'");

            // Now initialize the weapon systems
            try
            {
                var vs = GameManager.VehicleSetup;
                if (vs != null)
                {
                    // Make sure VehicleSetup knows about the aircraft
                    if (vs.m_controllerAircraft == null) vs.m_controllerAircraft = ac;
                    vs.m_isInfiniteAmmo = true;
                    vs.m_gunInitComplete = false;
                    vs.InitializeWeaponSystems();
                    vs.m_gunInitComplete = true;
                    LoggerInstance.Msg("[WEAP] InitializeWeaponSystems called after equip");
                }
                else
                {
                    // No VehicleSetup — manually init the FiringMechanisms
                    var manualFms = ac.gameObject.GetComponentsInChildren<Il2CppWeapon.FiringMechanismBase>();
                    for (int i = 0; i < manualFms.Length; i++)
                    {
                        var fm = manualFms[i].TryCast<Il2CppWeapon.FiringMechanism>();
                        if (fm != null)
                        {
                            fm.m_isInfiniteAmmo = true;
                            fm.Reload();
                            LoggerInstance.Msg($"[WEAP] Manually initialized FM: '{fm.gameObject.name}'");
                        }
                    }
                }
            }
            catch (Exception ex) { LoggerInstance.Msg($"[WEAP] Init after equip failed: {ex.Message}"); }

            // Verify
            var fms = ac.gameObject.GetComponentsInChildren<Il2CppWeapon.FiringMechanismBase>();
            LoggerInstance.Msg($"[WEAP] FiringMechanisms on aircraft after equip: {fms?.Length ?? 0}");

            SetStatus($"Equipped {holsteredName}!");
        }

        private void ResetLiveAircraft()
        {
            try
            {
                var ac = GameManager.ControllerAircraft;
                if (ac == null) { SetStatus("Not in aircraft"); return; }

                // Reset engines
                var engines = ac.m_aircraftEngines;
                if (engines != null)
                {
                    for (int i = 0; i < engines.Count; i++)
                    {
                        var eng = engines[i]?.TryCast<AircraftEngine>();
                        if (eng == null) continue;
                        string key = $"engine_{ac.gameObject.name}_{i}";
                        if (origEngineForce.TryGetValue(key, out float origForce))
                        {
                            eng.m_forceAtMaxRPM = origForce;
                            LoggerInstance.Msg($"[ENGINE] {key}: reset to {origForce:F0}");
                        }
                    }
                }

                // Reset physics config
                var rta = ac.gameObject.GetComponent<RigidTargetableAircraft>();
                if (rta != null)
                {
                    var cfg = rta.m_aircraftControllerConfig;
                    if (cfg != null) ResetPhysics(cfg);
                }

                // Also reset configs found via Resources (for next flight)
                foreach (var c in aircraftConfigs) ResetPhysics(c);

                enginePowerMultiplier = 1f;
                SetStatus("Aircraft reset to original");
            }
            catch (Exception ex) { SetStatus($"Reset failed: {ex.Message}"); LoggerInstance.Error($"[ENGINE] {ex}"); }
        }

        /// <summary>
        /// Apply custom physics to the live aircraft (engine + config + aero).
        /// </summary>
        private void ApplyLivePreset(float powerMult, float stallSpeed, float dragMult, float liftMult)
        {
            try
            {
                var ac = GameManager.ControllerAircraft;
                if (ac == null) { SetStatus("Not in aircraft — try from office too"); return; }

                // Engine thrust
                var engines = ac.m_aircraftEngines;
                if (engines != null)
                {
                    for (int i = 0; i < engines.Count; i++)
                    {
                        var eng = engines[i]?.TryCast<AircraftEngine>();
                        if (eng == null) continue;
                        string key = $"engine_{ac.gameObject.name}_{i}";
                        if (!origEngineForce.ContainsKey(key))
                            origEngineForce[key] = eng.m_forceAtMaxRPM;
                        eng.m_forceAtMaxRPM = origEngineForce[key] * powerMult;
                    }
                }

                // Physics config
                var rta = ac.gameObject.GetComponent<RigidTargetableAircraft>();
                if (rta != null)
                {
                    var cfg = rta.m_aircraftControllerConfig;
                    if (cfg != null)
                    {
                        string n = cfg.name ?? "?";
                        if (!origPhysics.ContainsKey(n))
                        {
                            origPhysics[n] = new float[] {
                                cfg.MaxEnginePower, cfg.Lift, cfg.StallSpeed, cfg.RollEffect, cfg.PitchEffect,
                                cfg.YawEffect, cfg.DragIncreaseFactor, cfg.ThrottleChangeSpeed,
                                cfg.BankedYawEffect, cfg.BankedTurnEffect, cfg.AerodynamicEffect,
                                cfg.MaxAerodynamicEffectSpeed, cfg.AutoTurnPitch, cfg.AutoRollLevel,
                                cfg.AutoPitchLevel, cfg.AirBrakesEffect, cfg.WheelTorque, cfg.WheelBrakeTorque,
                                cfg.ZeroLiftSpeed
                            };
                        }
                        var o = origPhysics[n];
                        cfg.MaxEnginePower = o[0] * powerMult;
                        if (stallSpeed >= 0f) cfg.StallSpeed = stallSpeed;
                        cfg.DragIncreaseFactor = Math.Max(o[6] * dragMult, 0.01f);
                        cfg.Lift = o[1] * liftMult;
                    }
                }

                // Also update the Resources-cached configs for consistency
                foreach (var c in aircraftConfigs)
                {
                    try
                    {
                        string n = c.name ?? "?";
                        if (origPhysics.TryGetValue(n, out var o))
                        {
                            c.MaxEnginePower = o[0] * powerMult;
                            if (stallSpeed >= 0f) c.StallSpeed = stallSpeed;
                            c.DragIncreaseFactor = Math.Max(o[6] * dragMult, 0.01f);
                            c.Lift = o[1] * liftMult;
                        }
                    }
                    catch { }
                }

                enginePowerMultiplier = powerMult;
            }
            catch (Exception ex) { SetStatus($"Preset failed: {ex.Message}"); LoggerInstance.Error($"[PRESET] {ex}"); }
        }

        // ================================================================
        // GUI
        // ================================================================
        private void InitStyles()
        {
            if (stylesInit) return;
            stylesInit = true;
            sNormal = new GUIStyle(GUI.skin.label) { fontSize = 16, padding = new RectOffset(10, 10, 3, 3) };
            sNormal.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
            sHighlight = new GUIStyle(GUI.skin.label) { fontSize = 18, padding = new RectOffset(10, 10, 3, 3) };
            sHighlight.normal.textColor = Color.black;
            sHeader = new GUIStyle(GUI.skin.label) { fontSize = 14, padding = new RectOffset(10, 10, 6, 2) };
            sHeader.normal.textColor = new Color(0.4f, 0.8f, 1f);
            sStatus = new GUIStyle(GUI.skin.label) { fontSize = 14, padding = new RectOffset(10, 10, 2, 2) };
            sStatus.normal.textColor = Color.green;
        }

        private void H(string t) => items.Add(new MI { Label = t, IsHeader = true });
        private void B(string t, Action a) => items.Add(new MI { Label = t, OnActivate = a });
        private void S(string t, float v, float min, float max, float step, Action<float> onChange)
        {
            float pct = Mathf.Clamp01((v - min) / (max - min));
            int f = (int)(pct * 10);
            items.Add(new MI
            {
                Label = $"{t}: {v:F1} [{"".PadLeft(f, '=')}>{"".PadLeft(10 - f, '-')}]",
                OnLeft = () => onChange(Mathf.Max(min, v - step)),
                OnRight = () => onChange(Mathf.Min(max, v + step))
            });
        }

        public override void OnGUI()
        {
            // HUD renders even when menu is closed
            if (showHUD && initialized && !showMenu)
            {
                try { RenderHUD(); } catch { }
            }

            if (!showMenu) return;
            try
            {
                InitStyles();
                items.Clear();
                switch (activeTab)
                {
                    case 0: BuildCheats(); break;
                    case 1: BuildFlight(); break;
                    case 2: BuildCombat(); break;
                    case 3: BuildPhysics(); break;
                    case 4: BuildAircraft(); break;
                    case 5: BuildExplore(); break;
                    case 6: BuildInfo(); break;
                    case 7: BuildDiagnostics(); break;
                }
                maxRows = items.Count(m => !m.IsHeader);
                if (selectedRow >= maxRows) selectedRow = Math.Max(0, maxRows - 1);
                Render();
            }
            catch (Exception ex) { showMenu = false; LoggerInstance.Error($"[GUI] {ex.Message}\n{ex.StackTrace}"); }
        }

        private int scrollOffset = 0;

        private void Render()
        {
            float x = 20, y = 20, w = 480;
            int maxVisible = 30; // max rows visible on screen
            int totalItems = items.Count;

            // Calculate scroll offset to keep selected row visible
            // Find which visual line the selected row is at
            int selectedVisualLine = 0;
            int si2 = 0;
            for (int i = 0; i < totalItems; i++)
            {
                if (!items[i].IsHeader)
                {
                    if (si2 == selectedRow) { selectedVisualLine = i; break; }
                    si2++;
                }
                else if (si2 <= selectedRow) selectedVisualLine = i;
            }
            if (selectedVisualLine < scrollOffset) scrollOffset = selectedVisualLine;
            if (selectedVisualLine >= scrollOffset + maxVisible) scrollOffset = selectedVisualLine - maxVisible + 1;
            if (scrollOffset < 0) scrollOffset = 0;

            int visibleCount = Math.Min(totalItems - scrollOffset, maxVisible);

            // Background
            float bgHeight = 70 + visibleCount * 26;
            GUI.color = new Color(0, 0, 0, 0.88f);
            GUI.Box(new Rect(x, y, w, bgHeight), "");
            GUI.color = Color.white;

            // Title
            GUI.Label(new Rect(x + 10, y + 4, w, 24), $"UW2 TRAINER v6  |  {tabNames[activeTab]}  |  LStick=Nav  RStick=Tabs  A=Select", sHeader);
            y += 26;

            // Status
            if (statusTimer > 0) { GUI.Label(new Rect(x + 10, y, w, 20), statusMessage, sStatus); y += 20; }

            // Connection
            GUI.color = initialized ? Color.green : Color.yellow;
            GUI.Label(new Rect(x + 10, y, w, 18), initialized ? "[Connected]" : "[Waiting...]", sStatus);
            GUI.color = Color.white;
            y += 22;

            // Scroll indicator top
            if (scrollOffset > 0)
            {
                GUI.Label(new Rect(x + w / 2 - 20, y - 2, 40, 18), "^^^", sHeader);
            }

            // Items (scrolled) — track selectable index across ALL items, not just visible
            int si = 0;
            // Count selectable items before scroll offset
            for (int i = 0; i < scrollOffset; i++)
                if (!items[i].IsHeader) si++;

            for (int i = scrollOffset; i < Math.Min(totalItems, scrollOffset + maxVisible); i++)
            {
                var item = items[i];
                if (item.IsHeader)
                {
                    GUI.Label(new Rect(x + 6, y, w, 24), item.Label, sHeader);
                }
                else
                {
                    if (si == selectedRow)
                    {
                        GUI.color = new Color(0.2f, 0.8f, 1f, 0.9f);
                        GUI.Box(new Rect(x, y, w, 24), "");
                        GUI.color = Color.white;
                        GUI.Label(new Rect(x + 6, y, w, 24), "> " + item.Label, sHighlight);
                    }
                    else
                    {
                        GUI.Label(new Rect(x + 6, y, w, 24), "  " + item.Label, sNormal);
                    }
                    si++;
                }
                y += 24;
            }

            // Scroll indicator bottom
            if (scrollOffset + maxVisible < totalItems)
            {
                GUI.Label(new Rect(x + w / 2 - 20, y, 40, 18), "vvv", sHeader);
            }
        }

        // ================================================================
        // VEHICLE STATS HUD
        // ================================================================
        private void InitHudStyles()
        {
            if (hudStylesInit) return;
            sHudLabel = new GUIStyle(GUI.skin.label) { fontSize = 13, padding = new RectOffset(4, 2, 0, 0) };
            sHudLabel.normal.textColor = new Color(0.7f, 0.75f, 0.8f, 1f);
            sHudValue = new GUIStyle(GUI.skin.label) { fontSize = 13, padding = new RectOffset(2, 6, 0, 0) };
            sHudValue.normal.textColor = new Color(0.4f, 1f, 0.6f, 1f);
            sHudHeader = new GUIStyle(GUI.skin.label) { fontSize = 11, padding = new RectOffset(4, 4, 0, 0) };
            sHudHeader.normal.textColor = new Color(0.3f, 0.85f, 1f, 1f);
            hudStylesInit = true;
        }

        private void RenderHUD()
        {
            InitHudStyles();

            float hudW = 310, lineH = 18, pad = 6;
            float hudX = Screen.width - hudW - 15;
            float hudY = 15;
            float rowY = hudY;

            // Gather data
            string aircraftName = "---";
            string engineType = "---";
            float speed = 0, speedKts = 0, alt = 0, thrust = 0, engPower = 0;
            float rpm = 0, maxRpm = 0, normalRpm = 0;
            float pitch = 0, roll = 0;
            float drag = 0, aeroFactor = 0, maxAeroSpeed = 0;
            float liveDrag = 0, liveAngDrag = 0; // actual rigidbody drag values (what Unity uses)
            float fuelPct = -1;
            float forceAtMax = 0;
            float airSpeedLimit = 0;
            float configPower = 0, configDrag = 0, configLift = 0;
            float windSpeed = 0;
            float boostVal = continuousBoost;
            float powerMult = enginePowerMultiplier;
            // Rotor-specific
            float rotorRPM = -1, rotorThrottle = -1;
            bool isRotor = false;

            try
            {
                var ac = GameManager.ControllerAircraft;
                if (ac == null) return; // no HUD if not in aircraft

                aircraftName = ac.gameObject?.name ?? "?";

                // Fuel
                try { fuelPct = (ac.m_fuelTankSize > 0) ? (ac.m_fuelOnBoard / ac.m_fuelTankSize * 100f) : -1; } catch { }

                // Engines
                var engines = ac.m_aircraftEngines;
                if (engines != null && engines.Count > 0)
                {
                    var eng = engines[0]?.TryCast<AircraftEngine>();
                    if (eng != null)
                    {
                        rpm = eng.m_currentRPM;
                        maxRpm = eng.m_maxRPM;
                        normalRpm = eng.m_normalizedRPM;
                        forceAtMax = eng.m_forceAtMaxRPM;
                        thrust = eng.m_thrust; // read from ENGINE, not physics controller
                        airSpeedLimit = eng.AirSpeedThrustLimit;

                        var rotor = eng.TryCast<EngineRotor>();
                        if (rotor != null)
                        {
                            engineType = "Rotor";
                            isRotor = true;
                            rotorRPM = rotor.m_rotorRPM;
                            rotorThrottle = rotor.m_currentThrottle;
                        }
                        else if (eng.TryCast<EngineCombustion>() != null) engineType = "Combustion";
                        else engineType = "Unknown";
                    }
                }

                // Physics controller
                var rta = ac.gameObject.GetComponent<RigidTargetableAircraft>();
                if (rta != null)
                {
                    var phys = rta.m_aircraftSystem?.TryCast<AircraftPhysicsController>();
                    if (phys != null)
                    {
                        speed = phys.m_forwardSpeed;
                        speedKts = phys.m_forwardSpeedInKnots;
                        alt = phys.m_altitude;
                        // thrust comes from engine (eng.m_thrust), not pc — pc.m_thrust is always 0
                        engPower = phys.m_enginePower;
                        pitch = phys.m_pitchRadians * 57.2958f; // rad to deg
                        roll = phys.m_rollRadians * 57.2958f;
                        drag = phys.m_originalDrag;
                        aeroFactor = phys.m_aeroFactor;

                        var cfg = phys.m_config;
                        if (cfg != null)
                        {
                            configPower = cfg.MaxEnginePower;
                            configDrag = cfg.DragIncreaseFactor;
                            configLift = cfg.Lift;
                            maxAeroSpeed = cfg.MaxAerodynamicEffectSpeed;
                        }
                    }
                }

                // Live rigidbody drag (what Unity actually uses for deceleration)
                try
                {
                    var rb = ac.gameObject.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        liveDrag = rb.drag;
                        liveAngDrag = rb.angularDrag;
                    }
                }
                catch { }

                // Wind
                try { windSpeed = AlwaysLoadedManager.WindSpeed; } catch { }
            }
            catch { return; }

            // Count rows for background height
            int rows = 16;
            if (isRotor) rows += 2;
            if (boostVal > 0 || powerMult != 1f || maxDragClamp >= 0f) rows += 1;
            if (glideTracking) rows += 2;
            else if (glideMaxDist > 10f) rows += 1;
            float hudH = pad * 2 + 22 + rows * lineH + 4;

            // Background
            GUI.color = new Color(0.05f, 0.07f, 0.1f, 0.82f);
            GUI.Box(new Rect(hudX, hudY, hudW, hudH), "");
            GUI.Box(new Rect(hudX, hudY, hudW, hudH), ""); // double for opacity
            GUI.color = Color.white;

            // Title bar
            rowY += pad;
            GUI.Label(new Rect(hudX, rowY, hudW, 20), $"{aircraftName}  [{engineType}]", sHudHeader);
            rowY += 22;

            // Draw line helper
            void HudRow(string label, string value, Color? valColor = null)
            {
                GUI.Label(new Rect(hudX + pad, rowY, 140, lineH), label, sHudLabel);
                var prevColor = sHudValue.normal.textColor;
                if (valColor.HasValue) sHudValue.normal.textColor = valColor.Value;
                GUI.Label(new Rect(hudX + 140, rowY, hudW - 140 - pad, lineH), value, sHudValue);
                if (valColor.HasValue) sHudValue.normal.textColor = prevColor;
                rowY += lineH;
            }

            // --- Flight ---
            Color green = new Color(0.4f, 1f, 0.6f);
            Color yellow = new Color(1f, 0.9f, 0.3f);
            Color red = new Color(1f, 0.4f, 0.3f);
            Color cyan = new Color(0.3f, 0.9f, 1f);

            HudRow("SPEED", $"{speedKts:F0} kts  ({speed:F1} m/s)", cyan);
            HudRow("ALTITUDE", $"{alt:F0} m", cyan);

            // Thrust from engine (actual force being applied)
            float actualForce = forceAtMax * normalRpm; // real force = forceAtMaxRPM * normalized RPM
            Color thrustColor = thrust > 0.5f ? green : thrust > 0.1f ? yellow : red;
            HudRow("ENG THRUST", $"{thrust:F2}  (force: {actualForce:F0})", thrustColor);
            HudRow("AIRSPD LIMIT", $"{airSpeedLimit:F3}", airSpeedLimit > 0.7f ? green : yellow);

            // RPM with bar feel
            float rpmPct = maxRpm > 0 ? (rpm / maxRpm * 100f) : 0;
            Color rpmColor = rpmPct > 90 ? red : rpmPct > 60 ? yellow : green;
            HudRow("RPM", $"{rpm:F0} / {maxRpm:F0}  ({rpmPct:F0}%)", rpmColor);

            // Fuel
            if (fuelPct >= 0)
            {
                Color fuelColor = unlimitedFuel ? cyan : fuelPct > 50 ? green : fuelPct > 20 ? yellow : red;
                string fuelStr = unlimitedFuel ? $"{fuelPct:F0}% [INF]" : $"{fuelPct:F0}%";
                HudRow("FUEL", fuelStr, fuelColor);
            }
            else HudRow("FUEL", "N/A");

            // Attitude
            HudRow("PITCH", $"{pitch:F1} deg");
            HudRow("ROLL", $"{roll:F1} deg");

            // Aero
            HudRow("AERO FACTOR", $"{aeroFactor:F3}");
            HudRow("MAX AERO SPD", $"{maxAeroSpeed:F0}");
            HudRow("CFG POWER", $"{configPower:F0}");
            HudRow("FORCE@MAX", $"{forceAtMax:F0}");

            // Drag — live values (what Unity is actually applying)
            Color dragColor = liveDrag > drag * 2 ? red : liveDrag > drag * 1.2f ? yellow : green;
            HudRow("RB DRAG", $"{liveDrag:F4}  (base: {drag:F4})", dragColor);
            HudRow("RB ANG DRAG", $"{liveAngDrag:F4}");
            HudRow("CFG DRAG INC", $"{configDrag:F3}");

            // Wind
            Color windColor = windSpeed > 15 ? red : windSpeed > 5 ? yellow : green;
            HudRow("WIND", $"{windSpeed:F1}", windColor);

            // Rotor-specific
            if (isRotor)
            {
                HudRow("ROTOR RPM", $"{rotorRPM:F0}", cyan);
                HudRow("COLLECTIVE", $"{rotorThrottle:F2}", cyan);
            }

            // Active mods indicator
            if (boostVal > 0 || powerMult != 1f || maxDragClamp >= 0f)
            {
                string mods = "";
                if (powerMult != 1f) mods += $"PWR:{powerMult:F1}x ";
                if (boostVal > 0) mods += $"BOOST:{boostVal:F0} ";
                if (maxDragClamp >= 0f) mods += "GLIDE ";
                HudRow("ACTIVE MODS", mods.Trim(), yellow);
            }

            // Glide tracker
            if (glideTracking)
            {
                float altLoss = glideStartAlt - alt;
                float ratio = altLoss > 1f ? glideMaxDist / altLoss : 0f;
                HudRow("GLIDE DIST", $"{glideMaxDist:F0}m  (from {glideStartSpeed:F0} m/s)", cyan);
                HudRow("GLIDE L/D", $"{ratio:F1}  (alt lost: {altLoss:F0}m)", altLoss > 100 ? yellow : cyan);
            }
            else if (glideMaxDist > 10f)
            {
                float altLoss = glideStartAlt - alt;
                float ratio = altLoss > 1f ? glideMaxDist / altLoss : 0f;
                HudRow("LAST GLIDE", $"{glideMaxDist:F0}m  L/D: {ratio:F1}", green);
            }

            // Separator + hint
            GUI.Label(new Rect(hudX, rowY + 2, hudW, 14), "F2=Toggle  |  Space=Menu", sHudHeader);
        }

        // ================================================================
        // MENU BUILDERS
        // ================================================================

        private void BuildCheats()
        {
            H("--- TOGGLES ---");
            B($"Unlimited Fuel [{(unlimitedFuel ? "ON" : "OFF")}]", () => {
                unlimitedFuel = !unlimitedFuel;
                if (playerData != null) playerData.isAircraftHasUnlimitedFuel = unlimitedFuel ? 1 : 0;
                // Also set directly on current vehicle
                try
                {
                    var ac = GameManager.ControllerAircraft;
                    if (ac != null)
                    {
                        ac.SetUnlimitedFuel(unlimitedFuel);
                        ac.RefuelVehicle();
                    }
                }
                catch { }
                SaveModState();
                SetStatus($"Fuel: {(unlimitedFuel ? "ON" : "OFF")}");
            });
            B($"Unlimited Ammo [{(unlimitedAmmo ? "ON" : "OFF")}]", () => { unlimitedAmmo = !unlimitedAmmo; if (playerData != null) playerData.isAircraftHasUnlimitedAmmo = unlimitedAmmo ? 1 : 0; SaveModState(); SetStatus($"Ammo: {(unlimitedAmmo ? "ON" : "OFF")}"); });
            B($"God Mode [{(godMode ? "ON" : "OFF")}]", () => {
                godMode = !godMode;
                try
                {
                    if (gameManager != null) gameManager.m_playerIsInvulnerable = godMode;
                    var cm = GameManager.CrashManager;
                    if (cm != null) cm.EnableCrash(!godMode);
                }
                catch { }
                SaveModState();
                SetStatus($"God: {(godMode ? "ON — invulnerable + no crashes" : "OFF")}");
            });

            H($"--- MONEY (current: ${(playerData != null ? Math.Max(0, playerData.money).ToString() : "?")}) ---");
            B("Add $100,000", () => AddMoney(100000));
            B("Set to $1,000,000", () => { if (playerData != null) { playerData.money = 1000000; SetStatus("$1,000,000"); } });
            B("Set to $9,999,999 (max safe)", () => { if (playerData != null) { playerData.money = 9999999; SetStatus("$9,999,999"); } });
            B("Fix Negative Money", () => { if (playerData != null && playerData.money < 0) { playerData.money = 500000; SetStatus("Fixed! $500,000"); } else SetStatus("Money is fine"); });

            H("--- UNLOCKS ---");
            B($"Unlock All Vehicles [{(allVehiclesUnlocked ? "ON" : "OFF")}]", () => { allVehiclesUnlocked = !allVehiclesUnlocked; if (allVehiclesUnlocked) UnlockVehicles(); SaveModState(); SetStatus($"Vehicle unlock: {(allVehiclesUnlocked ? "ON — persists across restarts" : "OFF")}"); });
            B("Enable Dev Cheats (RISKY)", () => {
                try { gameManager.m_enableGameCheats = true; gameManager.m_enablePlayerDataCheats = true; gameManager.ApplyPlayerDataCheats(); SetStatus("Dev cheats ON — may break saves!"); }
                catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); }
            });

            H("--- WEATHER ---");
            B("Day", () => SetWeather(0, "Day"));
            B("Sunset", () => SetWeather(1, "Sunset"));
            B("Night", () => SetWeather(2, "Night"));
            B("Overcast", () => SetWeather(3, "Overcast"));
            B("Auto (default)", () => {
                try { AlwaysLoadedManager.SetSkyState(SKY_CONDITION.Day); } catch { }
                if (gameManager != null) gameManager.m_forceWeather = false;
                SetStatus("Weather: Auto");
            });

            H("--- VEHICLE SWAP (pick before mission) ---");
            B("Phoenix (ultralight)", () => SwapVehicle(1, "Phoenix"));
            B("Stallion (biplane)", () => SwapVehicle(2, "Stallion"));
            B("Comet (aerobatic)", () => SwapVehicle(4, "Comet"));
            B("NewHawk (GA)", () => SwapVehicle(8, "NewHawk"));
            B("Dragonfly (helicopter)", () => SwapVehicle(16, "Dragonfly"));
            B("Kodiak (heavy)", () => SwapVehicle(32, "Kodiak"));
            B("Lightning (CUT - crashes!)", () => SwapVehicle(64, "Lightning"));

            H("--- SAVE ---");
            H("  Start any mission to auto-save changes");
            B("Manual Save (if not entering mission)", () => TriggerSave());
            B("Create 100% Dev Save (slot 2)", () => {
                if (playerData == null) { SetStatus("Not loaded"); return; }
                try
                {
                    playerData.money = 9999999;
                    playerData.vehicleUnlocked = playerData.vehicleUnlocked | 63;
                    playerData.vehicleOwned = playerData.vehicleOwned | 63;
                    playerData.officeUnlocked = playerData.officeUnlocked | 0x1FF;
                    playerData.officeOwned = playerData.officeOwned | 0x1FF;
                    playerData.isAircraftHasUnlimitedFuel = 1;
                    playerData.isAircraftHasUnlimitedAmmo = 1;
                    var pdi = GameManager.PlayerDataInterface;
                    var model = pdi?.TryCast<PlayerDataModel>();
                    model?.SetAndSavePlayerData(2);
                    SetStatus("Dev save -> slot 2!");
                }
                catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); }
            });

            H("--- OTHER ---");
            B("Enable Multiplayer UI", () => {
                try { gameManager.m_enableMultiplayerUI = true; SetStatus("Multiplayer UI enabled — check projector"); }
                catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); }
            });
            B("Debug Canvas", () => { try { gameManager.m_showDebugCanvas = true; SetStatus("Debug canvas enabled"); } catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); } });
            B("Disable Crashes", () => {
                try { var cm = GameManager.CrashManager; if (cm != null) { cm.EnableCrash(false); SetStatus("Crashes disabled!"); } else SetStatus("CrashManager null"); }
                catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); }
            });
            B("Unlock All Missions (dev)", () => {
                try { gameManager.m_unlockAllAircrafts = true; gameManager.m_unlockAllMissionCategories = true; gameManager.m_unlockAllMissionTypes = true; SetStatus("All missions unlocked"); }
                catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); }
            });
        }

        private void BuildFlight()
        {
            H("--- SPEED ---");
            try
            {
                var ac = GameManager.ControllerAircraft;
                if (ac != null)
                {
                    var engines = ac.m_aircraftEngines;
                    if (engines != null && engines.Count > 0)
                    {
                        var eng = engines[0]?.TryCast<AircraftEngine>();
                        if (eng != null)
                        {
                            H($"  Force={eng.m_forceAtMaxRPM:F0}  RPM={eng.m_maxRPM:F0}  Limit={eng.AirSpeedThrustLimit * 100f:F0}%");

                            // --- ENGINE POWER ---
                            H("  -- Engine Power --");
                            B("More Power (2x)", () => {
                                var e = GameManager.ControllerAircraft?.m_aircraftEngines?[0]?.TryCast<AircraftEngine>();
                                if (e != null) { e.m_forceAtMaxRPM *= 2f; SetStatus($"Power -> {e.m_forceAtMaxRPM:F0}"); }
                            });
                            B("Much More Power (5x)", () => {
                                var e = GameManager.ControllerAircraft?.m_aircraftEngines?[0]?.TryCast<AircraftEngine>();
                                if (e != null) { e.m_forceAtMaxRPM *= 5f; SetStatus($"Power -> {e.m_forceAtMaxRPM:F0}"); }
                            });

                            // --- AIR RESISTANCE ---
                            string dragStatus;
                            if (maxDragClamp >= 0f) dragStatus = "Realistic Glide";
                            else if (dragMultiplier >= 1f) dragStatus = "Stock";
                            else if (dragMultiplier >= 0.5f) dragStatus = "Reduced";
                            else if (dragMultiplier >= 0.25f) dragStatus = "Low";
                            else dragStatus = "Minimal";
                            H($"  -- Air Resistance [{dragStatus}] --");
                            B("Stock", () => { dragMultiplier = 1f; maxDragClamp = -1f; SetStatus("Air resistance: Stock"); });
                            B("Realistic Glide (no engine-off penalty)", () => {
                                maxDragClamp = 0.01f; dragMultiplier = 1f;
                                SetStatus("Realistic glide — drag won't increase when engine cuts");
                            });
                            B("Reduced", () => { dragMultiplier = 0.5f; maxDragClamp = -1f; SetStatus("Air resistance: Reduced"); });
                            B("Low", () => { dragMultiplier = 0.25f; maxDragClamp = -1f; SetStatus("Air resistance: Low"); });
                            B("Minimal", () => { dragMultiplier = 0.1f; maxDragClamp = -1f; SetStatus("Air resistance: Minimal"); });

                            // --- ADVANCED ---
                            H("  -- Advanced --");
                            B("Uncap RPM", () => {
                                var e = GameManager.ControllerAircraft?.m_aircraftEngines?[0]?.TryCast<AircraftEngine>();
                                if (e != null) {
                                    float target = Math.Max(e.m_maxRPM * 3f, e.m_unclampedRPM * 1.5f);
                                    e.m_maxRPM = target;
                                    SetStatus($"RPM uncapped -> {e.m_maxRPM:F0}");
                                }
                            });
                            B("ALL MAX (5x power + realistic glide)", () => {
                                try {
                                    var a = GameManager.ControllerAircraft;
                                    if (a == null) return;
                                    var engs = a.m_aircraftEngines;
                                    if (engs == null) return;
                                    for (int i = 0; i < engs.Count; i++) {
                                        var e = engs[i]?.TryCast<AircraftEngine>();
                                        if (e == null) continue;
                                        e.m_maxRPM = Math.Max(e.m_maxRPM * 3f, e.m_unclampedRPM * 1.5f);
                                        e.m_forceAtMaxRPM *= 5f;
                                        var flat = new AnimationCurve();
                                        flat.AddKey(0f, 1f);
                                        flat.AddKey(1000f, 1f);
                                        e.ForceAppliedVSAirspeedKTS = flat;
                                    }
                                    maxDragClamp = 0.01f; dragMultiplier = 1f;
                                    SetStatus("ALL MAX — 5x power + realistic glide!");
                                } catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); }
                            });
                            B("Reset to Stock", () => { try { maxDragClamp = -1f; dragMultiplier = 1f; enginePowerMultiplier = 1f; continuousBoost = 0; ResetLiveAircraft(); SetStatus("Reset to stock"); } catch (Exception ex) { SetStatus($"Reset failed: {ex.Message}"); } });
                        }
                    }
                    else H("  Not in aircraft");
                }
                else H("  Not in aircraft");
            }
            catch { H("  Not in aircraft"); }

            H($"--- BOOST ({continuousBoost:F0}) ---");
            B("OFF", () => { continuousBoost = 0; SetStatus("Boost OFF"); });
            B("Light", () => { continuousBoost = 2000; SetStatus("Boost: Light"); });
            B("Medium", () => { continuousBoost = 10000; SetStatus("Boost: Medium"); });
            B("Heavy", () => { continuousBoost = 50000; SetStatus("Boost: Heavy"); });
            B("RIDICULOUS", () => { continuousBoost = 200000; SetStatus("Boost: RIDICULOUS"); });
        }

        private void BuildCombat()
        {
            H("--- WEAPONS ---");
            B("Force Spawn All Weapons", () => {
                try
                {
                    var ac = GameManager.ControllerAircraft;
                    if (ac == null) { SetStatus("Not in aircraft"); return; }
                    var weapAttach = ac.gameObject.GetComponentInChildren<Il2CppWeapon.WeaponAttachment>();
                    if (weapAttach == null) { SetStatus("No weapon system"); return; }
                    weapAttach.m_forceSpawnAll = true;
                    weapAttach.SpawnWeapons();
                    var fmsOnAc = ac.gameObject.GetComponentsInChildren<Il2CppWeapon.FiringMechanismBase>();
                    LoggerInstance.Msg($"[WEAP] Force spawn: {fmsOnAc?.Length ?? 0} weapons");
                    // Activate gun mount points only (NOT muzzle flashes — handler controls those)
                    var allT = ac.gameObject.GetComponentsInChildren<Transform>(true);
                    for (int i = 0; i < allT.Length; i++)
                    {
                        if (!allT[i].gameObject.activeSelf)
                        {
                            string gn = allT[i].gameObject.name.ToLower();
                            if (gn.Contains("machine gun point"))
                                allT[i].gameObject.SetActive(true);
                        }
                    }
                    // Initialize
                    var vs = GameManager.VehicleSetup;
                    if (vs != null) { if (vs.m_controllerAircraft == null) vs.m_controllerAircraft = ac; vs.m_isInfiniteAmmo = true; vs.m_gunInitComplete = false; vs.InitializeWeaponSystems(); vs.m_gunInitComplete = true; }
                    SetStatus($"Weapons spawned! ({fmsOnAc?.Length ?? 0} found)");
                }
                catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); LoggerInstance.Error($"[WEAP] {ex}"); }
            });

            H("--- AMMO SWAP ---");
            B("Swap: Grenades", () => { SwapProjectile("cfg_friendly_grenadelauncher"); });
            B("Swap: Darts", () => { SwapProjectile("cfg_friendly_dartgun"); });
            B("Swap: Stallion Bullets", () => { SwapProjectile("cfg_friendly_stallion"); });
            B("Swap: Default (reset weapons)", () => {
                try {
                    var ac = GameManager.ControllerAircraft;
                    if (ac == null) { SetStatus("Not in aircraft"); return; }
                    var wa = ac.gameObject.GetComponentInChildren<Il2CppWeapon.WeaponAttachment>();
                    if (wa != null) { wa.m_forceSpawnAll = true; wa.SpawnWeapons(); }
                    var vs = GameManager.VehicleSetup;
                    if (vs != null) { if (vs.m_controllerAircraft == null) vs.m_controllerAircraft = ac; vs.m_isInfiniteAmmo = true; vs.m_gunInitComplete = false; vs.InitializeWeaponSystems(); vs.m_gunInitComplete = true; }
                    SetStatus("Weapons reset to default!");
                } catch (Exception ex) { SetStatus($"Reset failed: {ex.Message}"); }
            });

            B("Activate Laser Designator", () => {
                try
                {
                    var ac = GameManager.ControllerAircraft;
                    if (ac == null) { SetStatus("Not in aircraft"); return; }
                    var weapAttach = ac.gameObject.GetComponentInChildren<Il2CppWeapon.WeaponAttachment>();
                    if (weapAttach == null) { SetStatus("No weapon system"); return; }
                    var laser = weapAttach.m_laserDesignator;
                    if (laser != null)
                    {
                        laser.SetActive(true);
                        SetStatus("Laser designator activated!");
                        LoggerInstance.Msg("[WEAP] Laser designator activated");
                    }
                    else SetStatus("No laser designator on this aircraft");
                }
                catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); }
            });

            H("--- HANDHELD WEAPONS ---");
            B("Equip Grenade Launcher", () => {
                try { EquipWeapon("Holstered Grenade Launcher"); }
                catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); }
            });
            B("Equip Dart Gun", () => {
                try { EquipWeapon("Holstered Dart Gun"); }
                catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); }
            });

            H($"--- BATTLE MODE [{combatModeName}] ({combatActiveFighters} active) ---");
            B("Scan Combat Prefabs", () => { ScanCombatPrefabs(); });
            B("Skirmish (2 fighters)", () => { StartBattleMode("skirmish"); });
            B("Small Battle (4 fighters)", () => { StartBattleMode("small"); });
            B("Invasion (8 fighters)", () => { StartBattleMode("invasion"); });
            B("ALL OUT WAR (15 fighters)", () => { StartBattleMode("allout"); });
            B("End Battle", () => { ClearCombatants(); activeDrones.Clear(); combatModeName = "OFF"; combatSpawnInterval = 0; SetStatus("Battle ended"); });

            H($"--- MUSIC [{(hsMusicPlaying ? "HOSTILE SKIES" : "ORIGINAL")}] ---");
            if (!hsMusicPlaying)
            {
                B("Switch to Hostile Skies Music", () => { PlayHSMusic(); });
            }
            else
            {
                B("Switch to Original Music", () => { StopHSMusic(); });
                string trackName = "?";
                try { if (hsMusicClips.Count > 0 && hsMusicCurrentTrack < hsMusicClips.Count) trackName = hsMusicClips[hsMusicCurrentTrack].name; } catch { }
                H($"  Now playing: {trackName}");
                B("Next Track", () => {
                    if (hsMusicClips.Count == 0) return;
                    hsMusicCurrentTrack = (hsMusicCurrentTrack + 1) % hsMusicClips.Count;
                    hsMusicSource.clip = hsMusicClips[hsMusicCurrentTrack];
                    hsMusicSource.Play();
                    SetStatus($"Playing: {hsMusicClips[hsMusicCurrentTrack].name}");
                });
                B("Previous Track", () => {
                    if (hsMusicClips.Count == 0) return;
                    hsMusicCurrentTrack = (hsMusicCurrentTrack - 1 + hsMusicClips.Count) % hsMusicClips.Count;
                    hsMusicSource.clip = hsMusicClips[hsMusicCurrentTrack];
                    hsMusicSource.Play();
                    SetStatus($"Playing: {hsMusicClips[hsMusicCurrentTrack].name}");
                });
            }
            B($"Volume: {(int)(hsMusicVolume * 100)}%", () => {
                hsMusicVolume += 0.1f;
                if (hsMusicVolume > 1.0f) hsMusicVolume = 0.1f;
                if (hsMusicSource != null) hsMusicSource.volume = hsMusicVolume;
                SetStatus($"Music volume: {(int)(hsMusicVolume * 100)}%");
            });

            H($"--- HOSTILE SKIES (prefabs: {(enemyPrefabsReady ? "READY" : "not loaded — play a combat mission!")}) ---");
            if (enemyPrefabsReady)
            {
                string messerName = "no"; try { messerName = cachedEnemyMesser != null ? cachedEnemyMesser.name : "no"; } catch { messerName = "cached(err)"; }
                string wolfName = "no"; try { wolfName = cachedEnemyWolf != null ? cachedEnemyWolf.name : "no"; } catch { wolfName = "cached(err)"; }
                H($"  Cached: messer={messerName} wolf={wolfName} +{cachedFighterPrefabs.Count} others");
            }
            B("Load Enemy Prefabs", () => {
                if (enemyPrefabsReady) { SetStatus($"Already loaded!"); return; }
                if (!string.IsNullOrEmpty(savedCombatSceneName) && savedCombatSceneIndex > 0)
                {
                    // Load saved combat scene additively to grab prefabs
                    LoggerInstance.Msg($"[COMBAT] Loading saved combat scene '{savedCombatSceneName}' (index={savedCombatSceneIndex}) additively...");
                    combatSceneAutoLoaded = true;
                    try
                    {
                        UnityEngine.SceneManagement.SceneManager.LoadScene(savedCombatSceneIndex, UnityEngine.SceneManagement.LoadSceneMode.Additive);
                        SetStatus("Loading combat scene for prefabs... wait a few seconds then check");
                    }
                    catch (Exception ex) { SetStatus($"Scene load failed: {ex.Message}"); combatSceneAutoLoaded = false; }
                }
                else
                {
                    SetStatus("No saved scene — play a combat mission first!");
                }
            });
            B("Spawn 1 Enemy Fighter", () => {
                if (!enemyPrefabsReady) { LoadEnemyPrefabs(); SetStatus("Loading prefabs first — try again in a few seconds"); return; }
                var ac = GameManager.ControllerAircraft;
                if (ac == null) { SetStatus("Not in aircraft"); return; }
                float angle = UnityEngine.Random.Range(0f, 360f);
                Vector3 offset = Quaternion.Euler(0, angle, 0) * Vector3.forward * UnityEngine.Random.Range(300f, 500f);
                SpawnEnemyFighter(ac.transform.position + offset + Vector3.up * UnityEngine.Random.Range(0f, 80f));
            });
            B("Spawn 3 Enemy Fighters", () => {
                if (!enemyPrefabsReady) { LoadEnemyPrefabs(); SetStatus("Loading prefabs first — try again in a few seconds"); return; }
                var ac = GameManager.ControllerAircraft;
                if (ac == null) { SetStatus("Not in aircraft"); return; }
                for (int i = 0; i < 3; i++)
                {
                    float angle = (360f / 3) * i + UnityEngine.Random.Range(-20f, 20f);
                    Vector3 offset = Quaternion.Euler(0, angle, 0) * Vector3.forward * UnityEngine.Random.Range(300f, 500f);
                    SpawnEnemyFighter(ac.transform.position + offset + Vector3.up * UnityEngine.Random.Range(0f, 80f));
                }
            });
            B("DEEP SCAN: Everything Combat", () => {
                try
                {
                    LoggerInstance.Msg("[COMBAT] === DEEP COMBAT SCAN ===");

                    // 1. All Targetable objects
                    var allTargetable = Resources.FindObjectsOfTypeAll<Il2CppActor.Targetable>();
                    LoggerInstance.Msg($"[COMBAT] Targetable objects: {allTargetable?.Length ?? 0}");
                    for (int i = 0; i < (allTargetable?.Length ?? 0); i++)
                    {
                        try
                        {
                            var t = allTargetable[i];
                            var rootGO = t.transform.root.gameObject;
                            var aiCtrl = rootGO.GetComponentInChildren<Il2CppAi.AiAircraftController>(true);
                            var fm = rootGO.GetComponentsInChildren<Il2CppWeapon.FiringMechanismBase>(true);
                            LoggerInstance.Msg($"[COMBAT]   Target[{i}]: '{rootGO.name}' active={rootGO.activeSelf} hasAI={aiCtrl != null} weapons={fm?.Length ?? 0} type={t.GetIl2CppType().Name}");
                        }
                        catch { }
                    }

                    // 2. All CombatVehicle
                    var allCV = Resources.FindObjectsOfTypeAll<Il2CppTargetableSystem.CombatVehicle>();
                    LoggerInstance.Msg($"[COMBAT] CombatVehicle: {allCV?.Length ?? 0}");
                    for (int i = 0; i < (allCV?.Length ?? 0); i++)
                    {
                        try { LoggerInstance.Msg($"[COMBAT]   CV[{i}]: '{allCV[i].transform.root.gameObject.name}' active={allCV[i].gameObject.activeSelf}"); } catch { }
                    }

                    // 3. All SimpleTurret
                    var allTurrets = Resources.FindObjectsOfTypeAll<Il2CppTurret.SimpleTurret>();
                    LoggerInstance.Msg($"[COMBAT] SimpleTurret: {allTurrets?.Length ?? 0}");
                    for (int i = 0; i < (allTurrets?.Length ?? 0); i++)
                    {
                        try { LoggerInstance.Msg($"[COMBAT]   Turret[{i}]: '{allTurrets[i].gameObject.name}' root='{allTurrets[i].transform.root.gameObject.name}' active={allTurrets[i].gameObject.activeSelf}"); } catch { }
                    }

                    // 4. All AircraftBomber
                    var allBomber = Resources.FindObjectsOfTypeAll<Il2CppGameplay.BomberRush.AircraftBomber>();
                    LoggerInstance.Msg($"[COMBAT] AircraftBomber: {allBomber?.Length ?? 0}");
                    for (int i = 0; i < (allBomber?.Length ?? 0); i++)
                    {
                        try { LoggerInstance.Msg($"[COMBAT]   Bomber[{i}]: '{allBomber[i].gameObject.name}' root='{allBomber[i].transform.root.gameObject.name}' active={allBomber[i].gameObject.activeSelf}"); } catch { }
                    }

                    // 5. All NavalShip
                    var allShips = Resources.FindObjectsOfTypeAll<Il2CppTargetableSystem.NavalShip>();
                    LoggerInstance.Msg($"[COMBAT] NavalShip: {allShips?.Length ?? 0}");
                    for (int i = 0; i < (allShips?.Length ?? 0); i++)
                    {
                        try { LoggerInstance.Msg($"[COMBAT]   Ship[{i}]: '{allShips[i].gameObject.name}' root='{allShips[i].transform.root.gameObject.name}' active={allShips[i].gameObject.activeSelf}"); } catch { }
                    }

                    // 6. All FlakWeapon
                    var allFlak = Resources.FindObjectsOfTypeAll<Il2CppWeapon.FlakWeapon>();
                    LoggerInstance.Msg($"[COMBAT] FlakWeapon: {allFlak?.Length ?? 0}");
                    for (int i = 0; i < (allFlak?.Length ?? 0); i++)
                    {
                        try { LoggerInstance.Msg($"[COMBAT]   Flak[{i}]: '{allFlak[i].gameObject.name}' root='{allFlak[i].transform.root.gameObject.name}' active={allFlak[i].gameObject.activeSelf}"); } catch { }
                    }

                    // 7. All AirDefenseEnemyAIController
                    var allDefAI = Resources.FindObjectsOfTypeAll<Il2CppGameplay.Defense.AirDefenseEnemyAIController>();
                    LoggerInstance.Msg($"[COMBAT] AirDefenseEnemyAIController: {allDefAI?.Length ?? 0}");
                    for (int i = 0; i < (allDefAI?.Length ?? 0); i++)
                    {
                        try { LoggerInstance.Msg($"[COMBAT]   DefAI[{i}]: '{allDefAI[i].gameObject.name}' root='{allDefAI[i].transform.root.gameObject.name}' active={allDefAI[i].gameObject.activeSelf}"); } catch { }
                    }

                    // 8. All AirDefensePatrolGroup (holds enemy prefab refs!)
                    var allPatrol = Resources.FindObjectsOfTypeAll<Il2CppGameplay.Defense.AirDefensePatrolGroup>();
                    LoggerInstance.Msg($"[COMBAT] AirDefensePatrolGroup: {allPatrol?.Length ?? 0}");
                    for (int i = 0; i < (allPatrol?.Length ?? 0); i++)
                    {
                        try
                        {
                            var pg = allPatrol[i];
                            var prefab = pg.m_enemyAircraftPrefab;
                            LoggerInstance.Msg($"[COMBAT]   PatrolGroup[{i}]: '{pg.gameObject.name}' enemyPrefab='{prefab?.name ?? "null"}' count={pg.m_enemyCount}");
                            if (prefab != null && cachedEnemyMesser == null)
                            {
                                cachedEnemyMesser = prefab;
                                enemyPrefabsReady = true;
                                LoggerInstance.Msg($"[COMBAT]   *** GRABBED enemy prefab from PatrolGroup! ***");
                            }
                        }
                        catch (Exception ex) { LoggerInstance.Msg($"[COMBAT]   PatrolGroup[{i}]: {ex.Message}"); }
                    }

                    // 9. All TargetSpawner (dev test tool)
                    var allTS = Resources.FindObjectsOfTypeAll<Il2CppGameplay.TargetElimination2.Tester.TargetSpawner>();
                    LoggerInstance.Msg($"[COMBAT] TargetSpawner (dev tool): {allTS?.Length ?? 0}");
                    for (int i = 0; i < (allTS?.Length ?? 0); i++)
                    {
                        try
                        {
                            var ts = allTS[i];
                            var target = ts.m_target;
                            LoggerInstance.Msg($"[COMBAT]   TS[{i}]: '{ts.gameObject.name}' target='{target?.name ?? "null"}' interval={ts.m_spawnInterval} max={ts.m_maxSpawn}");
                        }
                        catch (Exception ex) { LoggerInstance.Msg($"[COMBAT]   TS[{i}]: {ex.Message}"); }
                    }

                    LoggerInstance.Msg("[COMBAT] === END DEEP SCAN ===");
                    SetStatus("Deep scan done — check log for [COMBAT]");
                }
                catch (Exception ex) { SetStatus($"Scan failed: {ex.Message}"); LoggerInstance.Error($"[COMBAT] Deep scan: {ex}"); }
            });
            B("Dev Scan: All AssetLoaderHandlers", () => {
                try
                {
                    var handlers = Resources.FindObjectsOfTypeAll<Il2Cpp.AssetLoaderHandler>();
                    LoggerInstance.Msg($"[COMBAT] === AssetLoaderHandler scan: {handlers?.Length ?? 0} ===");
                    for (int i = 0; i < (handlers?.Length ?? 0); i++)
                    {
                        try
                        {
                            var h = handlers[i];
                            LoggerInstance.Msg($"[COMBAT]   Handler[{i}]: '{h.gameObject.name}' bundleType={h.m_assetBundleType} prefab='{h.m_prefabName}' active={h.gameObject.activeSelf}");
                        }
                        catch (Exception ex) { LoggerInstance.Msg($"[COMBAT]   Handler[{i}]: error={ex.Message}"); }
                    }
                    SetStatus($"{handlers?.Length ?? 0} handlers — check log");
                }
                catch (Exception ex) { SetStatus($"Scan failed: {ex.Message}"); }
            });

            H($"--- DRONE COMBAT ({activeDrones.Count} drones) ---");
            B("Spawn 1 Drone", () => { SpawnDroneWave(1); });
            B("Spawn 3 Drones", () => { SpawnDroneWave(3); });
            B("Spawn 5 Drones", () => { SpawnDroneWave(5); });
            B("Clear All Drones", () => {
                for (int i = activeDrones.Count - 1; i >= 0; i--)
                    try { if (activeDrones[i].go != null) UnityEngine.Object.Destroy(activeDrones[i].go); } catch { }
                activeDrones.Clear();
                SetStatus("Drones cleared");
            });

            H("--- ENEMIES (ADVANCED) ---");
            B("Scan for Enemy AI (check log)", () => {
                try
                {
                    LoggerInstance.Msg("=== ENEMY AI SCAN ===");

                    // Find all AiAircraftController in scene
                    var allAI = Resources.FindObjectsOfTypeAll<Il2CppAi.AiAircraftController>();
                    LoggerInstance.Msg($"[ENEMY] AiAircraftController (incl inactive): {allAI?.Length ?? 0}");
                    for (int i = 0; i < Math.Min(allAI?.Length ?? 0, 20); i++)
                    {
                        var ai = allAI[i];
                        string rootName = "";
                        try { rootName = ai.transform.root.gameObject.name; } catch { }
                        LoggerInstance.Msg($"[ENEMY]   AI[{i}]: '{ai.gameObject.name}' root='{rootName}' active={ai.gameObject.activeSelf} state={ai.m_aiState}");
                    }

                    // Find AdvanceAircraftSpawner
                    var spawners = Resources.FindObjectsOfTypeAll<AdvanceAircraftSpawner>();
                    LoggerInstance.Msg($"[ENEMY] AdvanceAircraftSpawner: {spawners?.Length ?? 0}");
                    for (int i = 0; i < (spawners?.Length ?? 0); i++)
                    {
                        LoggerInstance.Msg($"[ENEMY]   Spawner[{i}]: '{spawners[i].gameObject.name}' active={spawners[i].gameObject.activeSelf}");
                    }

                    // Find all RigidTargetableAircraft (player + enemy aircraft)
                    var allTargetable = Resources.FindObjectsOfTypeAll<RigidTargetableAircraft>();
                    LoggerInstance.Msg($"[ENEMY] RigidTargetableAircraft: {allTargetable?.Length ?? 0}");
                    for (int i = 0; i < Math.Min(allTargetable?.Length ?? 0, 20); i++)
                    {
                        var t = allTargetable[i];
                        LoggerInstance.Msg($"[ENEMY]   RTA[{i}]: '{t.gameObject.name}' root='{t.transform.root.gameObject.name}' active={t.gameObject.activeSelf}");
                    }

                    // Find all AiAircraftConfigDO (enemy behavior configs)
                    var allAICfg = Resources.FindObjectsOfTypeAll<Il2CppAi.AiAircraftConfigDO>();
                    LoggerInstance.Msg($"[ENEMY] AiAircraftConfigDO: {allAICfg?.Length ?? 0}");
                    for (int i = 0; i < (allAICfg?.Length ?? 0); i++)
                    {
                        LoggerInstance.Msg($"[ENEMY]   AICfg[{i}]: '{allAICfg[i].name}'");
                    }

                    // Search for anything with "drone" or "target" or "balloon" in name
                    var allGO = Resources.FindObjectsOfTypeAll<GameObject>();
                    int found = 0;
                    for (int i = 0; i < allGO.Length && found < 30; i++)
                    {
                        string gn = allGO[i].name.ToLower();
                        if (gn.Contains("drone") || gn.Contains("target ring") || gn.Contains("balloon") || gn.Contains("training"))
                        {
                            LoggerInstance.Msg($"[ENEMY]   GO: '{allGO[i].name}' active={allGO[i].activeSelf} root='{allGO[i].transform.root.gameObject.name}'");
                            found++;
                        }
                    }

                    SetStatus("Enemy scan done — check log");
                }
                catch (Exception ex) { SetStatus($"Scan failed: {ex.Message}"); LoggerInstance.Error($"[ENEMY] {ex}"); }
            });

            B("Clone Ambient Aircraft Near Player", () => {
                try
                {
                    var ac = GameManager.ControllerAircraft;
                    if (ac == null) { SetStatus("Not in aircraft"); return; }

                    // Find any ambient/AI aircraft to clone
                    var allRTA = Resources.FindObjectsOfTypeAll<RigidTargetableAircraft>();
                    GameObject sourceGO = null;
                    string sourceName = "";
                    for (int i = 0; i < (allRTA?.Length ?? 0); i++)
                    {
                        string rootName = allRTA[i].transform.root.gameObject.name;
                        // Skip the player aircraft
                        if (rootName == ac.gameObject.name) continue;
                        string n = rootName.ToLower();
                        // Prefer ambient vehicles, messer, drones
                        if (n.Contains("ambient") || n.Contains("messer") || n.Contains("drone") || n.Contains("glider") || n.Contains("target"))
                        {
                            sourceGO = allRTA[i].transform.root.gameObject;
                            sourceName = rootName;
                            break;
                        }
                    }

                    if (sourceGO == null) { SetStatus("No cloneable aircraft found"); return; }

                    // Clone and position near player
                    var playerPos = ac.transform.position;
                    var spawnPos = playerPos + ac.transform.forward * 200f + Vector3.up * 50f;

                    var clone = UnityEngine.Object.Instantiate(sourceGO);
                    clone.name = sourceName + " (Mod Spawn)";
                    clone.transform.position = spawnPos;
                    clone.SetActive(true);

                    // Try to set AI to attack
                    var aiCtrl = clone.GetComponentInChildren<Il2CppAi.AiAircraftController>();
                    if (aiCtrl != null)
                    {
                        var playerLead = ac.m_aircraftLead;
                        if (playerLead != null)
                        {
                            aiCtrl.m_targetAircraftLead = playerLead;
                            aiCtrl.m_desiredAiState = Il2CppAi.AI_STATE.Attack;
                            LoggerInstance.Msg($"[ENEMY] Set AI to attack player");
                        }
                    }

                    // Enable rigidbody
                    var rb = clone.GetComponent<Rigidbody>();
                    if (rb != null) { rb.isKinematic = false; }

                    LoggerInstance.Msg($"[ENEMY] Cloned '{sourceName}' at {spawnPos}");
                    SetStatus($"Spawned {sourceName}!");
                }
                catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); LoggerInstance.Error($"[ENEMY] {ex}"); }
            });
        }

        // ================================================================
        // COMBAT SPAWNER SYSTEM
        // ================================================================
        private void ScanCombatPrefabs()
        {
            cachedFighterPrefabs.Clear();
            cachedTurretPrefabs.Clear();
            cachedShipPrefabs.Clear();

            LoggerInstance.Msg("=== COMBAT PREFAB SCAN ===");

            // Find enemy aircraft (AircraftController = AI-driven aircraft with combat capability)
            try
            {
                var allAC = Resources.FindObjectsOfTypeAll<Il2CppAi.AiAircraftController>();
                LoggerInstance.Msg($"[COMBAT] AiAircraftController: {allAC?.Length ?? 0}");
                for (int i = 0; i < (allAC?.Length ?? 0); i++)
                {
                    var ai = allAC[i];
                    if (ai == null) continue;
                    var rootGO = ai.transform.root.gameObject;
                    string rootName = rootGO.name;
                    // Skip the player aircraft
                    try { var playerAc = GameManager.ControllerAircraft; if (playerAc != null && rootName == playerAc.gameObject.name) continue; } catch { }
                    // Skip duplicates
                    bool dup = false;
                    foreach (var p in cachedFighterPrefabs) { if (p.name == rootName) { dup = true; break; } }
                    if (dup) continue;
                    cachedFighterPrefabs.Add(rootGO);
                    LoggerInstance.Msg($"[COMBAT]   Fighter: '{rootName}' active={rootGO.activeSelf} hasWeapons={ai.HasWeaponSystems}");
                }
            }
            catch (Exception ex) { LoggerInstance.Msg($"[COMBAT] Fighter scan: {ex.Message}"); }

            // Find RigidTargetableAircraft that have AI (backup search)
            try
            {
                var allRTA = Resources.FindObjectsOfTypeAll<RigidTargetableAircraft>();
                LoggerInstance.Msg($"[COMBAT] RigidTargetableAircraft: {allRTA?.Length ?? 0}");
                for (int i = 0; i < (allRTA?.Length ?? 0); i++)
                {
                    var rta = allRTA[i];
                    if (rta == null) continue;
                    var rootGO = rta.transform.root.gameObject;
                    string rootName = rootGO.name;
                    try { var playerAc = GameManager.ControllerAircraft; if (playerAc != null && rootName == playerAc.gameObject.name) continue; } catch { }
                    // Check if it has AI
                    var ai = rootGO.GetComponentInChildren<Il2CppAi.AiAircraftController>();
                    if (ai == null) continue;
                    bool dup = false;
                    foreach (var p in cachedFighterPrefabs) { if (p.name == rootName) { dup = true; break; } }
                    if (dup) continue;
                    cachedFighterPrefabs.Add(rootGO);
                    LoggerInstance.Msg($"[COMBAT]   RTA Fighter: '{rootName}'");
                }
            }
            catch (Exception ex) { LoggerInstance.Msg($"[COMBAT] RTA scan: {ex.Message}"); }

            // Find targetable objects that could be ground targets (turrets, balloons, etc.)
            try
            {
                var allGO = Resources.FindObjectsOfTypeAll<GameObject>();
                for (int i = 0; i < allGO.Length; i++)
                {
                    string n = allGO[i].name.ToLower();
                    if (n.Contains("turret") || n.Contains("flak") || n.Contains("cannon") || n.Contains("snow leopard"))
                    {
                        bool dup = false;
                        foreach (var p in cachedTurretPrefabs) { if (p.name == allGO[i].name) { dup = true; break; } }
                        if (!dup && cachedTurretPrefabs.Count < 10)
                        {
                            cachedTurretPrefabs.Add(allGO[i]);
                            LoggerInstance.Msg($"[COMBAT]   Turret: '{allGO[i].name}' active={allGO[i].activeSelf}");
                        }
                    }
                    if (n.Contains("destroyer") || n.Contains("naval") || n.Contains("ship"))
                    {
                        bool dup = false;
                        foreach (var p in cachedShipPrefabs) { if (p.name == allGO[i].name) { dup = true; break; } }
                        if (!dup && cachedShipPrefabs.Count < 10)
                        {
                            cachedShipPrefabs.Add(allGO[i]);
                            LoggerInstance.Msg($"[COMBAT]   Ship: '{allGO[i].name}' active={allGO[i].activeSelf}");
                        }
                    }
                }
            }
            catch (Exception ex) { LoggerInstance.Msg($"[COMBAT] Ground scan: {ex.Message}"); }

            // Deep scan: AdvanceAircraftSpawner and VehicleReferenceConfigDO
            try
            {
                var spawners = Resources.FindObjectsOfTypeAll<AdvanceAircraftSpawner>();
                LoggerInstance.Msg($"[COMBAT] AdvanceAircraftSpawner: {spawners?.Length ?? 0}");
                for (int s = 0; s < (spawners?.Length ?? 0); s++)
                {
                    var spawner = spawners[s];
                    LoggerInstance.Msg($"[COMBAT]   Spawner[{s}]: '{spawner.gameObject.name}' active={spawner.gameObject.activeSelf}");
                }

                // Find all VehicleReferenceConfigDO — these map IDs to prefab paths
                var vehicleRefs = Resources.FindObjectsOfTypeAll<Il2Cpp.VehicleReferenceConfigDO>();
                LoggerInstance.Msg($"[COMBAT] VehicleReferenceConfigDO: {vehicleRefs?.Length ?? 0}");
                for (int v = 0; v < (vehicleRefs?.Length ?? 0); v++)
                {
                    var vr = vehicleRefs[v];
                    LoggerInstance.Msg($"[COMBAT]   VehicleRef[{v}]: '{vr.name}' id='{vr.GetID()}'");
                    try
                    {
                        var islandList = vr.GetAircraftReferencesFrom(Il2Cpp.VehicleReferenceConfigDO.AircraftList.Island);
                        if (islandList != null)
                        {
                            for (int a = 0; a < islandList.Count; a++)
                            {
                                var ar = islandList[a];
                                if (ar != null)
                                    LoggerInstance.Msg($"[COMBAT]     Island[{a}]: type='{ar.Type}' refType='{ar.ReferenceType}' path='{ar.Path}'");
                            }
                        }
                    }
                    catch (Exception ex) { LoggerInstance.Msg($"[COMBAT]     Island list error: {ex.Message}"); }
                    try
                    {
                        var officeList = vr.GetAircraftReferencesFrom(Il2Cpp.VehicleReferenceConfigDO.AircraftList.Office);
                        if (officeList != null)
                        {
                            for (int a = 0; a < officeList.Count; a++)
                            {
                                var ar = officeList[a];
                                if (ar != null)
                                    LoggerInstance.Msg($"[COMBAT]     Office[{a}]: type='{ar.Type}' refType='{ar.ReferenceType}' path='{ar.Path}'");
                            }
                        }
                    }
                    catch (Exception ex) { LoggerInstance.Msg($"[COMBAT]     Office list error: {ex.Message}"); }
                }

                // Also check for DogFightMissionController
                var dogfights = Resources.FindObjectsOfTypeAll<GameObject>();
                int dfCount = 0;
                for (int i = 0; i < dogfights.Length && dfCount < 5; i++)
                {
                    string n = dogfights[i].name.ToLower();
                    if (n.Contains("messer") || n.Contains("dogfight") || n.Contains("fighter") || n.Contains("enemy") || n.Contains("combat mission"))
                    {
                        LoggerInstance.Msg($"[COMBAT]   Combat GO: '{dogfights[i].name}' active={dogfights[i].activeSelf} root='{dogfights[i].transform.root.gameObject.name}'");
                        dfCount++;
                    }
                }
            }
            catch (Exception ex) { LoggerInstance.Msg($"[COMBAT] Deep scan: {ex.Message}"); }

            // Try to load enemy fighters by known resource paths
            if (cachedFighterPrefabs.Count == 0)
            {
                string[] tryPaths = {
                    "Wolf 01", "Wolf_01", "Wolf 01 (Escort Targetable Vehicle - Easy)",
                    "Messer 01", "Messer_01", "Messer 01 (Escort Targetable Vehicle - Easy)",
                    "EnemyAircraft", "Enemy_Aircraft", "AI_Aircraft",
                    "ChaseAircraft", "CombatAircraft"
                };
                foreach (var path in tryPaths)
                {
                    try
                    {
                        var loaded = Resources.Load<GameObject>(path);
                        if (loaded != null)
                        {
                            cachedFighterPrefabs.Add(loaded);
                            LoggerInstance.Msg($"[COMBAT] Resources.Load found: '{path}' -> '{loaded.name}'");
                        }
                    }
                    catch { }
                }
                if (cachedFighterPrefabs.Count > 0)
                    LoggerInstance.Msg($"[COMBAT] Loaded {cachedFighterPrefabs.Count} fighters via Resources.Load");
                else
                    LoggerInstance.Msg("[COMBAT] Resources.Load found no enemy fighters");
            }

            // If still no fighters, try using the AdvanceAircraftSpawner to instantiate one
            if (cachedFighterPrefabs.Count == 0)
            {
                try
                {
                    var spawners = Resources.FindObjectsOfTypeAll<AdvanceAircraftSpawner>();
                    if (spawners != null && spawners.Length > 0)
                    {
                        var spawner = spawners[0];
                        // Try to spawn a wolf (enemy Kodiak) using the game's spawner
                        string[] enemyIds = { "wolf", "messer", "wolf_easy", "messer_easy", "enemy_wolf", "enemy_messer" };
                        foreach (var eid in enemyIds)
                        {
                            try
                            {
                                string capturedId = eid;
                                LoggerInstance.Msg($"[COMBAT] Trying spawner.InstantiateAircraft('{capturedId}', 'default')...");
                                spawner.StartCoroutine(spawner.InstantiateAircraft(capturedId, "default", false, (Il2CppSystem.Action<Il2Cpp.IMonoIdentity>)((Il2Cpp.IMonoIdentity identity) => {
                                    try
                                    {
                                        LoggerInstance.Msg($"[COMBAT] Spawner callback for '{capturedId}'!");
                                        if (identity == null) { LoggerInstance.Msg($"[COMBAT]   identity is null"); return; }
                                        var mono = identity.TryCast<MonoBehaviour>();
                                        if (mono == null) { LoggerInstance.Msg($"[COMBAT]   not a MonoBehaviour"); return; }
                                        var go = mono.gameObject;
                                        LoggerInstance.Msg($"[COMBAT]   Got '{go.name}' — caching as enemy prefab");

                                        // Log key components
                                        var aiCtrl = go.GetComponentInChildren<Il2CppAi.AiAircraftController>(true);
                                        LoggerInstance.Msg($"[COMBAT]   AiAircraftController: {(aiCtrl != null ? $"FOUND hasWeapons={aiCtrl.HasWeaponSystems}" : "MISSING")}");
                                        var aiWeapons = go.GetComponentsInChildren<Il2CppAi.AiAircraftWeapon>(true);
                                        LoggerInstance.Msg($"[COMBAT]   AiAircraftWeapon count: {aiWeapons?.Length ?? 0}");

                                        // Cache it
                                        if (capturedId.Contains("messer") && cachedEnemyMesser == null)
                                        {
                                            cachedEnemyMesser = go;
                                            go.SetActive(false);
                                            go.transform.position = new Vector3(0, -1000, 0);
                                            enemyPrefabsReady = true;
                                            LoggerInstance.Msg($"[COMBAT]   *** CACHED as enemy_messer ***");
                                        }
                                        else if (capturedId.Contains("wolf") && cachedEnemyWolf == null)
                                        {
                                            cachedEnemyWolf = go;
                                            go.SetActive(false);
                                            go.transform.position = new Vector3(0, -1000, 0);
                                            enemyPrefabsReady = true;
                                            LoggerInstance.Msg($"[COMBAT]   *** CACHED as enemy_wolf ***");
                                        }
                                        else
                                        {
                                            // Still useful — add to fighter prefabs list
                                            cachedFighterPrefabs.Add(go);
                                            go.SetActive(false);
                                            go.transform.position = new Vector3(0, -1000, 0);
                                        }
                                    }
                                    catch (Exception ex) { LoggerInstance.Msg($"[COMBAT] Callback error: {ex.Message}"); }
                                }), false, true));
                            }
                            catch (Exception ex) { LoggerInstance.Msg($"[COMBAT] Spawner '{eid}' failed: {ex.Message}"); }
                        }
                    }
                }
                catch (Exception ex) { LoggerInstance.Msg($"[COMBAT] Spawner attempt: {ex.Message}"); }
            }

            combatPrefabsScanned = true;
            LoggerInstance.Msg($"[COMBAT] Scan complete: {cachedFighterPrefabs.Count} fighters, {cachedTurretPrefabs.Count} turrets, {cachedShipPrefabs.Count} ships");
            SetStatus($"Found {cachedFighterPrefabs.Count} fighters, {cachedTurretPrefabs.Count} turrets, {cachedShipPrefabs.Count} ships");
        }

        private void SpawnFighter(Vector3 spawnPos, Quaternion rotation)
        {
            if (cachedFighterPrefabs.Count == 0) { SetStatus("No fighter prefabs — scan first"); return; }

            try
            {
                // Pick a random fighter prefab
                var prefab = cachedFighterPrefabs[UnityEngine.Random.Range(0, cachedFighterPrefabs.Count)];
                LoggerInstance.Msg($"[COMBAT] Cloning prefab '{prefab.name}' active={prefab.activeSelf}");
                var clone = UnityEngine.Object.Instantiate(prefab);
                clone.name = prefab.name + "_combat_" + UnityEngine.Random.Range(1000, 9999);
                clone.transform.position = spawnPos;
                clone.transform.rotation = rotation;
                clone.SetActive(true);

                // Enable rigidbody
                var rb = clone.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = false;
                    rb.velocity = clone.transform.forward * 30f;
                    LoggerInstance.Msg($"[COMBAT]   Rigidbody: mass={rb.mass} drag={rb.drag} kinematic={rb.isKinematic}");
                }
                else
                {
                    LoggerInstance.Msg($"[COMBAT]   WARNING: No Rigidbody on clone");
                }

                // Set AI to attack player
                var aiCtrl = clone.GetComponentInChildren<Il2CppAi.AiAircraftController>();
                if (aiCtrl != null)
                {
                    LoggerInstance.Msg($"[COMBAT]   AI found: state={aiCtrl.m_aiState} desiredState={aiCtrl.m_desiredAiState} hasWeapons={aiCtrl.HasWeaponSystems}");

                    var playerAc = GameManager.ControllerAircraft;
                    if (playerAc != null)
                    {
                        // Get player's AiAircraftLead for targeting
                        var playerLead = playerAc.m_aircraftLead;
                        if (playerLead != null)
                        {
                            aiCtrl.m_targetAircraftLead = playerLead;
                            LoggerInstance.Msg($"[COMBAT]   Target set: playerLead assigned");
                        }
                        else
                        {
                            LoggerInstance.Msg($"[COMBAT]   WARNING: playerAc.m_aircraftLead is null");
                        }

                        // Set attack state
                        try { aiCtrl.SetDesiredAiState(Il2CppAi.AI_STATE.Attack, playerAc.TryCast<Il2CppActor.ITargetable>()); } catch (Exception ex) { LoggerInstance.Msg($"[COMBAT]   SetDesiredAiState failed: {ex.Message}"); }
                        try { aiCtrl.AllowAttack(true); } catch (Exception ex) { LoggerInstance.Msg($"[COMBAT]   AllowAttack failed: {ex.Message}"); }
                        try { aiCtrl.ResetWeaponSystems(true); } catch (Exception ex) { LoggerInstance.Msg($"[COMBAT]   ResetWeaponSystems failed: {ex.Message}"); }

                        LoggerInstance.Msg($"[COMBAT]   Post-setup: state={aiCtrl.m_aiState} desiredState={aiCtrl.m_desiredAiState}");
                    }
                    else
                    {
                        LoggerInstance.Msg($"[COMBAT]   WARNING: ControllerAircraft is null — can't set target");
                    }

                    // Log AiAircraftWeapon components
                    try
                    {
                        var aiWeapons = clone.GetComponentsInChildren<Il2CppAi.AiAircraftWeapon>(true);
                        LoggerInstance.Msg($"[COMBAT]   AiAircraftWeapon count: {aiWeapons?.Length ?? 0}");
                        for (int w = 0; w < (aiWeapons?.Length ?? 0); w++)
                        {
                            var aw = aiWeapons[w];
                            LoggerInstance.Msg($"[COMBAT]     AiWeapon[{w}]: '{aw.gameObject.name}' active={aw.IsActive} isFiring={aw.m_isFiring}");
                            aw.IsActive = true;
                        }
                    }
                    catch (Exception ex) { LoggerInstance.Msg($"[COMBAT]   AiWeapon scan: {ex.Message}"); }
                }
                else
                {
                    LoggerInstance.Msg($"[COMBAT]   WARNING: No AiAircraftController on clone '{clone.name}'");
                    // Log all components for debugging
                    try
                    {
                        var allComps = clone.GetComponentsInChildren<Component>(true);
                        LoggerInstance.Msg($"[COMBAT]   Components ({allComps.Length}):");
                        for (int c = 0; c < Math.Min(allComps.Length, 30); c++)
                        {
                            try { LoggerInstance.Msg($"[COMBAT]     [{c}] {allComps[c].GetIl2CppType().Name} on '{allComps[c].gameObject.name}'"); } catch { }
                        }
                    }
                    catch { }
                }

                // Enable all weapons on the clone
                try
                {
                    var weapons = clone.GetComponentsInChildren<Il2CppWeapon.FiringMechanismBase>(true);
                    LoggerInstance.Msg($"[COMBAT]   FiringMechanisms: {weapons?.Length ?? 0}");
                    for (int w = 0; w < weapons.Length; w++)
                    {
                        weapons[w].gameObject.SetActive(true);
                        var fm = weapons[w].TryCast<Il2CppWeapon.FiringMechanism>();
                        if (fm != null) fm.m_isInfiniteAmmo = true;
                        LoggerInstance.Msg($"[COMBAT]     FM[{w}]: '{weapons[w].gameObject.name}' enabled={weapons[w].m_isEnabled} ready={weapons[w].m_readyToFire}");
                    }
                }
                catch (Exception ex) { LoggerInstance.Msg($"[COMBAT]   Weapon enable: {ex.Message}"); }

                spawnedCombatants.Add(clone);
                combatActiveFighters = spawnedCombatants.Count;
                LoggerInstance.Msg($"[COMBAT] === Fighter spawn complete: '{clone.name}' at ({spawnPos.x:F0},{spawnPos.y:F0},{spawnPos.z:F0}) ===");
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"[COMBAT] Spawn failed: {ex}");
                SetStatus($"Spawn failed: {ex.Message}");
            }
        }

        // ================================================================
        // ENEMY PREFAB LOADER — uses AdvanceAircraftSpawner to get real enemy prefabs
        // ================================================================
        private void LoadEnemyPrefabs()
        {
            if (enemyPrefabsReady) { SetStatus($"Prefabs already loaded! messer={cachedEnemyMesser != null} wolf={cachedEnemyWolf != null}"); return; }

            LoggerInstance.Msg("[COMBAT] === LOADING ENEMY PREFABS ===");

            // STEP 1: Check if ScanCombatPrefabs already cached them from a callback
            if (cachedEnemyMesser != null || cachedEnemyWolf != null)
            {
                enemyPrefabsReady = true;
                SetStatus($"Prefabs already cached! messer={cachedEnemyMesser != null} wolf={cachedEnemyWolf != null}");
                return;
            }

            // STEP 2: Search scene for already-spawned enemies (from previous scan callback)
            try
            {
                var allGO = Resources.FindObjectsOfTypeAll<GameObject>();
                for (int i = 0; i < allGO.Length; i++)
                {
                    string n = allGO[i]?.name?.ToLower() ?? "";
                    if (n.Contains("messer") || n.Contains("wolf") || n.Contains("enemy"))
                    {
                        var go = allGO[i];
                        // Must have AiAircraftController to be a real combat enemy
                        var aiCtrl = go.GetComponentInChildren<Il2CppAi.AiAircraftController>(true);
                        if (aiCtrl == null) continue;

                        LoggerInstance.Msg($"[COMBAT]   Found enemy in scene: '{go.name}' active={go.activeSelf} hasWeapons={aiCtrl.HasWeaponSystems}");

                        if (n.Contains("messer") && cachedEnemyMesser == null)
                        {
                            cachedEnemyMesser = go;
                            if (go.activeSelf) { go.SetActive(false); go.transform.position = new Vector3(0, -1000, 0); }
                            LoggerInstance.Msg($"[COMBAT]   *** CACHED as enemy_messer ***");
                        }
                        else if (cachedEnemyWolf == null)
                        {
                            cachedEnemyWolf = go;
                            if (go.activeSelf) { go.SetActive(false); go.transform.position = new Vector3(0, -1000, 0); }
                            LoggerInstance.Msg($"[COMBAT]   *** CACHED as enemy_wolf ***");
                        }

                        if (cachedEnemyMesser != null || cachedEnemyWolf != null)
                        {
                            enemyPrefabsReady = true;
                            SetStatus($"Found enemy prefab in scene! Ready to spawn.");
                            return;
                        }
                    }
                }
            }
            catch (Exception ex) { LoggerInstance.Msg($"[COMBAT] Scene search: {ex.Message}"); }

            // STEP 3: Try spawning via AdvanceAircraftSpawner (triggers async callbacks)
            try
            {
                var spawners = Resources.FindObjectsOfTypeAll<AdvanceAircraftSpawner>();
                if (spawners == null || spawners.Length == 0) { SetStatus("No AdvanceAircraftSpawner — run Scan Combat Prefabs first"); return; }

                var spawner = spawners[0];
                string[] enemyIds = { "enemy_messer", "enemy_wolf", "messer", "wolf" };
                foreach (var eid in enemyIds)
                {
                    try
                    {
                        string capturedId = eid;
                        LoggerInstance.Msg($"[COMBAT] Requesting spawner.InstantiateAircraft('{capturedId}')...");
                        spawner.StartCoroutine(spawner.InstantiateAircraft(capturedId, "default", false,
                            (Il2CppSystem.Action<Il2Cpp.IMonoIdentity>)((Il2Cpp.IMonoIdentity identity) =>
                        {
                            try
                            {
                                if (identity == null) { LoggerInstance.Msg($"[COMBAT] '{capturedId}': identity null"); return; }
                                var mono = identity.TryCast<MonoBehaviour>();
                                if (mono == null) { LoggerInstance.Msg($"[COMBAT] '{capturedId}': not MonoBehaviour"); return; }
                                var go = mono.gameObject;
                                LoggerInstance.Msg($"[COMBAT] '{capturedId}' loaded: '{go.name}'");

                                var aiCtrl = go.GetComponentInChildren<Il2CppAi.AiAircraftController>(true);
                                LoggerInstance.Msg($"[COMBAT]   AI: {(aiCtrl != null ? $"FOUND hasWeapons={aiCtrl.HasWeaponSystems}" : "MISSING")}");
                                var aiWeapons = go.GetComponentsInChildren<Il2CppAi.AiAircraftWeapon>(true);
                                LoggerInstance.Msg($"[COMBAT]   AiWeapons: {aiWeapons?.Length ?? 0}");

                                // Log ALL components for debugging
                                var comps = go.GetComponentsInChildren<Component>(true);
                                for (int c = 0; c < Math.Min(comps.Length, 25); c++)
                                {
                                    try { LoggerInstance.Msg($"[COMBAT]     [{c}] {comps[c].GetIl2CppType().Name} on '{comps[c].gameObject.name}'"); } catch { }
                                }

                                if (capturedId.Contains("messer") && cachedEnemyMesser == null)
                                {
                                    cachedEnemyMesser = go;
                                    go.SetActive(false); go.transform.position = new Vector3(0, -1000, 0);
                                    enemyPrefabsReady = true;
                                    LoggerInstance.Msg($"[COMBAT] *** CACHED enemy_messer: '{go.name}' ***");
                                }
                                else if (capturedId.Contains("wolf") && cachedEnemyWolf == null)
                                {
                                    cachedEnemyWolf = go;
                                    go.SetActive(false); go.transform.position = new Vector3(0, -1000, 0);
                                    enemyPrefabsReady = true;
                                    LoggerInstance.Msg($"[COMBAT] *** CACHED enemy_wolf: '{go.name}' ***");
                                }
                            }
                            catch (Exception ex) { LoggerInstance.Error($"[COMBAT] Callback error '{capturedId}': {ex}"); }
                        }), false, true));
                    }
                    catch (Exception ex) { LoggerInstance.Msg($"[COMBAT] '{eid}': {ex.Message}"); }
                }
                SetStatus("Spawner requests sent — hit Load again in a few seconds to check");
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"[COMBAT] LoadEnemyPrefabs: {ex}");
                SetStatus($"Load failed: {ex.Message}");
            }
        }

        private void SpawnEnemyFighter(Vector3 spawnPos)
        {
            // Pick a prefab — prefer messer, fall back to wolf
            GameObject prefab = null;
            if (IsValidCachedPrefab(cachedEnemyMesser)) prefab = cachedEnemyMesser;
            else if (IsValidCachedPrefab(cachedEnemyWolf)) prefab = cachedEnemyWolf;
            if (prefab == null) { SetStatus("No enemy prefabs loaded — play a combat mission first"); return; }

            try
            {
                var ac = GameManager.ControllerAircraft;
                if (ac == null) { SetStatus("Not in aircraft"); return; }

                LoggerInstance.Msg($"[COMBAT] SpawnEnemyFighter using '{prefab.name}'");

                // Clone the prefab
                var clone = UnityEngine.Object.Instantiate(prefab);
                clone.name = "HS_Enemy_" + UnityEngine.Random.Range(1000, 9999);
                clone.transform.position = spawnPos;
                clone.transform.rotation = Quaternion.LookRotation(ac.transform.position - spawnPos);

                // Activate BEFORE configuring AI — components need to be awake
                clone.SetActive(true);

                // Get player's Targetable for targeting
                Il2CppActor.Targetable playerTarget = null;
                try { playerTarget = ac.gameObject.GetComponentInChildren<Il2CppActor.Targetable>(true); } catch { }
                try { if (playerTarget == null) { var rta = ac.gameObject.GetComponentInChildren<RigidTargetableAircraft>(true); if (rta != null) playerTarget = rta.TryCast<Il2CppActor.Targetable>(); } } catch { }

                // === PRIMARY: Use AirDefenseEnemyAIController (the game's own enemy wrapper) ===
                var defAI = clone.GetComponentInChildren<Il2CppGameplay.Defense.AirDefenseEnemyAIController>(true);
                if (defAI != null)
                {
                    LoggerInstance.Msg($"[COMBAT]   AirDefenseEnemyAIController found on '{clone.name}'");

                    // Set target to player
                    if (playerTarget != null)
                    {
                        try { defAI.SetTarget(playerTarget); LoggerInstance.Msg("[COMBAT]   SetTarget → player"); } catch (Exception ex) { LoggerInstance.Msg($"[COMBAT]   SetTarget failed: {ex.Message}"); }
                    }

                    // Allow attack and initiate
                    try { defAI.AllowAttack(true); LoggerInstance.Msg("[COMBAT]   AllowAttack(true)"); } catch (Exception ex) { LoggerInstance.Msg($"[COMBAT]   AllowAttack failed: {ex.Message}"); }
                    try { defAI.AttackDesignatedTarget(); LoggerInstance.Msg("[COMBAT]   AttackDesignatedTarget()"); } catch (Exception ex) { LoggerInstance.Msg($"[COMBAT]   AttackDesignatedTarget failed: {ex.Message}"); }

                    // Also configure the underlying AiAircraftController directly
                    var aiCtrl = defAI.AIController;
                    if (aiCtrl != null)
                    {
                        LoggerInstance.Msg($"[COMBAT]   AiAircraftController: state={aiCtrl.m_aiState} desired={aiCtrl.m_desiredAiState} hasWeapons={aiCtrl.HasWeaponSystems}");

                        // Set ALL targeting references
                        var playerLead = ac.m_aircraftLead;
                        if (playerLead != null)
                        {
                            try { aiCtrl.m_targetAircraftLead = playerLead; LoggerInstance.Msg("[COMBAT]   m_targetAircraftLead set"); } catch { }
                        }

                        // Set ITargetable references — this is what the flight AI actually chases
                        var playerITargetable = ac.TryCast<Il2CppActor.ITargetable>();
                        if (playerITargetable != null)
                        {
                            try { aiCtrl.m_iTargetableTarget = playerITargetable; LoggerInstance.Msg("[COMBAT]   m_iTargetableTarget set"); } catch { }
                        }

                        // Set flyToPosition to player's current position as initial target
                        try { aiCtrl.m_flyToPosition = ac.transform.position; LoggerInstance.Msg("[COMBAT]   m_flyToPosition set to player pos"); } catch { }

                        // Set the ITargetable via SetTarget method too
                        try { aiCtrl.SetTarget(playerITargetable); LoggerInstance.Msg("[COMBAT]   SetTarget(player)"); } catch (Exception ex) { LoggerInstance.Msg($"[COMBAT]   SetTarget: {ex.Message}"); }

                        try { aiCtrl.SetDesiredAiState(Il2CppAi.AI_STATE.Attack, playerITargetable); } catch { }
                        try { aiCtrl.AllowAttack(true); } catch { }
                        try { aiCtrl.ResetWeaponSystems(true); } catch { }

                        LoggerInstance.Msg($"[COMBAT]   Post-config: state={aiCtrl.m_aiState} desired={aiCtrl.m_desiredAiState}");

                        // Log current fly-to position and target info
                        try { LoggerInstance.Msg($"[COMBAT]   flyToPos=({aiCtrl.m_flyToPosition.x:F0},{aiCtrl.m_flyToPosition.y:F0},{aiCtrl.m_flyToPosition.z:F0})"); } catch { }
                        try { LoggerInstance.Msg($"[COMBAT]   iTargetableTarget={aiCtrl.m_iTargetableTarget != null} targetLead={aiCtrl.m_targetAircraftLead != null}"); } catch { }
                    }
                    else
                    {
                        LoggerInstance.Msg("[COMBAT]   WARNING: defAI.AIController is null");
                    }
                }
                else
                {
                    // Fallback: configure AiAircraftController directly
                    LoggerInstance.Msg("[COMBAT]   No AirDefenseEnemyAIController — using AiAircraftController directly");
                    var aiCtrl = clone.GetComponentInChildren<Il2CppAi.AiAircraftController>(true);
                    if (aiCtrl != null)
                    {
                        var playerLead = ac.m_aircraftLead;
                        if (playerLead != null) aiCtrl.m_targetAircraftLead = playerLead;
                        try { aiCtrl.SetDesiredAiState(Il2CppAi.AI_STATE.Attack, ac.TryCast<Il2CppActor.ITargetable>()); } catch { }
                        try { aiCtrl.AllowAttack(true); } catch { }
                        try { aiCtrl.ResetWeaponSystems(true); } catch { }
                        LoggerInstance.Msg($"[COMBAT]   Direct AI config: state={aiCtrl.m_aiState}");
                    }
                    else
                    {
                        LoggerInstance.Msg("[COMBAT]   WARNING: No AI controller at all");
                    }
                }

                // === Activate ALL AiAircraftWeapons ===
                try
                {
                    var aiWeapons = clone.GetComponentsInChildren<Il2CppAi.AiAircraftWeapon>(true);
                    LoggerInstance.Msg($"[COMBAT]   AiAircraftWeapon: {aiWeapons?.Length ?? 0}");
                    for (int w = 0; w < (aiWeapons?.Length ?? 0); w++)
                    {
                        aiWeapons[w].IsActive = true;
                        aiWeapons[w].gameObject.SetActive(true);
                        if (playerTarget != null)
                        {
                            try { aiWeapons[w].CurrentTarget = playerTarget.TryCast<Il2CppActor.ITargetable>(); } catch { }
                        }
                        LoggerInstance.Msg($"[COMBAT]     AiWeapon[{w}]: '{aiWeapons[w].gameObject.name}' IsActive={aiWeapons[w].IsActive} isFiring={aiWeapons[w].m_isFiring} hasTarget={aiWeapons[w].CurrentTarget != null}");
                    }
                }
                catch { }

                // === Activate ALL SimpleTurrets ===
                try
                {
                    var turrets = clone.GetComponentsInChildren<Il2CppTurret.SimpleTurret>(true);
                    LoggerInstance.Msg($"[COMBAT]   SimpleTurret: {turrets?.Length ?? 0}");
                    for (int t = 0; t < (turrets?.Length ?? 0); t++)
                    {
                        turrets[t].gameObject.SetActive(true);
                    }
                }
                catch { }

                // === Enable rigidbody and give initial velocity ===
                var rb = clone.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = false;
                    rb.useGravity = false;
                    rb.velocity = clone.transform.forward * 40f;
                    LoggerInstance.Msg($"[COMBAT]   Rigidbody: mass={rb.mass} drag={rb.drag} velocity set");
                }

                // === Enable all FiringMechanisms with infinite ammo ===
                try
                {
                    var fms = clone.GetComponentsInChildren<Il2CppWeapon.FiringMechanismBase>(true);
                    LoggerInstance.Msg($"[COMBAT]   FiringMechanisms: {fms?.Length ?? 0}");
                    for (int f = 0; f < (fms?.Length ?? 0); f++)
                    {
                        fms[f].gameObject.SetActive(true);
                        fms[f].m_isEnabled = true;
                        var fm = fms[f].TryCast<Il2CppWeapon.FiringMechanism>();
                        if (fm != null) fm.m_isInfiniteAmmo = true;
                    }
                }
                catch { }

                spawnedCombatants.Add(clone);
                combatActiveFighters = spawnedCombatants.Count;
                LoggerInstance.Msg($"[COMBAT] === Enemy '{clone.name}' spawned at ({spawnPos.x:F0},{spawnPos.y:F0},{spawnPos.z:F0}) — {combatActiveFighters} active ===");
                SetStatus($"Enemy spawned! ({combatActiveFighters} active)");
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"[COMBAT] SpawnEnemyFighter: {ex}");
                SetStatus($"Spawn failed: {ex.Message}");
            }
        }

        // ================================================================
        // HOSTILE SKIES MUSIC SYSTEM
        // ================================================================
        private void LoadHSMusic()
        {
            if (hsMusicLoaded && hsMusicClips.Count > 0) { SetStatus($"Music already loaded ({hsMusicClips.Count} tracks)"); return; }

            string musicPath = Path.Combine(MelonEnvironment.GameRootDirectory, "UserData", "HostileSkies", "Music");
            if (!Directory.Exists(musicPath))
            {
                SetStatus("No music folder — create UserData/HostileSkies/Music/ and add .ogg files");
                return;
            }

            var oggFiles = Directory.GetFiles(musicPath, "*.ogg");
            if (oggFiles.Length == 0)
            {
                SetStatus("No .ogg files found in HostileSkies/Music/");
                return;
            }

            LoggerInstance.Msg($"[MUSIC] Found {oggFiles.Length} OGG files, loading...");
            hsMusicClips.Clear();

            // Create persistent audio source
            if (hsMusicObject == null)
            {
                hsMusicObject = new GameObject("HS_MusicPlayer");
                UnityEngine.Object.DontDestroyOnLoad(hsMusicObject);
                hsMusicSource = hsMusicObject.AddComponent<AudioSource>();
                hsMusicSource.loop = false;
                hsMusicSource.playOnAwake = false;
                hsMusicSource.volume = hsMusicVolume;
                hsMusicSource.spatialBlend = 0f; // 2D audio — no spatialization
                LoggerInstance.Msg("[MUSIC] Created HS_MusicPlayer AudioSource");
            }

            // Load each OGG file
            foreach (var file in oggFiles)
            {
                try
                {
                    string fileName = Path.GetFileName(file);
                    string fileUri = "file:///" + file.Replace("\\", "/").Replace(" ", "%20");

                    // Use synchronous WWW-style loading via AudioClip
                    // UnityWebRequest needs coroutines, so we use the older approach
                    var www = UnityEngine.Networking.UnityWebRequestMultimedia.GetAudioClip(fileUri, AudioType.OGGVORBIS);
                    var op = www.SendWebRequest();

                    // Spin-wait for load (these are local files, should be near-instant)
                    int timeout = 0;
                    while (!op.isDone && timeout < 500) { System.Threading.Thread.Sleep(10); timeout++; }

                    if (www.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                    {
                        var clip = UnityEngine.Networking.DownloadHandlerAudioClip.GetContent(www);
                        if (clip != null)
                        {
                            clip.name = Path.GetFileNameWithoutExtension(file);
                            hsMusicClips.Add(clip);
                            LoggerInstance.Msg($"[MUSIC]   Loaded: '{clip.name}' ({clip.length:F1}s)");
                        }
                    }
                    else
                    {
                        LoggerInstance.Msg($"[MUSIC]   Failed: '{fileName}' — {www.error}");
                    }
                    www.Dispose();
                }
                catch (Exception ex) { LoggerInstance.Msg($"[MUSIC]   Error: {ex.Message}"); }
            }

            hsMusicLoaded = hsMusicClips.Count > 0;
            LoggerInstance.Msg($"[MUSIC] Loaded {hsMusicClips.Count}/{oggFiles.Length} tracks");
            SetStatus($"Loaded {hsMusicClips.Count} music tracks");
        }

        private void PlayHSMusic()
        {
            if (!hsMusicLoaded || hsMusicClips.Count == 0) { LoadHSMusic(); if (!hsMusicLoaded) return; }

            // Mute original BGM
            try
            {
                var bgm = Resources.FindObjectsOfTypeAll<Il2CppAudio.Music.BGMPlayer>();
                if (bgm != null && bgm.Length > 0)
                {
                    // Get the game's audio sources and store/mute them
                    for (int i = 0; i < bgm.Length; i++)
                    {
                        try
                        {
                            var officeChannel = bgm[i].m_officeChannel;
                            var islandChannel = bgm[i].m_islandChannel;
                            if (officeChannel != null)
                            {
                                if (originalBGMVolume < 0) originalBGMVolume = officeChannel.volume;
                                officeChannel.volume = 0f;
                            }
                            if (islandChannel != null)
                            {
                                islandChannel.volume = 0f;
                            }
                        }
                        catch { }
                    }
                    LoggerInstance.Msg("[MUSIC] Original BGM muted");
                }
            }
            catch { }

            // Shuffle and play
            if (hsMusicClips.Count > 1)
                hsMusicCurrentTrack = UnityEngine.Random.Range(0, hsMusicClips.Count);

            hsMusicSource.clip = hsMusicClips[hsMusicCurrentTrack];
            hsMusicSource.volume = hsMusicVolume;
            hsMusicSource.Play();
            hsMusicPlaying = true;
            LoggerInstance.Msg($"[MUSIC] Playing: '{hsMusicClips[hsMusicCurrentTrack].name}'");
            SetStatus($"Playing: {hsMusicClips[hsMusicCurrentTrack].name}");
        }

        private void StopHSMusic()
        {
            if (hsMusicSource != null && hsMusicSource.isPlaying)
                hsMusicSource.Stop();
            hsMusicPlaying = false;

            // Restore original BGM
            try
            {
                var bgm = Resources.FindObjectsOfTypeAll<Il2CppAudio.Music.BGMPlayer>();
                if (bgm != null && bgm.Length > 0)
                {
                    for (int i = 0; i < bgm.Length; i++)
                    {
                        try
                        {
                            var officeChannel = bgm[i].m_officeChannel;
                            var islandChannel = bgm[i].m_islandChannel;
                            float restoreVol = originalBGMVolume > 0 ? originalBGMVolume : 1f;
                            if (officeChannel != null) officeChannel.volume = restoreVol;
                            if (islandChannel != null) islandChannel.volume = restoreVol;
                        }
                        catch { }
                    }
                    LoggerInstance.Msg("[MUSIC] Original BGM restored");
                }
            }
            catch { }

            SetStatus("Music: Original");
        }

        private void UpdateHSMusic()
        {
            // Auto-advance to next track when current one finishes
            if (hsMusicPlaying && hsMusicSource != null && !hsMusicSource.isPlaying && hsMusicClips.Count > 0)
            {
                hsMusicCurrentTrack = (hsMusicCurrentTrack + 1) % hsMusicClips.Count;
                hsMusicSource.clip = hsMusicClips[hsMusicCurrentTrack];
                hsMusicSource.Play();
                LoggerInstance.Msg($"[MUSIC] Next track: '{hsMusicClips[hsMusicCurrentTrack].name}'");
            }
        }

        private void SpawnDrone(Vector3 spawnPos, Quaternion rotation)
        {
            try
            {
                var ac = GameManager.ControllerAircraft;
                if (ac == null) return;

                // Clone the player's current aircraft as a drone
                // CRITICAL: Deactivate source first so clone spawns inactive
                // This prevents ALL Update() calls from running on the clone before we strip components
                bool wasActive = ac.gameObject.activeSelf;
                ac.gameObject.SetActive(false);
                var clone = UnityEngine.Object.Instantiate(ac.gameObject);
                ac.gameObject.SetActive(wasActive); // restore player aircraft immediately

                clone.name = "Drone_" + UnityEngine.Random.Range(1000, 9999);
                clone.transform.position = spawnPos;
                clone.transform.rotation = rotation;

                // Strip everything that isn't visual/physics/weapons while clone is INACTIVE
                // DestroyImmediate is safe here since no Update() is running
                var allComps = clone.GetComponentsInChildren<Component>(true);
                for (int c = 0; c < allComps.Length; c++)
                {
                    try
                    {
                        var comp = allComps[c];
                        if (comp == null) continue;
                        string typeName = comp.GetIl2CppType().Name;

                        // KEEP: Transform, MeshRenderer, MeshFilter, SkinnedMeshRenderer, Rigidbody
                        // KEEP: Weapon-related (FiringMechanism, Weapon, WeaponAttachment, Projectile)
                        // KEEP: Material-related
                        if (typeName == "Transform" || typeName == "RectTransform") continue;
                        if (typeName == "MeshRenderer" || typeName == "MeshFilter" || typeName == "SkinnedMeshRenderer") continue;
                        if (typeName == "Rigidbody") continue;
                        if (typeName == "Collider" || typeName == "BoxCollider" || typeName == "SphereCollider" ||
                            typeName == "CapsuleCollider" || typeName == "MeshCollider") continue;
                        if (typeName.Contains("FiringMechanism") || typeName.Contains("Weapon") ||
                            typeName.Contains("Projectile") || typeName.Contains("Hardpoint")) continue;
                        if (typeName == "Material" || typeName == "Renderer") continue;

                        // DestroyImmediate since clone is inactive — no deferred destruction issues
                        try { UnityEngine.Object.DestroyImmediate(comp); } catch { }
                    }
                    catch { }
                }

                // Also nuke all child GameObjects that are UI/camera/audio containers
                // These hold OVRManager, cameras, input controllers, tablets, etc.
                var childCount = clone.transform.childCount;
                var toDestroy = new System.Collections.Generic.List<GameObject>();
                for (int i = 0; i < childCount; i++)
                {
                    try
                    {
                        var child = clone.transform.GetChild(i);
                        if (child == null) continue;
                        string cn = child.name.ToLower();
                        // Keep visual mesh objects, destroy everything else that's clearly a system
                        if (cn.Contains("camera") || cn.Contains("ovr") || cn.Contains("oculusmr") ||
                            cn.Contains("input") || cn.Contains("tablet") || cn.Contains("efi") ||
                            cn.Contains("ui") || cn.Contains("canvas") || cn.Contains("tutorial") ||
                            cn.Contains("controller") || cn.Contains("grab") || cn.Contains("hand") ||
                            cn.Contains("player") || cn.Contains("audio") || cn.Contains("sound") ||
                            cn.Contains("networked") || cn.Contains("photon") || cn.Contains("compass") ||
                            cn.Contains("needle") || cn.Contains("instrument") || cn.Contains("gauge") ||
                            cn.Contains("stall") || cn.Contains("electrical") || cn.Contains("seat"))
                        {
                            toDestroy.Add(child.gameObject);
                        }
                    }
                    catch { }
                }
                foreach (var go in toDestroy)
                    try { UnityEngine.Object.DestroyImmediate(go); } catch { }

                // NOW activate — only visual mesh + rigidbody + weapons remain
                clone.SetActive(true);

                // Setup rigidbody — low drag so thrust actually moves it
                var rb = clone.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = false;
                    rb.useGravity = false;
                    rb.drag = 0.15f;       // low drag = maintains speed like a real plane
                    rb.angularDrag = 3f;    // high angular drag = no tumbling
                    rb.mass = 500f;         // reasonable aircraft mass
                    rb.velocity = clone.transform.forward * 60f; // spawn already moving
                }

                // Force spawn weapons on the drone
                try
                {
                    var wa = clone.GetComponentInChildren<Il2CppWeapon.WeaponAttachment>();
                    if (wa != null)
                    {
                        wa.m_forceSpawnAll = true;
                        wa.SpawnWeapons();
                    }
                    // Set all firing mechanisms to infinite ammo
                    var fms = clone.GetComponentsInChildren<Il2CppWeapon.FiringMechanismBase>(true);
                    for (int i = 0; i < fms.Length; i++)
                    {
                        fms[i].gameObject.SetActive(true);
                        var fm = fms[i].TryCast<Il2CppWeapon.FiringMechanism>();
                        if (fm != null) fm.m_isInfiniteAmmo = true;
                    }
                }
                catch (Exception ex) { LoggerInstance.Msg($"[DRONE] Weapon setup: {ex.Message}"); }

                // Disable engine sounds on drone to reduce audio chaos
                try
                {
                    var audioSrcs = clone.GetComponentsInChildren<AudioSource>(true);
                    for (int i = 0; i < audioSrcs.Length; i++)
                        audioSrcs[i].volume *= 0.3f; // reduce volume instead of muting completely
                }
                catch { }

                var drone = new DroneAI { go = clone, rb = rb, alive = true };
                activeDrones.Add(drone);
                spawnedCombatants.Add(clone);
                combatActiveFighters = spawnedCombatants.Count;

                LoggerInstance.Msg($"[DRONE] Spawned '{clone.name}' at ({spawnPos.x:F0}, {spawnPos.y:F0}, {spawnPos.z:F0})");
                SetStatus($"Drone spawned!");
            }
            catch (Exception ex)
            {
                LoggerInstance.Error($"[DRONE] Spawn failed: {ex}");
                SetStatus($"Drone failed: {ex.Message}");
            }
        }

        private void UpdateDrones()
        {
            var ac = GameManager.ControllerAircraft;
            if (ac == null) return;
            Vector3 playerPos = ac.transform.position;
            Vector3 playerVel = Vector3.zero;
            try { var prb = ac.GetComponent<Rigidbody>(); if (prb != null) playerVel = prb.velocity; } catch { }

            for (int i = activeDrones.Count - 1; i >= 0; i--)
            {
                var drone = activeDrones[i];

                if (drone.go == null || !drone.go.activeSelf)
                {
                    activeDrones.RemoveAt(i);
                    continue;
                }

                Vector3 dronePos = drone.go.transform.position;
                Vector3 toPlayer = playerPos - dronePos;
                float dist = toPlayer.magnitude;
                float dt = Time.deltaTime;

                if (dist > 2500f)
                {
                    UnityEngine.Object.Destroy(drone.go);
                    activeDrones.RemoveAt(i);
                    LoggerInstance.Msg("[DRONE] Despawned — too far");
                    continue;
                }

                // --- ATTACK PATTERN STATE MACHINE ---
                Vector3 targetPos;

                if (drone.attackPhase == 2f)
                {
                    // BREAK — hard turn away after a pass, then re-engage
                    drone.breakTimer -= dt;
                    targetPos = dronePos + drone.breakDir * 400f;
                    if (drone.breakTimer <= 0f)
                        drone.attackPhase = 0f; // back to approach
                }
                else if (dist < 120f)
                {
                    // COMMIT/OVERSHOOT — flew past player, break off
                    drone.attackPhase = 2f;
                    drone.breakTimer = 2.5f + UnityEngine.Random.Range(0f, 1.5f);
                    // Break direction: up and to one side
                    float side = (UnityEngine.Random.value > 0.5f) ? 1f : -1f;
                    drone.breakDir = (drone.go.transform.forward + drone.go.transform.right * side * 0.7f + Vector3.up * 0.5f).normalized;
                    targetPos = dronePos + drone.breakDir * 400f;
                }
                else
                {
                    // APPROACH — lead the player for intercept
                    drone.attackPhase = 0f;
                    float closingSpeed = Mathf.Max(30f, drone.rb != null ? drone.rb.velocity.magnitude : 50f);
                    float eta = dist / closingSpeed;
                    targetPos = playerPos + playerVel * eta * 0.5f; // lead target

                    // Slight offset so we don't just ram them head-on
                    if (dist < 400f && dist > 200f)
                        targetPos += drone.go.transform.right * 40f;
                }

                // --- ALTITUDE FLOOR ---
                if (dronePos.y < drone.minAlt)
                    targetPos.y = drone.minAlt + 80f;
                if (targetPos.y < drone.minAlt)
                    targetPos.y = drone.minAlt + 30f;

                // --- ROTATION — snap to target with banking ---
                Vector3 dir = (targetPos - dronePos).normalized;
                if (dir.sqrMagnitude > 0.001f)
                {
                    Quaternion targetRot = Quaternion.LookRotation(dir);

                    // Calculate bank angle based on horizontal turn rate
                    Vector3 localTarget = drone.go.transform.InverseTransformDirection(dir);
                    float targetBank = Mathf.Clamp(-localTarget.x * 60f, -45f, 45f);
                    drone.bankAngle = Mathf.Lerp(drone.bankAngle, targetBank, dt * 3f);

                    // Apply rotation with bank
                    drone.go.transform.rotation = Quaternion.Slerp(drone.go.transform.rotation, targetRot, dt * drone.turnSpeed);
                    drone.go.transform.Rotate(0f, 0f, drone.bankAngle, Space.Self);
                }

                // --- THRUST — always fly forward like a plane ---
                if (drone.rb != null)
                {
                    float curSpeed = drone.rb.velocity.magnitude;

                    // Proportional thrust: push harder when slow, ease off at max
                    float thrustMul = Mathf.Clamp01(1f - (curSpeed / drone.maxSpeed));
                    drone.rb.AddForce(drone.go.transform.forward * drone.speed * (0.3f + thrustMul * 0.7f), ForceMode.Acceleration);

                    // Gentle lift to counteract gravity-free sink from drag
                    drone.rb.AddForce(Vector3.up * 12f, ForceMode.Acceleration);

                    // Hard speed cap
                    if (curSpeed > drone.maxSpeed)
                        drone.rb.velocity = drone.rb.velocity.normalized * drone.maxSpeed;

                    // Emergency pull-up
                    if (drone.rb.velocity.y < -20f)
                        drone.rb.AddForce(Vector3.up * 40f, ForceMode.Acceleration);
                }

                // --- WEAPONS --- fire on approach when lined up
                float angle = Vector3.Angle(drone.go.transform.forward, toPlayer);
                drone.fireTimer -= dt;

                if (dist < drone.fireRange && angle < drone.fireAngle && drone.fireTimer <= 0f && drone.attackPhase != 2f)
                {
                    drone.fireTimer = drone.fireInterval;
                    try
                    {
                        var fms = drone.go.GetComponentsInChildren<Il2CppWeapon.FiringMechanismBase>();
                        for (int w = 0; w < fms.Length; w++)
                        {
                            try
                            {
                                fms[w].m_isEnabled = true;
                                fms[w].m_readyToFire = true;
                                fms[w].m_triggerWasPulled = true;
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
                else
                {
                    try
                    {
                        var fms = drone.go.GetComponentsInChildren<Il2CppWeapon.FiringMechanismBase>();
                        for (int w = 0; w < fms.Length; w++)
                        {
                            try { fms[w].m_triggerWasPulled = false; }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
        }

        private void SpawnDroneWave(int count)
        {
            var ac = GameManager.ControllerAircraft;
            if (ac == null) { SetStatus("Not in aircraft"); return; }

            for (int i = 0; i < count; i++)
            {
                float angle = (360f / count) * i + UnityEngine.Random.Range(-20f, 20f);
                float dist = UnityEngine.Random.Range(400f, 700f);
                float alt = UnityEngine.Random.Range(20f, 100f);
                Vector3 offset = Quaternion.Euler(0, angle, 0) * Vector3.forward * dist;
                Vector3 spawnPos = ac.transform.position + offset + Vector3.up * alt;
                Quaternion rot = Quaternion.LookRotation(ac.transform.position - spawnPos);
                SpawnDrone(spawnPos, rot);
            }
        }

        private void SpawnWave(int fighterCount)
        {
            var ac = GameManager.ControllerAircraft;
            if (ac == null) { SetStatus("Not in aircraft"); return; }

            for (int i = 0; i < fighterCount; i++)
            {
                // Spawn in a spread pattern around the player
                float angle = (360f / fighterCount) * i + UnityEngine.Random.Range(-20f, 20f);
                float dist = UnityEngine.Random.Range(300f, 600f);
                float alt = UnityEngine.Random.Range(-30f, 80f);
                Vector3 offset = Quaternion.Euler(0, angle, 0) * Vector3.forward * dist;
                Vector3 spawnPos = ac.transform.position + offset + Vector3.up * alt;

                // Face toward player
                Quaternion rot = Quaternion.LookRotation(ac.transform.position - spawnPos);
                SpawnFighter(spawnPos, rot);
            }
        }

        private void StartBattleMode(string mode)
        {
            if (!combatPrefabsScanned) ScanCombatPrefabs();
            if (cachedFighterPrefabs.Count == 0) { SetStatus("No combat prefabs found — try a combat mission first to load them"); return; }

            // Clear existing
            ClearCombatants();

            switch (mode)
            {
                case "skirmish":
                    combatModeName = "SKIRMISH";
                    combatMaxFighters = 2;
                    combatSpawnInterval = 30f;
                    SpawnWave(2);
                    break;
                case "small":
                    combatModeName = "SMALL BATTLE";
                    combatMaxFighters = 4;
                    combatSpawnInterval = 20f;
                    SpawnWave(4);
                    break;
                case "invasion":
                    combatModeName = "INVASION";
                    combatMaxFighters = 8;
                    combatSpawnInterval = 10f;
                    SpawnWave(8);
                    break;
                case "allout":
                    combatModeName = "ALL OUT WAR";
                    combatMaxFighters = 15;
                    combatSpawnInterval = 5f;
                    SpawnWave(10); // first wave of 10, respawns up to 15
                    break;
                default:
                    combatModeName = "OFF";
                    combatMaxFighters = 0;
                    combatSpawnInterval = 0;
                    break;
            }

            combatSpawnTimer = 0f;
            SetStatus($"Battle: {combatModeName}!");
            LoggerInstance.Msg($"[COMBAT] Battle mode: {combatModeName} — max={combatMaxFighters} interval={combatSpawnInterval}s");
        }

        private void ClearCombatants()
        {
            int cleared = 0;
            for (int i = spawnedCombatants.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (spawnedCombatants[i] != null)
                    {
                        UnityEngine.Object.Destroy(spawnedCombatants[i]);
                        cleared++;
                    }
                }
                catch { }
            }
            spawnedCombatants.Clear();
            activeDrones.Clear();
            combatActiveFighters = 0;
            combatSpawnTimer = 0f;
            if (cleared > 0) LoggerInstance.Msg($"[COMBAT] Cleared {cleared} combatants");
        }

        private void UpdateCombatSpawner()
        {
            if (combatSpawnInterval <= 0 || combatMaxFighters <= 0) return;

            // Clean up destroyed combatants
            for (int i = spawnedCombatants.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (spawnedCombatants[i] == null || !spawnedCombatants[i].activeSelf)
                    {
                        spawnedCombatants.RemoveAt(i);
                    }
                }
                catch { spawnedCombatants.RemoveAt(i); }
            }
            combatActiveFighters = spawnedCombatants.Count;

            // Respawn if below max
            if (combatActiveFighters < combatMaxFighters)
            {
                combatSpawnTimer += Time.deltaTime;
                if (combatSpawnTimer >= combatSpawnInterval)
                {
                    combatSpawnTimer = 0f;
                    int toSpawn = Math.Min(2, combatMaxFighters - combatActiveFighters); // spawn up to 2 at a time
                    SpawnWave(toSpawn);
                    LoggerInstance.Msg($"[COMBAT] Respawned {toSpawn} — active: {combatActiveFighters + toSpawn}/{combatMaxFighters}");
                }
            }
        }

        private void BuildPhysics()
        {
            if (aircraftConfigs.Count == 0)
            {
                H("No configs loaded yet");
                B("Scan for aircraft configs", () => {
                    try
                    {
                        var all = Resources.FindObjectsOfTypeAll<AircraftControllerConfigDO>();
                        if (all != null)
                        {
                            for (int i = 0; i < all.Count; i++)
                            {
                                var c = all[i];
                                if (c != null && !aircraftConfigs.Contains(c))
                                {
                                    aircraftConfigs.Add(c);
                                    string n = c.name ?? "?";
                                    origPhysics[n] = new float[] {
                                        c.MaxEnginePower, c.Lift, c.StallSpeed, c.RollEffect, c.PitchEffect,
                                        c.YawEffect, c.DragIncreaseFactor, c.ThrottleChangeSpeed,
                                        c.BankedYawEffect, c.BankedTurnEffect, c.AerodynamicEffect,
                                        c.MaxAerodynamicEffectSpeed, c.AutoTurnPitch, c.AutoRollLevel,
                                        c.AutoPitchLevel, c.AirBrakesEffect, c.WheelTorque, c.WheelBrakeTorque,
                                        c.ZeroLiftSpeed
                                    };
                                    LoggerInstance.Msg($"[PHYSICS] Found '{n}': Power={c.MaxEnginePower:F0}");
                                }
                            }
                        }
                        SetStatus($"Found {aircraftConfigs.Count} configs");
                    }
                    catch (Exception ex) { SetStatus($"Scan failed: {ex.Message}"); }
                });
                return;
            }

            foreach (var cfg in aircraftConfigs)
            {
                string n = cfg.name ?? "?";
                var c = cfg;
                H($"--- {n} ---");
                S("Power", c.MaxEnginePower, 0, 5000, 50, v => c.MaxEnginePower = v);
                S("Lift", c.Lift, 0, 200, 5, v => c.Lift = v);
                S("Stall", c.StallSpeed, 0, 200, 5, v => c.StallSpeed = v);
                S("Roll", c.RollEffect, 0, 50, 1, v => c.RollEffect = v);
                S("Pitch", c.PitchEffect, 0, 50, 1, v => c.PitchEffect = v);
                S("Yaw", c.YawEffect, 0, 50, 1, v => c.YawEffect = v);
                S("Drag", c.DragIncreaseFactor, 0.01f, 10, 0.2f, v => c.DragIncreaseFactor = v);
                S("Throttle Spd", c.ThrottleChangeSpeed, 0, 20, 0.5f, v => c.ThrottleChangeSpeed = v);
                B($"Reset {n}", () => { ResetPhysics(c); SetStatus($"Reset {n}"); });
            }

            H("--- PRESETS (ALL AIRCRAFT) ---");
            B("INSANE (10x power, no stall) LIVE", () => {
                ApplyLivePreset(10f, 1f, 0.1f, 3f);
                SetStatus("INSANE MODE LIVE!");
            });
            B("GLIDER (no engine, max aero)", () => {
                foreach (var c in aircraftConfigs) { try { c.MaxEnginePower = 0; c.AerodynamicEffect = 5; c.Lift = 80; c.DragIncreaseFactor = 0.2f; c.StallSpeed = 5; } catch { } }
                // Also zero out live engine thrust
                try {
                    var ac = GameManager.ControllerAircraft;
                    if (ac != null) {
                        var engines = ac.m_aircraftEngines;
                        if (engines != null) for (int i = 0; i < engines.Count; i++) { var e = engines[i]?.TryCast<AircraftEngine>(); if (e != null) e.m_forceAtMaxRPM = 0; }
                    }
                } catch { }
                SetStatus("GLIDER MODE!");
            });
            B("Reset ALL to original (LIVE)", () => {
                ResetLiveAircraft();
                SetStatus("All reset");
            });
        }

        private void BuildAircraft()
        {
            H("--- COPY PHYSICS BETWEEN AIRCRAFT ---");
            if (aircraftConfigs.Count < 2) { H("Need 2+ configs loaded"); return; }

            for (int i = 0; i < aircraftConfigs.Count; i++)
            {
                var src = aircraftConfigs[i];
                string sn = src.name ?? $"#{i}";
                H($"  {sn}: P={src.MaxEnginePower:F0} L={src.Lift:F1} S={src.StallSpeed:F1}");
            }

            H("--- APPLY ONE TO ALL ---");
            for (int i = 0; i < aircraftConfigs.Count; i++)
            {
                var src = aircraftConfigs[i];
                string sn = src.name ?? $"#{i}";
                var s = src;
                B($"All fly like {sn}", () => {
                    foreach (var dst in aircraftConfigs)
                    {
                        if (dst == s) continue;
                        try
                        {
                            dst.MaxEnginePower = s.MaxEnginePower; dst.Lift = s.Lift; dst.StallSpeed = s.StallSpeed;
                            dst.RollEffect = s.RollEffect; dst.PitchEffect = s.PitchEffect; dst.YawEffect = s.YawEffect;
                            dst.DragIncreaseFactor = s.DragIncreaseFactor; dst.ThrottleChangeSpeed = s.ThrottleChangeSpeed;
                            dst.AerodynamicEffect = s.AerodynamicEffect; dst.AutoRollLevel = s.AutoRollLevel; dst.AutoPitchLevel = s.AutoPitchLevel;
                        }
                        catch { }
                    }
                    SetStatus($"All aircraft = {sn}");
                });
            }

            H("--- FLY AS (model + physics) ---");
            B("Fly as NPC Glider", () => { TryModelSwap("Glider (Ambient Vehicle)"); ApplyCustomPhysics(0, 100, 3, 0.15f, 5, 25); });
            B("Fly as NPC Ultralight", () => { TryModelSwap("Ultralight (Ambient Vehicle)"); ApplyCustomPhysics(400, 50, 15, 0.8f, 2, 15); });
            B("Fly as Fishing Boat (why not)", () => { TryModelSwap("Fishing Boat A (Ambient Vehicle)"); ApplyCustomPhysics(200, 30, 0, 2.0f, 1, 5); });
            B("Restore original", () => {
                try
                {
                    var player = GameManager.ControllerAircraft;
                    if (player == null) { SetStatus("No player aircraft"); return; }
                    // Remove swapped model
                    var swapped = player.transform.Find("SwappedModel");
                    if (swapped != null) GameObject.Destroy(swapped.gameObject);
                    // Re-enable all player renderers
                    var renderers = player.gameObject.GetComponentsInChildren<MeshRenderer>(true);
                    for (int i = 0; i < renderers.Count; i++) renderers[i].enabled = true;
                    // Reset physics
                    foreach (var c in aircraftConfigs) ResetPhysics(c);
                    SetStatus("Original restored");
                }
                catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); }
            });

            H("--- MESH / PREFAB SCAN ---");
            B("Scan aircraft models", () => {
                try
                {
                    LoggerInstance.Msg("[MESH] === SCAN ===");
                    var player = GameManager.ControllerAircraft;
                    if (player != null)
                    {
                        var r = player.gameObject.GetComponentsInChildren<MeshRenderer>();
                        LoggerInstance.Msg($"[MESH] PLAYER '{player.gameObject.name}': {r.Count} renderers");
                        for (int i = 0; i < Math.Min(r.Count, 10); i++)
                            LoggerInstance.Msg($"[MESH]   {r[i].gameObject.name}");
                    }
                    // Scan for all prefab-like aircraft
                    var allGO = Resources.FindObjectsOfTypeAll<GameObject>();
                    int npc = 0;
                    for (int i = 0; i < allGO.Count; i++)
                    {
                        string gn = allGO[i]?.name?.ToLower() ?? "";
                        if ((gn.Contains("aircraft") || gn.Contains("plane") || gn.Contains("phoenix") ||
                             gn.Contains("stallion") || gn.Contains("comet") || gn.Contains("newhawk") ||
                             gn.Contains("dragonfly") || gn.Contains("kodiak") || gn.Contains("ultralight") ||
                             gn.Contains("biplane") || gn.Contains("glider") || gn.Contains("proxy")) &&
                            !gn.Contains("col") && !gn.Contains("collider"))
                        {
                            LoggerInstance.Msg($"[MESH] GO '{allGO[i].name}' active={allGO[i].activeSelf}");
                            npc++;
                        }
                    }
                    SetStatus($"Player + {npc} aircraft objects — check log");
                }
                catch (Exception ex) { SetStatus($"Scan failed: {ex.Message}"); }
            });
        }

        private void BuildExplore()
        {
            H("--- HIDDEN CONTENT SCAN ---");
            B("Scan for ships/carrier/rig", () => {
                try
                {
                    var allGO = Resources.FindObjectsOfTypeAll<GameObject>();
                    int found = 0;
                    LoggerInstance.Msg("[EXPLORE] === HIDDEN CONTENT ===");
                    for (int i = 0; i < allGO.Count; i++)
                    {
                        string n = allGO[i]?.name?.ToLower() ?? "";
                        if (n.Contains("carrier") || n.Contains("oil") || n.Contains("rig") ||
                            n.Contains("ship") || n.Contains("boat") || n.Contains("naval") ||
                            n.Contains("convoy") || n.Contains("turret") || n.Contains("tank") ||
                            n.Contains("military") || n.Contains("destroyer") || n.Contains("platform") ||
                            n.Contains("barge") || n.Contains("submarine") || n.Contains("cruiser"))
                        {
                            var go = allGO[i];
                            string path = go.name;
                            var t = go.transform.parent;
                            int d = 0;
                            while (t != null && d < 5) { path = t.gameObject.name + "/" + path; t = t.parent; d++; }
                            LoggerInstance.Msg($"[EXPLORE] '{go.name}' active={go.activeSelf} path={path}");
                            found++;
                        }
                    }
                    SetStatus($"{found} objects — check log");
                }
                catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); }
            });

            B("Scan ALL vehicles/traffic", () => {
                try
                {
                    var allMB = GameObject.FindObjectsOfType<MonoBehaviour>();
                    int count = 0;
                    LoggerInstance.Msg("[EXPLORE] === VEHICLES ===");
                    for (int i = 0; i < allMB.Count; i++)
                    {
                        string fn = allMB[i]?.GetType()?.FullName ?? "";
                        if (fn.Contains("Vehicle") || fn.Contains("Ship") || fn.Contains("Traffic") ||
                            fn.Contains("Convoy") || fn.Contains("Combat") || fn.Contains("Naval") ||
                            fn.Contains("Targetable") || fn.Contains("Aircraft"))
                        {
                            string tn = allMB[i].GetType().Name;
                            string gn = allMB[i].gameObject?.name ?? "?";
                            LoggerInstance.Msg($"[EXPLORE] {tn} on '{gn}' active={allMB[i].gameObject?.activeSelf}");
                            count++;
                        }
                    }
                    SetStatus($"{count} vehicle components — check log");
                }
                catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); }
            });

            B("Carrier Diagnostic (check log)", () => {
                try
                {
                    LoggerInstance.Msg("[CARRIER] === AIRCRAFT CARRIER DIAGNOSTIC ===");

                    // Check ScreenProjectorMediator state
                    var mediators = Resources.FindObjectsOfTypeAll<Il2Cpp.ScreenProjectorMediator>();
                    LoggerInstance.Msg($"[CARRIER] ScreenProjectorMediator instances: {mediators?.Length ?? 0}");
                    for (int i = 0; i < (mediators?.Length ?? 0); i++)
                    {
                        var m = mediators[i];
                        LoggerInstance.Msg($"[CARRIER]   Mediator[{i}]: m_isOnAircraftCarrier={m.m_isOnAircraftCarrier}");
                        try
                        {
                            var ftc = m.m_fastTravelController;
                            if (ftc != null)
                                LoggerInstance.Msg($"[CARRIER]   FastTravel: isForFastTravel={ftc.m_isForFastTravel} targetIsland={ftc.m_targetIslandIntValue} levelType={ftc.m_levelType}");
                            else
                                LoggerInstance.Msg($"[CARRIER]   FastTravel: null");
                        }
                        catch (Exception ex) { LoggerInstance.Msg($"[CARRIER]   FastTravel error: {ex.Message}"); }
                    }

                    // Check MainMenuScreenViewDataDO — it's not a UnityEngine.Object, so find via MainMenuScreenView
                    LoggerInstance.Msg("[CARRIER] MainMenuScreenViewDataDO: checked via mediator above");

                    // Check the signal
                    try
                    {
                        var signal = ScreenProjectorSignals.GotoAircraftCarrierClicked;
                        LoggerInstance.Msg($"[CARRIER] GotoAircraftCarrierClicked signal: {(signal != null ? "EXISTS" : "NULL")}");
                    }
                    catch (Exception ex) { LoggerInstance.Msg($"[CARRIER] Signal error: {ex.Message}"); }

                    // Check MainMenuScreenView for 4/5 button layouts
                    var menuViews = Resources.FindObjectsOfTypeAll<Il2Cpp.MainMenuScreenView>();
                    LoggerInstance.Msg($"[CARRIER] MainMenuScreenView instances: {menuViews?.Length ?? 0}");
                    for (int i = 0; i < (menuViews?.Length ?? 0); i++)
                    {
                        try
                        {
                            var mv = menuViews[i];
                            var p4 = mv.m_contentParentFor4Buttons;
                            var p5 = mv.m_contentParentFor5buttons;
                            LoggerInstance.Msg($"[CARRIER]   View[{i}]: 4btn={p4?.name ?? "null"} active={p4?.activeSelf} | 5btn={p5?.name ?? "null"} active={p5?.activeSelf}");
                        }
                        catch (Exception ex) { LoggerInstance.Msg($"[CARRIER]   View[{i}]: error={ex.Message}"); }
                    }

                    // Scan for any GameObjects with "carrier" in name
                    var allGO = Resources.FindObjectsOfTypeAll<GameObject>();
                    int carrierGOs = 0;
                    for (int i = 0; i < allGO.Length; i++)
                    {
                        string n = allGO[i]?.name?.ToLower() ?? "";
                        if (n.Contains("carrier"))
                        {
                            LoggerInstance.Msg($"[CARRIER] GO: '{allGO[i].name}' active={allGO[i].activeSelf} root='{allGO[i].transform.root.gameObject.name}'");
                            carrierGOs++;
                        }
                    }
                    LoggerInstance.Msg($"[CARRIER] GameObjects with 'carrier' in name: {carrierGOs}");
                    LoggerInstance.Msg("[CARRIER] === END DIAGNOSTIC ===");
                    SetStatus($"Carrier diag done — {mediators?.Length ?? 0} mediators, {carrierGOs} GOs — check log");
                }
                catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); LoggerInstance.Error($"[CARRIER] {ex}"); }
            });

            B("Force Carrier Flag ON", () => {
                try
                {
                    var mediators = Resources.FindObjectsOfTypeAll<Il2Cpp.ScreenProjectorMediator>();
                    for (int i = 0; i < (mediators?.Length ?? 0); i++)
                    {
                        mediators[i].m_isOnAircraftCarrier = true;
                        LoggerInstance.Msg($"[CARRIER] Set m_isOnAircraftCarrier=true on mediator[{i}]");
                    }
                    SetStatus($"Carrier flag ON on {mediators?.Length ?? 0} mediators");
                }
                catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); }
            });

            B("Trigger Carrier Signal", () => {
                try
                {
                    LoggerInstance.Msg("[CARRIER] Dispatching GotoAircraftCarrierClicked...");
                    var signal = ScreenProjectorSignals.GotoAircraftCarrierClicked;
                    if (signal != null)
                    {
                        signal.Dispatch();
                        LoggerInstance.Msg("[CARRIER] Signal dispatched!");
                        SetStatus("Carrier signal sent!");
                    }
                    else
                    {
                        LoggerInstance.Msg("[CARRIER] Signal is null — not initialized");
                        SetStatus("Signal is null");
                    }
                }
                catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); LoggerInstance.Error($"[CARRIER] {ex}"); }
            });

            H("--- QUALITY ---");
            B("Force HIGH Materials", () => {
                try
                {
                    var qm = GameObject.FindObjectOfType<MonoBehaviour>();
                    // Try to find QualityManager by scanning all MonoBehaviours
                    var allMB = GameObject.FindObjectsOfType<MonoBehaviour>();
                    for (int i = 0; i < allMB.Count; i++)
                    {
                        if (allMB[i].GetType().Name == "QualityManager")
                        {
                            // Call SetTierIndex and ApplyChanges via Il2Cpp invoke
                            var type = allMB[i].GetType();
                            var setMethod = type.GetMethod("SetTierIndex");
                            var applyMethod = type.GetMethod("ApplyChanges");
                            if (setMethod != null && applyMethod != null)
                            {
                                setMethod.Invoke(allMB[i], new object[] { 0, 4, 1 }); // Environment > Material > High
                                applyMethod.Invoke(allMB[i], null);
                                SetStatus("Materials set to HIGH!");
                            }
                            else SetStatus("QualityManager methods not found");
                            return;
                        }
                    }
                    SetStatus("QualityManager not found in scene");
                }
                catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); }
            });
        }

        private void BuildInfo()
        {
            H("--- PLAYER DATA ---");
            if (playerData != null)
            {
                try
                {
                    H($"  Money: ${playerData.money}");
                    H($"  Vehicles: unlocked={playerData.vehicleUnlocked} owned={playerData.vehicleOwned}");
                    H($"  Offices: unlocked={playerData.officeUnlocked} owned={playerData.officeOwned}");
                    H($"  Fuel: {playerData.isAircraftHasUnlimitedFuel} Ammo: {playerData.isAircraftHasUnlimitedAmmo}");
                    H($"  Island: {playerData.currentIsland} Vehicle: {playerData.currentVehicle}");
                    H($"  Save slot: {PlayerDataModel.SaveGameIndex}");
                }
                catch (Exception ex) { H($"  Error: {ex.Message}"); }
            }
            else H("  Not loaded yet");

            H("--- AIRCRAFT ---");
            H($"  {aircraftConfigs.Count} configs loaded");
            try
            {
                var ac = GameManager.ControllerAircraft;
                if (ac != null) H($"  Current: {ac.gameObject?.name}");
            }
            catch { }

            H("--- CRYPTO ---");
            if (cryptoConfig != null)
            {
                try { H($"  PW: {cryptoConfig.PasswordHash}"); H($"  Salt: {cryptoConfig.SaltKey}"); H($"  IV: {cryptoConfig.IVKey}"); H($"  Caesar: {cryptoConfig.CaesarKey}"); }
                catch { }
            }

            H("--- STATUS ---");
            H($"  GameManager: {(gameManager != null ? "OK" : "--")}");
            H($"  PlayerData: {(playerData != null ? "OK" : "--")}");
            H($"  Configs: {aircraftConfigs.Count}");
            H($"  Crypto: {(cryptoConfig != null ? "OK" : "--")}");

            H("--- CONTROLS ---");
            H("  Space/F1 = Toggle menu + pause");
            H("  LStick/Arrows = Navigate");
            H("  RStick/L-R Arrows = Switch tabs");
            H("  A button/Enter = Activate");
            H("  B button = Close menu");
            H("  LStick X / A,D keys = Adjust sliders");
        }

        private void BuildDiagnostics()
        {
            H("=== EXPERIMENTAL — USE AT OWN RISK ===");

            // --- HUD ---
            B($"Vehicle Stats HUD [{(showHUD ? "ON" : "OFF")}] (F2)", () => { showHUD = !showHUD; SetStatus($"HUD: {(showHUD ? "ON — close menu to see" : "OFF")}"); });

            // --- ENGINE DIAGNOSTICS ---
            H("--- ENGINE DIAGNOSTICS ---");
            B("Dump Engine State (check log)", () => {
                try
                {
                    var ac = GameManager.ControllerAircraft;
                    if (ac == null) { SetStatus("Not in aircraft"); return; }

                    LoggerInstance.Msg("=== ENGINE DIAGNOSTICS ===");
                    var engines = ac.m_aircraftEngines;
                    if (engines != null)
                    {
                        for (int i = 0; i < engines.Count; i++)
                        {
                            var eng = engines[i]?.TryCast<AircraftEngine>();
                            if (eng == null) continue;
                            string type = "AircraftEngine";
                            var rotor = eng.TryCast<EngineRotor>();
                            var combustion = eng.TryCast<EngineCombustion>();
                            if (rotor != null) type = "EngineRotor";
                            else if (combustion != null) type = "EngineCombustion";

                            LoggerInstance.Msg($"[ENG{i}] Type={type}");
                            LoggerInstance.Msg($"  forceAtMaxRPM={eng.m_forceAtMaxRPM:F1}");
                            LoggerInstance.Msg($"  thrust={eng.m_thrust:F3} vectoredThrust={eng.m_vectoredThrust}");
                            LoggerInstance.Msg($"  currentRPM={eng.m_currentRPM:F1} maxRPM={eng.m_maxRPM:F1} idleRPM={eng.m_idleRPM:F1}");
                            LoggerInstance.Msg($"  desiredRPM={eng.m_desiredRPM:F1} unclampedRPM={eng.m_unclampedRPM:F1}");
                            LoggerInstance.Msg($"  normalizedRPM={eng.m_normalizedRPM:F3} airSpeedFactor={eng.m_airSpeedFactor:F3}");
                            LoggerInstance.Msg($"  rpmSmoothTime={eng.m_rpmSmoothTime:F3} rpmMaxSpeed={eng.m_rpmMaxSpeed:F1}");
                            LoggerInstance.Msg($"  wheelTorque={eng.m_wheelTorque:F1} maxWheelTorque={eng.m_maxWheelTorque:F1}");
                            LoggerInstance.Msg($"  zeroTorqueSpeed={eng.m_zeroTorqueSpeed:F1}");
                            LoggerInstance.Msg($"  AirSpeedThrustLimit={eng.AirSpeedThrustLimit:F3}");

                            if (rotor != null)
                            {
                                LoggerInstance.Msg($"  [ROTOR] rotorRPM={rotor.m_rotorRPM:F1} maxRotorRPMForTorque={rotor.m_maxRotorRPMForTorque:F1}");
                                LoggerInstance.Msg($"  [ROTOR] brakeTorque={rotor.m_brakeTorque:F1} currentThrottle={rotor.m_currentThrottle:F3}");
                                LoggerInstance.Msg($"  [ROTOR] normalizedRotorRPM={rotor.m_normalizedRotorRPM:F3}");
                                LoggerInstance.Msg($"  [ROTOR] throttleIncreaseSmoothTime={rotor.m_throttleIncreaseSmoothTime:F3}");
                                LoggerInstance.Msg($"  [ROTOR] throttleReductionSmoothTime={rotor.m_throttleReductionSmoothTime:F3}");
                            }
                        }
                    }

                    // Physics controller
                    var rta = ac.gameObject.GetComponent<RigidTargetableAircraft>();
                    if (rta != null)
                    {
                        var phys = rta.m_aircraftSystem?.TryCast<AircraftPhysicsController>();
                        if (phys != null)
                        {
                            LoggerInstance.Msg($"[PHYSICS] thrust={phys.m_thrust:F3} enginePower={phys.m_enginePower:F3}");
                            LoggerInstance.Msg($"[PHYSICS] forwardSpeed={phys.m_forwardSpeed:F1} knots={phys.m_forwardSpeedInKnots:F1}");
                            LoggerInstance.Msg($"[PHYSICS] aeroFactor={phys.m_aeroFactor:F4}");
                            LoggerInstance.Msg($"[PHYSICS] originalDrag={phys.m_originalDrag:F4} originalAngDrag={phys.m_originalAngularDrag:F4}");
                            LoggerInstance.Msg($"[PHYSICS] altitude={phys.m_altitude:F1}");
                            LoggerInstance.Msg($"[PHYSICS] pitch={phys.m_pitchRadians:F3} roll={phys.m_rollRadians:F3}");
                            var cfg = phys.m_config;
                            if (cfg != null)
                            {
                                LoggerInstance.Msg($"[CONFIG] name={cfg.name}");
                                LoggerInstance.Msg($"[CONFIG] MaxEnginePower={cfg.MaxEnginePower:F0} Lift={cfg.Lift:F1}");
                                LoggerInstance.Msg($"[CONFIG] StallSpeed={cfg.StallSpeed:F1} DragIncreaseFactor={cfg.DragIncreaseFactor:F3}");
                                LoggerInstance.Msg($"[CONFIG] AerodynamicEffect={cfg.AerodynamicEffect:F2} MaxAeroSpeed={cfg.MaxAerodynamicEffectSpeed:F0}");
                                LoggerInstance.Msg($"[CONFIG] RollEffect={cfg.RollEffect:F2} PitchEffect={cfg.PitchEffect:F2} YawEffect={cfg.YawEffect:F2}");
                                LoggerInstance.Msg($"[CONFIG] BankedTurnEffect={cfg.BankedTurnEffect:F2} BankedYawEffect={cfg.BankedYawEffect:F2}");
                                LoggerInstance.Msg($"[CONFIG] ThrottleChangeSpeed={cfg.ThrottleChangeSpeed:F2}");
                                LoggerInstance.Msg($"[CONFIG] AirBrakesEffect={cfg.AirBrakesEffect:F2}");
                                LoggerInstance.Msg($"[CONFIG] WheelTorque={cfg.WheelTorque:F1} WheelBrakeTorque={cfg.WheelBrakeTorque:F1}");
                                LoggerInstance.Msg($"[CONFIG] ZeroLiftSpeed={cfg.ZeroLiftSpeed:F1}");
                            }
                        }
                    }

                    // Wind
                    try
                    {
                        LoggerInstance.Msg($"[WIND] speed={AlwaysLoadedManager.WindSpeed:F1} vel={AlwaysLoadedManager.WindVelocity}");
                    }
                    catch { }

                    // Wing + PropWash + Drag diagnostics
                    try
                    {
                        var rb = ac.gameObject.GetComponent<Rigidbody>();
                        LoggerInstance.Msg($"[DRAG] rb.drag={rb?.drag:F4} rb.angularDrag={rb?.angularDrag:F4} rb.mass={rb?.mass:F1}");

                        var wings = ac.gameObject.GetComponentsInChildren<Wing>();
                        LoggerInstance.Msg($"[WINGS] Total Wing components: {wings?.Length ?? 0}");
                        if (wings != null)
                        {
                            for (int w = 0; w < wings.Length; w++)
                            {
                                var wing = wings[w];
                                if (wing == null) continue;
                                bool hasPropWash = wing.m_attachedPropWash != null;
                                bool hasGroundEffect = wing.m_attachedGroundEffect != null;
                                bool affectedByWind = wing.m_affectedByWind;
                                LoggerInstance.Msg($"[WING{w}] '{wing.gameObject.name}' area={wing.m_wingGeometry.WingArea:F4} CDOverride={wing.CDOverride:F4} AoA={wing.AngleOfAttack:F2}");
                                LoggerInstance.Msg($"  propWash={hasPropWash} groundEffect={hasGroundEffect} affectedByWind={affectedByWind} canOptimize={wing.m_canOptimizeWing}");
                                LoggerInstance.Msg($"  deflection={wing.m_currentDeflection:F3} maxDeflection={wing.m_maxDeflectionDegrees:F1} sections={wing.SectionCount}");
                                LoggerInstance.Msg($"  liftForce={wing.m_wingForces.LiftForce} dragForce={wing.m_wingForces.DragForce}");
                                if (hasPropWash)
                                {
                                    var pw = wing.m_attachedPropWash;
                                    LoggerInstance.Msg($"  [PROPWASH] GO='{pw.gameObject.name}'");
                                }
                            }
                        }

                        // Also check AircraftAttachment array
                        var attachments = ac.m_aircraftAttachments;
                        LoggerInstance.Msg($"[ATTACHMENTS] Total: {attachments?.Length ?? 0}");
                        if (attachments != null)
                        {
                            for (int a = 0; a < attachments.Length; a++)
                            {
                                var att = attachments[a];
                                if (att == null) continue;
                                LoggerInstance.Msg($"[ATT{a}] '{att.gameObject.name}' type={att.GetIl2CppType().Name}");
                            }
                        }
                    }
                    catch (Exception ex2) { LoggerInstance.Msg($"[WINGS] Error: {ex2.Message}"); }

                    SetStatus("Dumped — check MelonLoader log");
                }
                catch (Exception ex) { SetStatus($"Dump failed: {ex.Message}"); LoggerInstance.Error($"[DIAG] {ex}"); }
            });

            // --- WEAPONS ---
            H("--- WEAPONS ---");
            B("Scan Weapons (check log)", () => {
                try
                {
                    var ac = GameManager.ControllerAircraft;
                    if (ac == null) { SetStatus("Not in aircraft"); return; }

                    LoggerInstance.Msg("=== WEAPON SCAN ===");
                    LoggerInstance.Msg($"[WEAP] Aircraft: {ac.gameObject.name}");

                    // Check for WeaponAttachment
                    var weapAttach = ac.gameObject.GetComponentInChildren<Il2CppWeapon.WeaponAttachment>();
                    if (weapAttach != null)
                    {
                        LoggerInstance.Msg($"[WEAP] WeaponAttachment found on '{weapAttach.gameObject.name}'");
                        var weapons = weapAttach.m_weapons;
                        LoggerInstance.Msg($"[WEAP] FiringMechanism count: {weapons?._size ?? 0}");
                        if (weapons != null)
                        {
                            for (int i = 0; i < weapons._size; i++)
                            {
                                var fm = weapons._items[i];
                                if (fm == null) continue;
                                LoggerInstance.Msg($"[WEAP]   FM[{i}]: '{fm.gameObject.name}' type={fm.GetIl2CppType().Name}");
                            }
                        }
                        var hardpoints = weapAttach.m_hardPoints;
                        LoggerInstance.Msg($"[WEAP] HardpointConfigs count: {hardpoints?._size ?? 0}");

                        // Check laser designator
                        var laser = weapAttach.m_laserDesignator;
                        LoggerInstance.Msg($"[WEAP] LaserDesignator: {(laser != null ? laser.name : "NULL")}");
                    }
                    else
                    {
                        LoggerInstance.Msg("[WEAP] No WeaponAttachment found on aircraft");
                    }

                    // Check VehicleSetup
                    try
                    {
                        var vs = GameManager.VehicleSetup;
                        if (vs != null)
                        {
                            LoggerInstance.Msg($"[WEAP] VehicleSetup found: infiniteAmmo={vs.m_isInfiniteAmmo} gunInitComplete={vs.m_gunInitComplete}");
                        }
                        else LoggerInstance.Msg("[WEAP] VehicleSetup: NULL");
                    }
                    catch { LoggerInstance.Msg("[WEAP] VehicleSetup: error accessing"); }

                    // Also log ALL inactive GameObjects on the aircraft
                    var allAcTransforms = ac.gameObject.GetComponentsInChildren<Transform>(true);
                    LoggerInstance.Msg($"[WEAP] --- Inactive objects on aircraft ({allAcTransforms.Length} total objects) ---");
                    for (int t = 0; t < allAcTransforms.Length; t++)
                    {
                        var tr = allAcTransforms[t];
                        if (!tr.gameObject.activeSelf)
                        {
                            var comps = tr.gameObject.GetComponents<Component>();
                            string compList = "";
                            for (int cc = 0; cc < comps.Length; cc++)
                                if (comps[cc] != null) compList += comps[cc].GetIl2CppType().Name + ",";
                            LoggerInstance.Msg($"[WEAP]   INACTIVE: '{tr.gameObject.name}' parent='{tr.parent?.gameObject.name}' [{compList}]");
                        }
                    }

                    // Dump ENTIRE aircraft hierarchy for weapon-related objects
                    LoggerInstance.Msg($"[WEAP] --- Full aircraft scan for weapon objects ---");
                    for (int t = 0; t < allAcTransforms.Length; t++)
                    {
                        var tr = allAcTransforms[t];
                        string gn = tr.gameObject.name.ToLower();
                        if (gn.Contains("weapon") || gn.Contains("gun") || gn.Contains("cannon") ||
                            gn.Contains("turret") || gn.Contains("firing") || gn.Contains("hardpoint") ||
                            gn.Contains("ammo") || gn.Contains("bullet") || gn.Contains("projectile") ||
                            gn.Contains("muzzle") || gn.Contains("barrel") || gn.Contains("laser") ||
                            gn.Contains("dart") || gn.Contains("grenade") || gn.Contains("machine") ||
                            gn.Contains("50cal") || gn.Contains("sidearm") || gn.Contains("holster"))
                        {
                            var comps = tr.gameObject.GetComponents<Component>();
                            string compList = "";
                            for (int cc = 0; cc < comps.Length; cc++)
                                if (comps[cc] != null) compList += comps[cc].GetIl2CppType().Name + ",";
                            LoggerInstance.Msg($"[WEAP]   AC[{t}]: '{tr.gameObject.name}' active={tr.gameObject.activeSelf} parent='{tr.parent?.gameObject.name}' [{compList}]");
                        }
                    }

                    // Dump ENTIRE aircraft hierarchy for weapon-related objects
                    var weapSysGO = weapAttach != null ? weapAttach.gameObject : null;
                    if (weapSysGO != null)
                    {
                        LoggerInstance.Msg($"[WEAP] --- Weapon System hierarchy (including inactive) ---");
                        var allChildren = weapSysGO.GetComponentsInChildren<Transform>(true); // true = include inactive
                        for (int c = 0; c < allChildren.Length; c++)
                        {
                            var t = allChildren[c];
                            string indent = "";
                            var p = t.parent;
                            while (p != null && p != weapSysGO.transform) { indent += "  "; p = p.parent; }
                            var comps = t.gameObject.GetComponents<Component>();
                            string compList = "";
                            for (int cc = 0; cc < comps.Length; cc++)
                            {
                                if (comps[cc] != null) compList += comps[cc].GetIl2CppType().Name + ",";
                            }
                            LoggerInstance.Msg($"[WEAP]   {indent}{t.gameObject.name} active={t.gameObject.activeSelf} [{compList}]");
                        }
                    }

                    // Search for ALL FiringMechanism/WeaponBase in memory (including inactive/prefabs)
                    var allFMRes = Resources.FindObjectsOfTypeAll<Il2CppWeapon.FiringMechanismBase>();
                    LoggerInstance.Msg($"[WEAP] All FiringMechanismBase (incl inactive): {allFMRes?.Length ?? 0}");
                    if (allFMRes != null)
                    {
                        for (int i = 0; i < Math.Min(allFMRes.Length, 20); i++)
                        {
                            var fm = allFMRes[i];
                            string rootName = "?";
                            try { rootName = fm.transform.root.gameObject.name; } catch { }
                            LoggerInstance.Msg($"[WEAP]   FM[{i}]: '{fm.gameObject.name}' root='{rootName}' active={fm.gameObject.activeSelf} type={fm.GetIl2CppType().Name}");
                        }
                    }

                    // Search for FiringMechanismConfigDO (weapon configs - ScriptableObjects)
                    var allConfigs = Resources.FindObjectsOfTypeAll<Il2CppWeapon.FiringMechanismConfigDO>();
                    LoggerInstance.Msg($"[WEAP] All FiringMechanismConfigDO: {allConfigs?.Length ?? 0}");
                    if (allConfigs != null)
                    {
                        for (int i = 0; i < Math.Min(allConfigs.Length, 20); i++)
                        {
                            var cfg = allConfigs[i];
                            LoggerInstance.Msg($"[WEAP]   Config[{i}]: '{cfg.name}'");
                        }
                    }

                    // Search for ProjectileConfigDO (ammo types)
                    var allProjCfg = Resources.FindObjectsOfTypeAll<Il2CppWeapon.ProjectileConfigDO>();
                    LoggerInstance.Msg($"[WEAP] All ProjectileConfigDO: {allProjCfg?.Length ?? 0}");
                    if (allProjCfg != null)
                    {
                        for (int i = 0; i < Math.Min(allProjCfg.Length, 20); i++)
                        {
                            var pc = allProjCfg[i];
                            LoggerInstance.Msg($"[WEAP]   ProjCfg[{i}]: '{pc.name}'");
                        }
                    }

                    SetStatus("Weapon scan done — check log");
                }
                catch (Exception ex) { SetStatus($"Scan failed: {ex.Message}"); LoggerInstance.Error($"[WEAP] {ex}"); }
            });

            // Equip/Spawn/Swap buttons moved to Combat tab
            B("Equip Grenade Launcher DELETEME", () => {
                try { EquipWeapon("Holstered Grenade Launcher"); }
                catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); LoggerInstance.Error($"[WEAP] {ex}"); }
            });
            B("Equip Dart Gun", () => {
                try { EquipWeapon("Holstered Dart Gun"); }
                catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); LoggerInstance.Error($"[WEAP] {ex}"); }
            });
            B("Force Spawn All Weapons", () => {
                try
                {
                    var ac = GameManager.ControllerAircraft;
                    if (ac == null) { SetStatus("Not in aircraft"); return; }
                    var weapAttach = ac.gameObject.GetComponentInChildren<Il2CppWeapon.WeaponAttachment>();
                    if (weapAttach == null) { SetStatus("No weapon system"); return; }

                    // Set force spawn flag — bypasses level type check
                    weapAttach.m_forceSpawnAll = true;
                    LoggerInstance.Msg("[WEAP] Set m_forceSpawnAll = true");

                    // Call SpawnWeapons — this will use CreateHardpointWeapon for each configured hardpoint
                    weapAttach.SpawnWeapons();
                    LoggerInstance.Msg("[WEAP] SpawnWeapons() called with forceSpawnAll");

                    // Check what we got
                    var fmsOnAc = ac.gameObject.GetComponentsInChildren<Il2CppWeapon.FiringMechanismBase>();
                    LoggerInstance.Msg($"[WEAP] FiringMechanisms after force spawn: {fmsOnAc?.Length ?? 0}");
                    for (int i = 0; i < (fmsOnAc?.Length ?? 0); i++)
                    {
                        LoggerInstance.Msg($"[WEAP]   FM[{i}]: '{fmsOnAc[i].gameObject.name}' type={fmsOnAc[i].GetIl2CppType().Name}");
                    }

                    // Initialize weapon systems
                    var vs = GameManager.VehicleSetup;
                    if (vs != null)
                    {
                        if (vs.m_controllerAircraft == null) vs.m_controllerAircraft = ac;
                        vs.m_isInfiniteAmmo = true;
                        vs.m_gunInitComplete = false;
                        vs.InitializeWeaponSystems();
                        vs.m_gunInitComplete = true;
                        LoggerInstance.Msg("[WEAP] InitializeWeaponSystems called");
                    }

                    // Activate muzzle flash points
                    var allT = ac.gameObject.GetComponentsInChildren<Transform>(true);
                    for (int i = 0; i < allT.Length; i++)
                    {
                        if (!allT[i].gameObject.activeSelf)
                        {
                            string gn = allT[i].gameObject.name.ToLower();
                            if (gn.Contains("machine gun point") || gn.Contains("muzzle"))
                            {
                                allT[i].gameObject.SetActive(true);
                                LoggerInstance.Msg($"[WEAP] Activated: '{allT[i].gameObject.name}'");
                            }
                        }
                    }

                    SetStatus($"Force spawned! {fmsOnAc?.Length ?? 0} weapons found");
                }
                catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); LoggerInstance.Error($"[WEAP] {ex}"); }
            });
            B("Swap Ammo: Grenades", () => { SwapProjectile("cfg_friendly_grenadelauncher"); });
            B("Swap Ammo: Darts", () => { SwapProjectile("cfg_friendly_dartgun"); });
            B("Swap Ammo: Stallion Bullets", () => { SwapProjectile("cfg_friendly_stallion"); });
            B("Activate All Weapons on Aircraft", () => {
                try
                {
                    var ac = GameManager.ControllerAircraft;
                    if (ac == null) { SetStatus("Not in aircraft"); return; }
                    // Find all inactive objects and activate weapon-related ones
                    var allT = ac.gameObject.GetComponentsInChildren<Transform>(true);
                    int activated = 0;
                    for (int i = 0; i < allT.Length; i++)
                    {
                        if (!allT[i].gameObject.activeSelf)
                        {
                            string gn = allT[i].gameObject.name.ToLower();
                            if (gn.Contains("weapon") || gn.Contains("gun") || gn.Contains("cannon") ||
                                gn.Contains("turret") || gn.Contains("firing") || gn.Contains("muzzle") ||
                                gn.Contains("barrel") || gn.Contains("hardpoint") || gn.Contains("dart") ||
                                gn.Contains("grenade") || gn.Contains("laser") || gn.Contains("sidearm") ||
                                gn.Contains("holster") || gn.Contains("machine"))
                            {
                                allT[i].gameObject.SetActive(true);
                                activated++;
                                LoggerInstance.Msg($"[WEAP] Activated: '{allT[i].gameObject.name}'");
                            }
                        }
                    }
                    // Also try InitializeWeaponSystems
                    var vs = GameManager.VehicleSetup;
                    if (vs != null) { vs.m_isInfiniteAmmo = true; vs.m_gunInitComplete = false; vs.InitializeWeaponSystems(); }
                    SetStatus($"Activated {activated} weapon objects");
                }
                catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); LoggerInstance.Error($"[WEAP] {ex}"); }
            });
            B("Unequip Weapon", () => {
                try
                {
                    var ac = GameManager.ControllerAircraft;
                    if (ac == null) { SetStatus("Not in aircraft"); return; }
                    var weapAttach = ac.gameObject.GetComponentInChildren<Il2CppWeapon.WeaponAttachment>();
                    if (weapAttach == null) { SetStatus("No weapon system"); return; }
                    // Find the sidearm mounting point
                    var mountPoint = weapAttach.transform.Find("Sidearm Mounting Point");
                    if (mountPoint == null) mountPoint = weapAttach.transform;
                    // Detach any children (weapons) from the mount point
                    for (int i = mountPoint.childCount - 1; i >= 0; i--)
                    {
                        var child = mountPoint.GetChild(i);
                        child.SetParent(null);
                        LoggerInstance.Msg($"[WEAP] Unequipped: '{child.gameObject.name}'");
                    }
                    SetStatus("Weapon unequipped");
                }
                catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); LoggerInstance.Error($"[WEAP] {ex}"); }
            });

            // --- HOSTILE SKIES ZONES ---
            H("--- HOSTILE SKIES ZONES ---");
            B($"Scan & Dump All Zones ({combatZones.Count} loaded)", () => {
                zonesInitialized = false;
                InitializeZones();
                SetStatus($"Found {combatZones.Count} zones — saved to UW2Trainer_Zones.txt");
            });
            B("Log Current Position", () => {
                try {
                    var ac = GameManager.ControllerAircraft;
                    if (ac == null) { SetStatus("Not in aircraft"); return; }
                    Vector3 p = ac.transform.position;
                    string line = $"MANUAL\t0\t{p.x:F1}\t{p.y:F1}\t{p.z:F1}\t300";
                    string zonesPath = Path.Combine(MelonEnvironment.GameRootDirectory, "Mods", "UW2Trainer_Zones.txt");
                    File.AppendAllText(zonesPath, "\n" + line);
                    LoggerInstance.Msg($"[HOSTILE] Logged position: ({p.x:F1}, {p.y:F1}, {p.z:F1})");
                    SetStatus($"Logged: ({p.x:F0}, {p.y:F0}, {p.z:F0})");
                } catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); }
            });
            B($"Combat Stats: {totalKills} kills, {islandsDiscovered} islands", () => {
                LoggerInstance.Msg($"[HOSTILE] === COMBAT STATS ===");
                LoggerInstance.Msg($"  Total Kills: {totalKills} | Session: {sessionKills} | Best Streak: {bestStreak}");
                LoggerInstance.Msg($"  Islands: {islandsDiscovered}/9 | Threat: {threatLevelUnlocked}");
                LoggerInstance.Msg($"  Unlocks: Grenades={grenadesUnlocked} Darts={dartsUnlocked} RapidFire={rapidFireUnlocked}");
                SetStatus("Stats dumped to log");
            });

            // --- ROTOR-SPECIFIC (helicopter) ---
            H("--- HELICOPTER ROTOR MODS ---");
            try
            {
                var ac = GameManager.ControllerAircraft;
                if (ac != null)
                {
                    var engines = ac.m_aircraftEngines;
                    bool hasRotor = false;
                    if (engines != null)
                    {
                        for (int i = 0; i < engines.Count; i++)
                        {
                            var rotor = engines[i]?.TryCast<EngineRotor>();
                            if (rotor != null)
                            {
                                hasRotor = true;
                                H($"  Rotor{i}: RPM={rotor.m_rotorRPM:F0}/{rotor.m_maxRotorRPMForTorque:F0} brake={rotor.m_brakeTorque:F1}");
                                int idx = i; // capture for lambda
                                B($"2x Rotor Force (E{i})", () => {
                                    var r = ac.m_aircraftEngines[idx]?.TryCast<EngineRotor>();
                                    if (r != null) { r.m_forceAtMaxRPM *= 2f; r.m_maxRotorRPMForTorque *= 1.5f; SetStatus($"Rotor{idx} boosted"); }
                                });
                                B($"Zero Brake Torque (E{i})", () => {
                                    var r = ac.m_aircraftEngines[idx]?.TryCast<EngineRotor>();
                                    if (r != null) { r.m_brakeTorque = 0f; SetStatus($"Rotor{idx} brake=0"); }
                                });
                            }
                        }
                    }
                    if (!hasRotor) H("  Not a helicopter (no EngineRotor found)");

                    // TailRotor
                    try
                    {
                        var tailRotor = ac.gameObject.GetComponentInChildren<TailRotor>();
                        if (tailRotor != null)
                        {
                            H($"  TailRotor: force={tailRotor.m_currentForce:F1} max={tailRotor.m_maxRotorForce:F1} gyro={tailRotor.m_gyroGain:F2}");
                            B("2x Tail Rotor Force", () => {
                                var tr = ac.gameObject.GetComponentInChildren<TailRotor>();
                                if (tr != null) { tr.m_maxRotorForce *= 2f; SetStatus($"Tail max -> {tr.m_maxRotorForce:F0}"); }
                            });
                            B("2x Gyro Gain", () => {
                                var tr = ac.gameObject.GetComponentInChildren<TailRotor>();
                                if (tr != null) { tr.m_gyroGain *= 2f; SetStatus($"Gyro -> {tr.m_gyroGain:F2}"); }
                            });
                        }
                    }
                    catch { }

                    // RotorHub
                    try
                    {
                        var hub = ac.gameObject.GetComponentInChildren<RotorHub>();
                        if (hub != null)
                        {
                            H($"  RotorHub: collective={hub.m_collectiveMax:F1} cyclic={hub.m_cyclicMax:F1}");
                            B("2x Collective Max", () => {
                                var h = ac.gameObject.GetComponentInChildren<RotorHub>();
                                if (h != null) { h.m_collectiveMax *= 2f; SetStatus($"Collective -> {h.m_collectiveMax:F1}"); }
                            });
                            B("2x Cyclic Max", () => {
                                var h = ac.gameObject.GetComponentInChildren<RotorHub>();
                                if (h != null) { h.m_cyclicMax *= 2f; SetStatus($"Cyclic -> {h.m_cyclicMax:F1}"); }
                            });
                        }
                    }
                    catch { }
                }
                else H("  Not in aircraft");
            }
            catch (Exception ex) { H($"  Error: {ex.Message}"); }

            // --- ANIMATIONCURVE OVERRIDE ---
            H("--- SPEED LIMITER BYPASS ---");
            B("Flatten Thrust Curve (removes speed dropoff)", () => {
                try
                {
                    var ac = GameManager.ControllerAircraft;
                    if (ac == null) { SetStatus("Not in aircraft"); return; }
                    var engines = ac.m_aircraftEngines;
                    int count = 0;
                    if (engines != null)
                    {
                        for (int i = 0; i < engines.Count; i++)
                        {
                            var eng = engines[i]?.TryCast<AircraftEngine>();
                            if (eng == null) continue;
                            // Replace the curve with a flat 1.0 — full thrust at all speeds
                            var flatCurve = new AnimationCurve();
                            flatCurve.AddKey(0f, 1f);
                            flatCurve.AddKey(500f, 1f);
                            eng.ForceAppliedVSAirspeedKTS = flatCurve;
                            count++;
                            LoggerInstance.Msg($"[EXP] Flattened thrust curve on engine {i}");
                        }
                    }
                    SetStatus($"Flattened {count} engine curves — full thrust at any speed");
                }
                catch (Exception ex) { SetStatus($"Failed: {ex.Message}"); LoggerInstance.Error($"[EXP] {ex}"); }
            });
        }

        private void ApplyCustomPhysics(float power, float lift, float stall, float drag, float aeroEffect, float maxAeroSpeed)
        {
            if (aircraftConfigs.Count == 0) { SetStatus("No configs loaded yet"); return; }
            // Apply to whichever config the current plane uses (apply to all to be safe)
            foreach (var c in aircraftConfigs)
            {
                try
                {
                    c.MaxEnginePower = power;
                    c.Lift = lift;
                    c.StallSpeed = stall;
                    c.DragIncreaseFactor = drag;
                    c.AerodynamicEffect = aeroEffect;
                    c.MaxAerodynamicEffectSpeed = maxAeroSpeed;
                }
                catch { }
            }
            SetStatus($"Physics: P={power} L={lift} S={stall} D={drag}");
        }

        private void SaveModState()
        {
            try
            {
                var lines = new List<string>();
                lines.Add($"fuel={unlimitedFuel}");
                lines.Add($"ammo={unlimitedAmmo}");
                lines.Add($"god={godMode}");
                lines.Add($"allVehicles={allVehiclesUnlocked}");
                File.WriteAllLines(modSettingsPath, lines);
            }
            catch { }
        }

        private void LoadModState()
        {
            try
            {
                if (!File.Exists(modSettingsPath)) return;
                foreach (var line in File.ReadAllLines(modSettingsPath))
                {
                    if (line.StartsWith("fuel=")) unlimitedFuel = line.Contains("True");
                    if (line.StartsWith("ammo=")) unlimitedAmmo = line.Contains("True");
                    if (line.StartsWith("god=")) godMode = line.Contains("True");
                    if (line.StartsWith("allVehicles=")) allVehiclesUnlocked = line.Contains("True");
                }
            }
            catch { }
        }

        // ================================================================
        // HOSTILE SKIES — Save/Load/Zones/Loop
        // ================================================================
        private void SaveCombatState()
        {
            try
            {
                var lines = new List<string>();
                lines.Add($"totalKills={totalKills}");
                lines.Add($"bestStreak={bestStreak}");
                lines.Add($"threatLevelUnlocked={threatLevelUnlocked}");
                lines.Add($"grenadesUnlocked={grenadesUnlocked}");
                lines.Add($"dartsUnlocked={dartsUnlocked}");
                lines.Add($"rapidFireUnlocked={rapidFireUnlocked}");
                string islands = "";
                for (int i = 0; i < islandDiscoveryMap.Length; i++) { if (islandDiscoveryMap[i]) islands += i + ","; }
                lines.Add($"islandsDiscovered={islands}");
                if (!string.IsNullOrEmpty(savedCombatSceneName))
                {
                    lines.Add($"combatSceneName={savedCombatSceneName}");
                    lines.Add($"combatSceneIndex={savedCombatSceneIndex}");
                }
                File.WriteAllLines(combatSavePath, lines);
                LoggerInstance.Msg($"[HOSTILE] Saved: kills={totalKills} streak={bestStreak} threat={threatLevelUnlocked}");
            }
            catch (Exception ex) { LoggerInstance.Msg($"[HOSTILE] Save failed: {ex.Message}"); }
        }

        private void LoadCombatState()
        {
            try
            {
                combatSavePath = Path.Combine(MelonEnvironment.GameRootDirectory, "Mods", "UW2Trainer_Combat.txt");
                if (!File.Exists(combatSavePath)) return;
                foreach (var line in File.ReadAllLines(combatSavePath))
                {
                    if (line.StartsWith("totalKills=")) int.TryParse(line.Substring(11), out totalKills);
                    if (line.StartsWith("bestStreak=")) int.TryParse(line.Substring(11), out bestStreak);
                    if (line.StartsWith("threatLevelUnlocked=")) int.TryParse(line.Substring(20), out threatLevelUnlocked);
                    if (line.StartsWith("grenadesUnlocked=")) grenadesUnlocked = line.Contains("True");
                    if (line.StartsWith("dartsUnlocked=")) dartsUnlocked = line.Contains("True");
                    if (line.StartsWith("rapidFireUnlocked=")) rapidFireUnlocked = line.Contains("True");
                    if (line.StartsWith("combatSceneName=")) savedCombatSceneName = line.Substring(16);
                    if (line.StartsWith("combatSceneIndex=")) int.TryParse(line.Substring(17), out savedCombatSceneIndex);
                    if (line.StartsWith("islandsDiscovered="))
                    {
                        string vals = line.Substring(18);
                        foreach (var v in vals.Split(','))
                        {
                            int idx;
                            if (int.TryParse(v, out idx) && idx >= 0 && idx < islandDiscoveryMap.Length)
                                islandDiscoveryMap[idx] = true;
                        }
                        islandsDiscovered = 0;
                        for (int i = 0; i < islandDiscoveryMap.Length; i++) if (islandDiscoveryMap[i]) islandsDiscovered++;
                    }
                }
                LoggerInstance.Msg($"[HOSTILE] Loaded: kills={totalKills} streak={bestStreak} threat={threatLevelUnlocked} islands={islandsDiscovered}");
            }
            catch { }
        }

        private void InitializeZones()
        {
            if (zonesInitialized) return;
            combatZones.Clear();

            // Auto-discover runway positions from the game's existing RunwayTrigger components
            try
            {
                var runways = Resources.FindObjectsOfTypeAll<Il2Cpp.RunwayTrigger>();
                LoggerInstance.Msg($"[HOSTILE] Found {runways?.Length ?? 0} RunwayTriggers");
                for (int i = 0; i < (runways?.Length ?? 0); i++)
                {
                    var rt = runways[i];
                    if (rt == null) continue;
                    Vector3 pos = rt.transform.position;
                    string name = rt.gameObject.name;
                    // Skip duplicate positions (within 50m)
                    bool dup = false;
                    foreach (var z in combatZones)
                        if (Vector3.Distance(z.Position, pos) < 50f) { dup = true; break; }
                    if (dup) continue;

                    combatZones.Add(new CombatZone {
                        Position = pos,
                        Radius = 300f,
                        Name = name,
                        ZoneType = 0, // Airfield
                        Discovered = false,
                        CooldownRemaining = 0f
                    });
                    LoggerInstance.Msg($"[HOSTILE]   Zone: '{name}' at ({pos.x:F0}, {pos.y:F0}, {pos.z:F0})");
                }
            }
            catch (Exception ex) { LoggerInstance.Msg($"[HOSTILE] Runway scan: {ex.Message}"); }

            // Also try to get island positions from FreeFlightWaypointHelper
            try
            {
                var helpers = Resources.FindObjectsOfTypeAll<Il2Cpp.FreeFlightWaypointHelper>();
                LoggerInstance.Msg($"[HOSTILE] FreeFlightWaypointHelper: {helpers?.Length ?? 0}");
                for (int h = 0; h < (helpers?.Length ?? 0); h++)
                {
                    var helper = helpers[h];
                    if (helper == null) continue;
                    var locationData = helper.m_islandLocationData;
                    if (locationData == null) continue;
                    for (int i = 0; i < locationData.Length; i++)
                    {
                        var data = locationData[i];
                        if (data == null) continue;
                        try
                        {
                            var islandTransform = data.IslandLocation;
                            if (islandTransform == null) continue;
                            Vector3 pos = islandTransform.position;
                            string islandName = "Island_" + i;
                            try { islandName = data.IslandType.ToString(); } catch { }

                            // Add as a flyover zone (larger radius)
                            bool dup = false;
                            foreach (var z in combatZones)
                                if (Vector3.Distance(z.Position, pos) < 100f) { dup = true; break; }
                            if (!dup)
                            {
                                combatZones.Add(new CombatZone {
                                    Position = pos,
                                    Radius = 500f,
                                    Name = islandName,
                                    ZoneType = 1, // Flyover (island center)
                                    Discovered = false,
                                    CooldownRemaining = 0f
                                });
                                LoggerInstance.Msg($"[HOSTILE]   Island zone: '{islandName}' at ({pos.x:F0}, {pos.y:F0}, {pos.z:F0})");
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex) { LoggerInstance.Msg($"[HOSTILE] Island scan: {ex.Message}"); }

            zonesInitialized = combatZones.Count > 0;
            LoggerInstance.Msg($"[HOSTILE] Zones initialized: {combatZones.Count} total");

            // Dump zones to file for reference
            try
            {
                string zonesPath = Path.Combine(MelonEnvironment.GameRootDirectory, "Mods", "UW2Trainer_Zones.txt");
                var zoneLines = new List<string>();
                zoneLines.Add("# UW2 Hostile Skies — Zone Coordinates");
                zoneLines.Add($"# Generated {DateTime.Now}");
                foreach (var z in combatZones)
                    zoneLines.Add($"{z.Name}\t{z.ZoneType}\t{z.Position.x:F1}\t{z.Position.y:F1}\t{z.Position.z:F1}\t{z.Radius:F0}");
                File.WriteAllLines(zonesPath, zoneLines);
            }
            catch { }
        }

        private void CheckUnlocks()
        {
            bool changed = false;

            // Kill-based unlocks
            if (totalKills >= 5 && !grenadesUnlocked) { grenadesUnlocked = true; SetStatus("UNLOCKED: Grenades!"); changed = true; }
            if (totalKills >= 15 && !dartsUnlocked) { dartsUnlocked = true; SetStatus("UNLOCKED: Darts!"); changed = true; }
            if (totalKills >= 25 && threatLevelUnlocked < 4) { threatLevelUnlocked = 4; SetStatus("UNLOCKED: Threat Level 4!"); changed = true; }
            if (totalKills >= 50 && !rapidFireUnlocked) { rapidFireUnlocked = true; SetStatus("UNLOCKED: Rapid Fire!"); changed = true; }

            // Island discovery unlocks
            if (islandsDiscovered >= 3 && threatLevelUnlocked < 3) { threatLevelUnlocked = 3; SetStatus("UNLOCKED: Threat Level 3!"); changed = true; }
            if (islandsDiscovered >= 5 && threatLevelUnlocked < 5) { threatLevelUnlocked = 5; SetStatus("UNLOCKED: Threat Level 5!"); changed = true; }

            if (changed) SaveCombatState();
        }

        private void UpdateHostileSkies()
        {
            if (!hostileSkiesActive || !initialized) return;

            var ac = GameManager.ControllerAircraft;
            if (ac == null) return;
            Vector3 playerPos = ac.transform.position;

            // Initialize zones on first run
            if (!zonesInitialized) InitializeZones();
            if (!combatPrefabsScanned) ScanCombatPrefabs();

            // --- Kill Detection (every 1 second) ---
            killCheckTimer += Time.deltaTime;
            if (killCheckTimer >= 1f)
            {
                killCheckTimer = 0f;
                // Clean destroyed combatants
                int before = spawnedCombatants.Count;
                for (int i = spawnedCombatants.Count - 1; i >= 0; i--)
                {
                    try { if (spawnedCombatants[i] == null || !spawnedCombatants[i].activeSelf) spawnedCombatants.RemoveAt(i); }
                    catch { spawnedCombatants.RemoveAt(i); }
                }
                int killed = before - spawnedCombatants.Count;
                if (killed > 0 && before > previousCombatantCount - killed) // only count actual kills, not cleanup
                {
                    totalKills += killed;
                    sessionKills += killed;
                    killStreak += killed;
                    if (killStreak > bestStreak) bestStreak = killStreak;
                    LoggerInstance.Msg($"[HOSTILE] +{killed} kills! Total={totalKills} Streak={killStreak} Best={bestStreak}");
                    CheckUnlocks();
                }
                combatActiveFighters = spawnedCombatants.Count;
                previousCombatantCount = combatActiveFighters;
            }

            // --- Zone Proximity Check (every 1 second) ---
            zoneCheckTimer += Time.deltaTime;
            if (zoneCheckTimer >= 1f)
            {
                zoneCheckTimer = 0f;
                for (int i = 0; i < combatZones.Count; i++)
                {
                    var zone = combatZones[i];

                    // Tick cooldown
                    if (zone.CooldownRemaining > 0f)
                    {
                        zone.CooldownRemaining -= 1f;
                        combatZones[i] = zone;
                        continue;
                    }

                    float dist = Vector3.Distance(playerPos, zone.Position);

                    // Zone type filtering by threat level
                    bool zoneActive = false;
                    if (zone.ZoneType == 0) zoneActive = true; // Airfields always active
                    if (zone.ZoneType == 1 && threatLevel >= 2) zoneActive = true; // Flyover at threat 2+
                    if (zone.ZoneType == 2 && threatLevel >= 3) zoneActive = true; // Naval at threat 3+
                    if (zone.ZoneType == 3 && threatLevel >= 3) zoneActive = true; // Mountain at threat 3+

                    if (!zoneActive) continue;

                    if (dist < zone.Radius)
                    {
                        // Island discovery
                        if (!zone.Discovered)
                        {
                            zone.Discovered = true;
                            combatZones[i] = zone;
                            // Try to map to island index
                            for (int isl = 0; isl < 9; isl++)
                            {
                                if (zone.Name.Contains(isl.ToString()) && !islandDiscoveryMap[isl])
                                {
                                    islandDiscoveryMap[isl] = true;
                                    islandsDiscovered++;
                                    SetStatus($"Discovered: {zone.Name}!");
                                    LoggerInstance.Msg($"[HOSTILE] Island discovered: {zone.Name} (total: {islandsDiscovered})");
                                    CheckUnlocks();
                                    break;
                                }
                            }
                        }

                        // Spawn encounter based on threat level
                        int spawnCount = 0;
                        switch (threatLevel)
                        {
                            case 1: spawnCount = 1; break;
                            case 2: spawnCount = 2; break;
                            case 3: spawnCount = UnityEngine.Random.Range(3, 5); break;
                            case 4: spawnCount = UnityEngine.Random.Range(4, 7); break;
                            case 5: spawnCount = UnityEngine.Random.Range(6, 10); break;
                        }

                        // Don't exceed max active
                        int maxActive = threatLevel * 4;
                        spawnCount = Math.Min(spawnCount, maxActive - combatActiveFighters);

                        if (spawnCount > 0 && cachedFighterPrefabs.Count > 0)
                        {
                            SpawnWave(spawnCount);
                            killStreak = 0; // reset streak on new encounter (keeps tension)
                            LoggerInstance.Msg($"[HOSTILE] Zone '{zone.Name}' triggered! Spawned {spawnCount} at threat {threatLevel}");
                        }

                        // Set cooldown based on threat level
                        float[] cooldowns = { 60f, 40f, 25f, 15f, 8f };
                        zone.CooldownRemaining = cooldowns[Math.Min(threatLevel - 1, 4)];
                        combatZones[i] = zone;
                    }
                }

                // --- Ambient Spawns at threat 4+ ---
                if (threatLevel >= 4)
                {
                    ambientSpawnTimer += 1f;
                    float ambientInterval = threatLevel >= 5 ? 60f : 120f;
                    if (ambientSpawnTimer >= ambientInterval)
                    {
                        ambientSpawnTimer = 0f;
                        int maxActive = threatLevel * 4;
                        if (combatActiveFighters < maxActive && cachedFighterPrefabs.Count > 0)
                        {
                            int ambientCount = UnityEngine.Random.Range(1, 3);
                            SpawnWave(ambientCount);
                            LoggerInstance.Msg($"[HOSTILE] Ambient spawn: {ambientCount} fighters");
                        }
                    }
                }

                // --- Despawn far-away combatants (>2km) ---
                for (int i = spawnedCombatants.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        if (spawnedCombatants[i] != null && Vector3.Distance(playerPos, spawnedCombatants[i].transform.position) > 2000f)
                        {
                            UnityEngine.Object.Destroy(spawnedCombatants[i]);
                            spawnedCombatants.RemoveAt(i);
                            LoggerInstance.Msg("[HOSTILE] Despawned distant combatant");
                        }
                    }
                    catch { spawnedCombatants.RemoveAt(i); }
                }
            }
        }

        private void TryModelSwap(string npcName)
        {
            try
            {
                var player = GameManager.ControllerAircraft;
                if (player == null) { SetStatus("No player aircraft active"); return; }

                // Find the NPC by name
                GameObject npcGO = null;
                var allGO = Resources.FindObjectsOfTypeAll<GameObject>();
                for (int i = 0; i < allGO.Count; i++)
                {
                    if (allGO[i] != null && allGO[i].name != null && allGO[i].name.Contains(npcName))
                    {
                        npcGO = allGO[i];
                        break;
                    }
                }
                if (npcGO == null) { SetStatus($"'{npcName}' not found — fly near NPCs first"); return; }

                // Get meshes from NPC
                var npcRenderers = npcGO.GetComponentsInChildren<MeshRenderer>();
                var npcFilters = npcGO.GetComponentsInChildren<MeshFilter>();
                if (npcRenderers.Count == 0) { SetStatus("NPC has no renderers"); return; }

                // Get player's main visual mesh (skip colliders and UI elements)
                var playerRenderers = player.gameObject.GetComponentsInChildren<MeshRenderer>();

                // Approach: disable player visual meshes, instantiate NPC visual as child
                // First disable player renderers that aren't cockpit/UI
                int disabled = 0;
                for (int i = 0; i < playerRenderers.Count; i++)
                {
                    string rName = playerRenderers[i].gameObject.name.ToLower();
                    // Keep cockpit internals, disable external body
                    if (!rName.Contains("control") && !rName.Contains("cockpit") && !rName.Contains("glass") &&
                        !rName.Contains("helmet") && !rName.Contains("grabbable") && !rName.Contains("player") &&
                        !rName.Contains("volume") && !rName.Contains("sphere") && !rName.Contains("capsule"))
                    {
                        playerRenderers[i].enabled = false;
                        disabled++;
                    }
                }

                // Clone NPC visual and parent to player
                var clone = GameObject.Instantiate(npcGO, player.transform);
                clone.name = "SwappedModel";
                clone.transform.localPosition = Vector3.zero;
                clone.transform.localRotation = Quaternion.identity;

                // Strip ALL components except renderers and transforms — pure visual shell
                // Destroy rigidbodies
                var rbs = clone.GetComponentsInChildren<Rigidbody>();
                for (int i = 0; i < rbs.Count; i++) { try { GameObject.Destroy(rbs[i]); } catch { } }

                // Destroy colliders
                var cols = clone.GetComponentsInChildren<Collider>();
                for (int i = 0; i < cols.Count; i++) { try { GameObject.Destroy(cols[i]); } catch { } }

                // Disable all scripts
                var scripts = clone.GetComponentsInChildren<MonoBehaviour>();
                for (int i = 0; i < scripts.Count; i++) { try { scripts[i].enabled = false; } catch { } }

                // Destroy everything else that isn't a renderer, filter, or transform
                var allComps = clone.GetComponentsInChildren<Component>();
                for (int i = 0; i < allComps.Count; i++)
                {
                    var comp = allComps[i];
                    if (comp == null) continue;
                    string tn = comp.GetType().Name;
                    if (tn == "Transform" || tn == "MeshRenderer" || tn == "MeshFilter" || tn == "SkinnedMeshRenderer") continue;
                    try { GameObject.Destroy(comp); } catch { }
                }

                LoggerInstance.Msg($"[SWAP] Swapped player model with '{npcName}' — disabled {disabled} player renderers, cloned NPC");
                SetStatus($"Model swapped to {npcName}!");
            }
            catch (Exception ex) { SetStatus($"Swap failed: {ex.Message}"); }
        }

        // ================================================================
        private void SetStatus(string msg)
        {
            statusMessage = msg;
            statusTimer = 4f;
            LoggerInstance.Msg($"[UW2_Trainer] {msg}");
        }
    }
}
