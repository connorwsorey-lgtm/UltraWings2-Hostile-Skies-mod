/*
 * UW2 Diagnostics Mod - First Launch Logger
 * ==========================================
 * Lightweight mod that logs everything during first launch.
 * Catches errors, dumps discovered types, and verifies IL2CPP unhollowing.
 *
 * This mod has NO dependencies on game assemblies - it only uses
 * MelonLoader and UnityEngine base types, so it works even if
 * unhollowing fails or game assemblies aren't generated yet.
 *
 * Build: Reference only MelonLoader.dll and Il2Cppmscorlib.dll
 * Place built DLL in: <GameDir>/Mods/
 */

using MelonLoader;
using MelonLoader.Utils;
using System;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Collections.Generic;

[assembly: MelonInfo(typeof(UW2Diagnostics.DiagnosticsMod), "UW2 Diagnostics", "1.0.0", "UW2Modder")]
[assembly: MelonGame("Bit Planet Games, LLC", "Ultrawings 2")]

namespace UW2Diagnostics
{
    public class DiagnosticsMod : MelonMod
    {
        private string logPath;
        private StreamWriter logWriter;
        private bool hasLoggedTypes = false;
        private float timer = 0f;
        private int frameCount = 0;
        private bool crashGuardActive = true;

        public override void OnInitializeMelon()
        {
            // Create diagnostic log file
            string logDir = Path.Combine(MelonEnvironment.GameRootDirectory, "MelonLoader", "Logs");
            Directory.CreateDirectory(logDir);
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            logPath = Path.Combine(logDir, $"UW2_Diagnostics_{timestamp}.log");

            logWriter = new StreamWriter(logPath, false);
            logWriter.AutoFlush = true;

            Log("========================================");
            Log("UW2 DIAGNOSTICS MOD - FIRST LAUNCH LOG");
            Log($"Timestamp: {DateTime.Now}");
            Log($"MelonLoader: {MelonLoader.Properties.BuildInfo.Description}");
            Log($"Game Dir: {MelonEnvironment.GameRootDirectory}");
            Log("========================================");

            // Log system info
            Log("");
            Log("--- SYSTEM INFO ---");
            Log($"OS: {Environment.OSVersion}");
            Log($".NET Runtime: {Environment.Version}");
            Log($"64-bit Process: {Environment.Is64BitProcess}");
            Log($"Working Set: {Environment.WorkingSet / 1024 / 1024} MB");
            Log($"Processors: {Environment.ProcessorCount}");

            // Register unhandled exception handler
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            // Hook Unity's log callback to catch errors and exceptions
            try
            {
                UnityEngine.Application.add_logMessageReceived(
                    new System.Action<string, string, UnityEngine.LogType>(OnUnityLog));
                Log("[OK] Unity log callback hooked");
            }
            catch (Exception ex)
            {
                Log($"[WARN] Could not hook Unity log callback: {ex.Message}");
            }

            // Log all loaded assemblies at init time
            Log("");
            Log("--- ASSEMBLIES AT INIT ---");
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.GetName().Name))
            {
                try
                {
                    string loc = asm.IsDynamic ? "(dynamic)" : asm.Location;
                    Log($"  {asm.GetName().Name} v{asm.GetName().Version} [{loc}]");
                }
                catch { Log($"  {asm.GetName().Name} v{asm.GetName().Version} [(location unavailable)]"); }
            }

            // Check for key MelonLoader directories
            Log("");
            Log("--- DIRECTORY CHECK ---");
            CheckDir("MelonLoader", Path.Combine(MelonEnvironment.GameRootDirectory, "MelonLoader"));
            CheckDir("MelonLoader/Il2CppAssemblies", Path.Combine(MelonEnvironment.GameRootDirectory, "MelonLoader", "Il2CppAssemblies"));
            CheckDir("MelonLoader/Managed", Path.Combine(MelonEnvironment.GameRootDirectory, "MelonLoader", "Managed"));
            CheckDir("Mods", Path.Combine(MelonEnvironment.GameRootDirectory, "Mods"));
            CheckDir("Plugins", Path.Combine(MelonEnvironment.GameRootDirectory, "Plugins"));
            CheckDir("UserData", Path.Combine(MelonEnvironment.GameRootDirectory, "UserData"));

