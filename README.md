# Hostile Skies - Ultrawings 2 Combat Overhaul Mod

A MelonLoader mod for Ultrawings 2 (PC VR) that transforms free flight into a combat sandbox. Spawn enemy fighters, engage in dogfights, tweak aircraft performance, and explore hidden game content.

## Requirements

- **Ultrawings 2** (PC VR, Oculus/Steam)
- **MelonLoader** 0.6.x ([Download](https://melonwiki.xyz/))
- **.NET 6 SDK** (for building from source)

## Installation

### Pre-built
1. Install MelonLoader into your Ultrawings 2 directory
2. Copy `UW2TrainerMod.dll` from `bin/Debug/` into your `Mods/` folder
3. Launch the game

### Building from Source
1. Clone this repo
2. Ensure MelonLoader is installed in your UW2 directory (the `.csproj` references assemblies from the game's `MelonLoader/` folder)
3. Run `dotnet build UW2Mod.csproj`
4. The post-build script auto-copies the DLL to the `Mods/` folder

## Controls

### Menu Access
| Input | Action |
|-------|--------|
| **Spacebar / F1** | Toggle mod menu (pauses game) |
| **Left Thumbstick** | Navigate menu (up/down), adjust sliders (left/right) |
| **Right Thumbstick** | Switch tabs (left/right) |
| **A Button (Right)** | Activate / confirm |
| **B Button (Right)** | Back / close menu |
| **F2** | Toggle vehicle stats HUD |
| **Keyboard arrows** | Navigate (fallback) |
| **Enter** | Activate (fallback) |

### Wrist Panel (In-Flight)
- **Hold Left Menu Button** to open the wrist panel
- 18 quick-access items, scrollable
- Works while flying without pausing

## Features

### Hostile Skies - Combat System
The core feature. Spawn real combat AI enemies during free flight.

**How to unlock:**
1. Play at least one combat mission (Dogfight, Target Elimination, Air Defense, etc.)
2. The mod automatically caches enemy prefabs when a combat mission loads
3. Return to free flight - enemies are now available to spawn

**How to use:**
1. Open mod menu (Spacebar/F1) > **Combat** tab
2. The "HOSTILE SKIES" section shows prefab status (READY / not loaded)
3. Press **"Spawn 1 Enemy Fighter"** or **"Spawn 3 Enemy Fighters"**
4. AI enemies will chase you, perform attack runs, and fire their machineguns

**Enemy types cached from missions:**
- **Messer 01** - Light escort fighter with machinegun
- **Wolf 01** - Heavy fighter with machinegun
- Turrets, destroyers, and flak cannons are also detected

**Notes:**
- Prefabs persist across scene changes within a session
- The saved combat scene name is stored in `Mods/UW2Trainer_Combat.txt` for future use
- Enemies despawn at >2km distance to prevent object buildup

### Cheats Tab
| Feature | Description |
|---------|-------------|
| Unlimited Fuel | Never run out of fuel |
| Unlimited Ammo | Infinite ammunition |
| God Mode | Invulnerable + no crashes |
| Money Editor | Add $100K, set to $1M or $9.99M, fix negative |
| Unlock All Vehicles | Unlock Phoenix, Stallion, Comet, NewHawk, Dragonfly, Kodiak |
| Vehicle Swap | Fly any aircraft in any mission |
| Weather Control | Day, Sunset, Night, Overcast, Auto |
| Unlock All Missions | Access all mission types |
| Dev Cheats | Enable the game's internal cheat system |
| Disable Crashes | Turn off crash detection |
| Manual Save | Force save without entering a mission |
| Dev Save (Slot 2) | Create a 100% save file in slot 2 |

### Flight Tab
| Feature | Description |
|---------|-------------|
| Force Multiplier | 2x or 5x engine thrust per aircraft |
| Drag Reduction | Lower drag for higher top speed |
| RPM Uncap | Remove RPM limiter |
| Boost | Temporary speed burst |
| Per-aircraft settings | Each aircraft type has independent speed controls |

### Combat Tab
| Feature | Description |
|---------|-------------|
| Hostile Skies Spawn | Spawn 1 or 3 AI enemy fighters |
| Load Enemy Prefabs | Manually trigger prefab loading from saved combat scene |
| Battle Mode | Skirmish (2), Small Battle (4), Invasion (8), All Out War (15) |
| Scan Combat Prefabs | Search for available enemy assets |
| Deep Scan | Comprehensive dump of all combat-capable objects |
| Weapons in Free Flight | Force-spawn aircraft weapons |
| Ammo Swap | Switch between bullet types |
| Grenade/Dart Equip | Equip handheld weapons |
| Laser Designator | Activate laser targeting |

### Physics Tab
- Scan and modify aircraft physics configs
- Direct access to `AircraftControllerConfigDO` values

### Aircraft Tab
- Scan aircraft models and mesh renderers
- Model swap experiments

### Explore Tab
| Feature | Description |
|---------|-------------|
| Hidden Content Scan | Search for ships, carriers, rigs, military objects |
| Vehicle/Traffic Scan | Find all vehicle components in scene |
| Carrier Diagnostic | Check Aircraft Carrier location system state |
| Force Carrier Flag | Set `m_isOnAircraftCarrier = true` |
| Trigger Carrier Signal | Dispatch `GotoAircraftCarrierClicked` |
| Quality Override | Force HIGH material quality |

### Info Tab
- Player data dump (money, unlocks, mission progress)
- Current aircraft stats
- Game state information

### Diagnostics Tab
- Real-time engine/physics values
- Component inspection
- Performance monitoring

## Vehicle Stats HUD (F2)

Press F2 to toggle an in-game overlay showing:
- Current speed (m/s and knots)
- Altitude
- Engine RPM and force
- Rigidbody drag and velocity
- Aircraft type and configuration

## File Structure

```
Mods/
  UW2TrainerMod.dll          - The compiled mod
  UW2Trainer_State.txt       - Persisted toggle states (fuel, ammo, god mode, etc.)
  UW2Trainer_Combat.txt      - Combat progression (kills, streaks, unlocks, combat scene)
UserData/
  HostileSkies/
    Music/                   - Drop .ogg files here for custom combat music (planned)
```

## Technical Notes

- Built on MelonLoader for IL2CPP Unity games
- Uses `Il2CppInterop` for accessing game internals
- Enemy AI uses the game's native `AirDefenseEnemyAIController` and `AiAircraftController`
- Prefabs are cloned with `DontDestroyOnLoad` to survive scene transitions
- Scene load hook (`OnSceneWasLoaded`) automatically scans combat mission scenes for enemy assets
- All modifications are runtime-only; no game files are modified

## Known Limitations

- Enemy prefabs require playing at least one combat mission per game session to cache
- The "Lightning" aircraft (vehicle ID 64) crashes on load - it's incomplete cut content
- `HasWeaponSystems` reports false on some enemies even though they have working turrets
- Enemy AI operates within local area; they don't follow across island boundaries

## Hidden Content Discovered

- **Aircraft Carrier** - UI navigation signals exist (`GotoAircraftCarrierClicked`, `IsOnAircraftCarrier`) suggesting a planned carrier location
- **Multiplayer Infrastructure** - Complete Photon networking and voice chat in the build
- **Cut Aircraft** - "Lightning" vehicle type in the enum but incomplete

## Building

```bash
# Ensure MelonLoader is installed in your UW2 directory
cd melonmod
dotnet build UW2Mod.csproj
# DLL auto-copies to Mods/ folder via post-build
```

## License

This mod is for personal use and educational purposes. Ultrawings 2 is developed by Bit Planet Games, LLC. This mod does not include any game assets or proprietary code.
