# MelonLoader Mod Setup for Ultrawings 2

## Step 1: Install MelonLoader

1. Download MelonLoader v0.6.x from: https://github.com/LavaGang/MelonLoader/releases
   - Get `MelonLoader.Installer.exe`
2. Run the installer
3. Click "SELECT" and choose: `C:\Program Files\Oculus\Software\Software\bit-planet-games-llc-ultrawings-2\Ultrawings 2.exe`
4. Make sure **"IL2CPP"** game type is selected (it should auto-detect)
5. Click **INSTALL**
6. Launch the game ONCE and close it - this generates the unhollowed assemblies

## Step 2: Build the Mod

After first launch, you'll have managed DLLs in:
`<GameDir>\MelonLoader\Managed\`

### Option A: Visual Studio / Rider
1. Create a new Class Library (.NET Framework 4.7.2 or .NET 6)
2. Add references to:
   - `<GameDir>\MelonLoader\net6\MelonLoader.dll`
   - `<GameDir>\MelonLoader\Managed\Il2Cppmscorlib.dll`
   - `<GameDir>\MelonLoader\Managed\Il2CppSystem.dll`
   - `<GameDir>\MelonLoader\Managed\Il2CppInterop.Runtime.dll`
   - `<GameDir>\MelonLoader\Managed\Il2CppUnityEngine.dll`
   - `<GameDir>\MelonLoader\Managed\Il2CppUnityEngine.CoreModule.dll`
   - `<GameDir>\MelonLoader\Managed\Il2CppUnityEngine.IMGUIModule.dll`
   - `<GameDir>\MelonLoader\Managed\Il2CppUnityEngine.InputLegacyModule.dll`
   - `<GameDir>\MelonLoader\Managed\Il2CppAssembly-CSharp.dll` (THE GAME CODE!)
3. Copy `UW2Mod.cs` into the project
4. Build

### Option B: Command Line
```
csc /target:library /out:UW2Mod.dll ^
    /reference:MelonLoader.dll ^
    /reference:Il2Cppmscorlib.dll ^
    /reference:Il2CppInterop.Runtime.dll ^
    /reference:Il2CppUnityEngine.dll ^
    /reference:Il2CppUnityEngine.CoreModule.dll ^
    /reference:Il2CppAssembly-CSharp.dll ^
    UW2Mod.cs
```

## Step 3: Install the Mod

1. Copy `UW2Mod.dll` to `<GameDir>\Mods\`
2. Launch the game
3. Press **F1** to open the trainer menu

## Hotkeys

| Key | Action |
|-----|--------|
| F1  | Toggle trainer menu |
| F2  | Toggle infinite ammo |
| F3  | Toggle unlimited fuel |
| F4  | Add money |
| F5  | Enable built-in cheat system |

## After Il2CppDumper

Once you have the full Il2CPP dump, you can:
1. See exact class structures in `dump.cs`
2. Find method offsets in `script.json`
3. Use Harmony patches to hook specific methods
4. Call game methods directly via the unhollowed assembly

## Exploring the Built-in Cheat System

The game has these cheat-related classes:
- `GameTools.Cheat.CheatManager` - Main cheat controller
- `CheatProcessor` - Processes cheat activation
- `CheatVerifierHandler` - Validates cheat codes
- `CheatConfigDO` / `CheatConfigListDO` - Cheat configuration data
- `CheatType` - Enum of available cheat types

After MelonLoader generates the managed DLLs, open `Il2CppAssembly-CSharp.dll`
in dnSpy/ILSpy to see the full CheatManager API and available cheat codes!

## Known Game Namespaces

```
GameTools.Cheat          - Cheat system
GameTools.Save           - Save/load system
GameTools.Gameplay       - Core gameplay
Gameplay.Mission         - Mission system
Gameplay.Race            - Racing
Gameplay.DogFight        - Dogfight mode
Gameplay.Defense         - Defense missions
Gameplay.Hunt            - Hunt missions
Gameplay.TargetElimination2 - Target elimination
Weapon.RevolverSystem    - Weapon system
Weapon.TargetSeeking     - Guided weapons
Game.DLC / UW2.DLC       - DLC content
Game.Racing.AirRaces     - Air racing
Vehicle.SpawningSystem   - Vehicle spawning
```