            // List unhollowed assemblies if they exist
            string il2cppDir = Path.Combine(MelonEnvironment.GameRootDirectory, "MelonLoader", "Il2CppAssemblies");
            if (Directory.Exists(il2cppDir))
            {
                var dlls = Directory.GetFiles(il2cppDir, "*.dll");
                Log($"\n--- IL2CPP ASSEMBLIES ({dlls.Length} found) ---");
                foreach (var dll in dlls.OrderBy(d => d))
                {
                    var fi = new FileInfo(dll);
                    Log($"  {fi.Name} ({fi.Length / 1024} KB)");
                }
            }
            else
            {
                Log("\n[WARN] Il2CppAssemblies directory does not exist yet!");
                Log("[INFO] This is normal for first launch - MelonLoader will generate them.");
            }

            LoggerInstance.Msg("Diagnostics mod loaded - logging to: " + logPath);
            LoggerInstance.Msg("Console will show key events. Full log at path above.");
        }

        public override void OnUpdate()
        {
            frameCount++;
            timer += UnityEngine.Time.deltaTime;

            // Log milestone frames
            if (frameCount == 1)
            {
                Log("\n--- FIRST FRAME ---");
                Log($"Time since start: {timer:F2}s");
                LogAssemblyUpdate();
            }
            else if (frameCount == 10)
            {
                Log($"\n--- FRAME 10 (t={timer:F2}s) ---");
            }
            else if (frameCount == 60)
            {
                Log($"\n--- FRAME 60 (t={timer:F2}s) ---");
                Log($"Working Set: {Environment.WorkingSet / 1024 / 1024} MB");
            }
            else if (frameCount == 300)
            {
                Log($"\n--- FRAME 300 (t={timer:F2}s) ---");
                Log("Game appears stable after 300 frames.");
                crashGuardActive = false;
            }

            // At 5 seconds, dump all game types (once)
            if (timer > 5f && !hasLoggedTypes)
            {
                hasLoggedTypes = true;
                LogGameTypes();
                LogGameObjects();
            }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            Log($"\n--- SCENE LOADED: [{buildIndex}] {sceneName} (frame {frameCount}, t={timer:F2}s) ---");
            LoggerInstance.Msg($"Scene loaded: [{buildIndex}] {sceneName}");
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            Log($"  Scene initialized: [{buildIndex}] {sceneName}");
        }

        public override void OnApplicationQuit()
        {
            Log($"\n--- APPLICATION QUIT (frame {frameCount}, t={timer:F2}s) ---");
            Log("Clean shutdown.");
            logWriter?.Close();
        }

        // ================================================================
        // CRASH / ERROR HANDLING
        // ================================================================

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            WriteCrashReport("UNHANDLED_EXCEPTION", e.ExceptionObject as Exception, e.ExceptionObject?.ToString());
        }

        private void OnUnityLog(string condition, string stackTrace, UnityEngine.LogType logType)
        {
            if (logType == UnityEngine.LogType.Exception || logType == UnityEngine.LogType.Error)
            {
                Log($"\n[UNITY {logType}] (frame {frameCount}, t={timer:F2}s)");
                Log($"  {condition}");
                if (!string.IsNullOrEmpty(stackTrace))
                    Log($"  Stack: {stackTrace}");

                // Write a crash report for exceptions
                if (logType == UnityEngine.LogType.Exception)
                {
                    WriteCrashReport($"UNITY_EXCEPTION", null, $"{condition}\n{stackTrace}");
                }
            }
        }

        private void WriteCrashReport(string crashType, Exception ex, string rawMessage = null)
        {
            try
            {
                // Write to diagnostics log
                Log($"\n!!! {crashType} !!!");
                Log($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
                Log($"Frame: {frameCount}, Uptime: {timer:F2}s");
                Log($"Working Set: {Environment.WorkingSet / 1024 / 1024} MB");

                if (ex != null)
                {
                    Log($"Exception Type: {ex.GetType().FullName}");
                    Log($"Message: {ex.Message}");
                    Log($"Source: {ex.Source}");
                    Log($"Stack Trace:\n{ex.StackTrace}");
                    if (ex.InnerException != null)
                    {
                        Log($"Inner Exception: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
                        Log($"Inner Stack:\n{ex.InnerException.StackTrace}");
                    }
                }
                else if (rawMessage != null)
                {
                    Log($"Raw: {rawMessage}");
                }
                logWriter?.Flush();

                // Also write a standalone crash report file
                string crashDir = Path.Combine(MelonEnvironment.GameRootDirectory, "MelonLoader", "Logs");
                string crashFile = Path.Combine(crashDir, $"UW2_CRASH_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
                var crashLines = new List<string>
                {
                    "=== UW2 CRASH REPORT ===",
                    $"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}",
                    $"Crash Type: {crashType}",
                    $"Frame: {frameCount}",
                    $"Uptime: {timer:F2}s",
                    $"Memory: {Environment.WorkingSet / 1024 / 1024} MB",
                    $"OS: {Environment.OSVersion}",
                    $".NET: {Environment.Version}",
                    ""
                };
                if (ex != null)
                {
                    crashLines.Add($"Exception: {ex.GetType().FullName}");
                    crashLines.Add($"Message: {ex.Message}");
                    crashLines.Add($"Source: {ex.Source}");
                    crashLines.Add($"Stack Trace:");
                    crashLines.Add(ex.StackTrace ?? "(no stack trace)");
                    var inner = ex.InnerException;
                    int depth = 0;
                    while (inner != null && depth < 5)
                    {
                        crashLines.Add($"");
                        crashLines.Add($"Inner Exception [{depth}]: {inner.GetType().FullName}");
                        crashLines.Add($"Message: {inner.Message}");
                        crashLines.Add(inner.StackTrace ?? "(no stack trace)");
                        inner = inner.InnerException;
                        depth++;
                    }
                }
                else
                {
                    crashLines.Add(rawMessage ?? "(no details)");
                }
                crashLines.Add("");
                crashLines.Add("=== LOADED ASSEMBLIES AT CRASH ===");
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.GetName().Name))
                {
                    crashLines.Add($"  {asm.GetName().Name} v{asm.GetName().Version}");
                }
                crashLines.Add("");
                crashLines.Add("=== ACTIVE SCENES ===");
                try
                {
                    int sceneCount = UnityEngine.SceneManagement.SceneManager.sceneCount;
                    for (int i = 0; i < sceneCount; i++)
                    {
                        var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                        crashLines.Add($"  [{scene.buildIndex}] {scene.name} (loaded={scene.isLoaded})");
                    }
                }
                catch { crashLines.Add("  (could not enumerate scenes)"); }

                File.WriteAllLines(crashFile, crashLines);
                LoggerInstance.Error($"CRASH REPORT: {crashFile}");
            }
            catch (Exception writeEx)
            {
                LoggerInstance.Error($"Failed to write crash report: {writeEx.Message}");
            }
        }

        // ================================================================
        // TYPE DISCOVERY
        // ================================================================

        private void LogAssemblyUpdate()
        {
            Log("Assemblies now loaded:");
            var assemblies = AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.GetName().Name);
            foreach (var asm in assemblies)
            {
                if (asm.GetName().Name.StartsWith("Il2Cpp") || asm.GetName().Name.Contains("Assembly-CSharp"))
                    Log($"  [GAME] {asm.GetName().Name} v{asm.GetName().Version}");
            }
        }

        private void LogGameTypes()
        {
            Log($"\n--- GAME TYPE SCAN (t={timer:F2}s) ---");

            // Key types we're looking for (from IL2CPP dump analysis)
            string[] targetTypes = {
                "PlayerDataDO", "PlayerDataModel", "PlayerDataHandler",
                "CheatManager", "BaseCheatManager", "MoneyCheatProcessor",
                "ProgressUnlockerCheat", "CheatConfigDO",
                "AircraftControllerConfigDO", "ControllerAircraft", "ControllerVehicle",
                "VehicleReferenceConfigDO", "VehicleType",
                "FiringMechanismConfigDO", "Hardpoint", "WeaponAttachment", "WeaponBase",
                "EFIMissionMediator", "FreeFlightMissionController",
                "CryptoConfigDO", "Crypto", "BaseSerializationConfigHandler",
                "GameManager", "VRManager", "FreeflightLifeManager",
                "AiAircraftController", "AircraftPhysicsController",
                "LevelType", "VehicleType", "IslandType",
            };

            int totalTypes = 0;
            int gameTypes = 0;
            var foundTargets = new List<string>();
            var missingTargets = new List<string>(targetTypes);

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var types = asm.GetTypes();
                    totalTypes += types.Length;

                    foreach (var type in types)
                    {
                        // Check if it's one of our targets
                        if (targetTypes.Contains(type.Name))
                        {
                            gameTypes++;
                            string info = $"  FOUND: {type.FullName} (in {asm.GetName().Name})";

                            // Log fields for key types
                            if (type.Name == "PlayerDataDO" || type.Name == "AircraftControllerConfigDO" ||
                                type.Name == "CryptoConfigDO" || type.Name == "FiringMechanismConfigDO")
                            {
                                info += "\n    Fields:";
                                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                                {
                                    info += $"\n      {field.FieldType.Name} {field.Name}";
                                }
                            }

                            Log(info);
                            foundTargets.Add(type.Name);
                            missingTargets.Remove(type.Name);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"  [WARN] Could not scan {asm.GetName().Name}: {ex.Message}");
                }
            }

            Log($"\nTotal types scanned: {totalTypes}");
            Log($"Target types found: {foundTargets.Count}/{targetTypes.Length}");

            if (missingTargets.Count > 0)
            {
                Log($"\n[WARN] Missing target types ({missingTargets.Count}):");
                foreach (var t in missingTargets)
                    Log($"  MISSING: {t}");
            }
            else
            {
                Log("\n[OK] All target game types found!");
            }

            LoggerInstance.Msg($"Type scan: {foundTargets.Count}/{targetTypes.Length} target types found. See log for details.");
        }

        private void LogGameObjects()
        {
            Log($"\n--- ACTIVE GAME OBJECTS ---");
            try
            {
                var allObjects = UnityEngine.GameObject.FindObjectsOfType<UnityEngine.MonoBehaviour>();
                if (allObjects != null)
                {
                    var typeCounts = new Dictionary<string, int>();
                    for (int i = 0; i < allObjects.Count; i++)
                    {
                        if (allObjects[i] == null) continue;
                        string typeName = allObjects[i].GetType().Name;
                        typeCounts[typeName] = typeCounts.GetValueOrDefault(typeName, 0) + 1;
                    }

                    Log($"Total active MonoBehaviours: {allObjects.Count}");
                    Log("By type (top 50):");
                    foreach (var kvp in typeCounts.OrderByDescending(x => x.Value).Take(50))
                    {
                        Log($"  {kvp.Value}x {kvp.Key}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[WARN] GameObject scan failed: {ex.Message}");
            }
        }

        // ================================================================
        // HELPERS
        // ================================================================

        private void CheckDir(string name, string path)
        {
            bool exists = Directory.Exists(path);
            int fileCount = exists ? Directory.GetFiles(path).Length : 0;
            Log($"  {name}: {(exists ? "EXISTS" : "MISSING")} ({fileCount} files) [{path}]");
        }

        private void Log(string msg)
        {
            try
            {
                logWriter?.WriteLine(msg);
            }
            catch { }
        }
    }
}
