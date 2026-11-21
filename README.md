# Sound Physics

RedLoader mod source for Sons of the Forest. Adds real-time acoustic raytracing to every 3D sound in the game: dynamic environment reverb and wall/terrain occlusion. Uses only the game's existing sounds, with no custom audio files and no FMOD bank edits.

## Features

- **Environment reverb.** Every 0.2 s, 16 rays probe the space around the listener. The mod blends reverb presets based on how enclosed the space is, what is overhead, wall distances and surface type:
  - **Cabin / tight interior:** short, warm room ring (0.4–0.8 s).
  - **Cave / stone bunker:** long, cold echo (2.0–3.8 s).
  - **Canyon / cliff:** a subtle delayed echo, timed from the distance to the rock face.
  - **Open field / forest:** stays dry.
- **Sound occlusion.** Sounds behind terrain, rock or walls are low-pass filtered and slightly quieter. The amount depends on the material and its thickness. Trees muffle only slightly. Sounds that can reach you around an edge are muffled less. Unblocked sounds are left untouched.
- **Every sound.** One-shots are caught the moment they play. Loops and emitter sounds are found by scanning the loaded FMOD banks, so sounds started from anywhere are covered.
- **Compatible.** Postfix-only hooks. Parameters change smoothly, without clicks.

## Build

Install RedLoader and launch the game once so it generates `_Redloader/Game`, then set `SOTF_GAME_DIR` to the game installation directory or pass `-p:GameDir=<game-directory>` to `dotnet build`.

The project targets `net6.0`, matching the RedLoader runtime. Release builds omit debug symbols so the generated DLL does not carry a local PDB path.

## Mod files

The build places `SoundPhysicsSOTF.dll` at the output root and copies `manifest.json` into the `SoundPhysicsSOTF` subfolder for the RedLoader package layout.

## Configuration

Settings are stored in `UserData/SoundPhysicsSOTF/SoundPhysicsSOTF.cfg`.

| Setting | Default | Range | Description |
|---|---|---|---|
| `ModEnabled` | `true` | | Master switch for reverb and occlusion. |
| `ReverbEnabled` | `true` | | Dynamic environment reverb. |
| `OcclusionEnabled` | `true` | | Muffles sounds behind terrain, walls and rock. |
| `ReverbIntensityMultiplier` | `1.0` | 0.0–2.0× | Scales the reverb level. |
| `MinOcclusionCutoffHz` | `900` | 300–5000 Hz | Low-pass cutoff for a fully blocked sound. Lower is more muffled. |
| `MaxProbeDistance` | `120` | 20–200 m | Range of the reverb probe rays. |
| `OcclusionVolumeFloor` | `0.55` | 0.1–1.0 | Volume multiplier for a fully blocked sound. |
| `DebugOverlay` | `false` | | Shows the live acoustic readout on screen. |
| `UsePlayHooks` | `true` | | Catches one-shot sounds the instant they play. Requires a restart. |
| `ReverbBusPath` | *(empty)* | | Empty applies reverb to the master mix. Set an FMOD bus path (e.g. `bus:/SFX`) to limit it to that bus. Available bus paths are written to the log on load. |

## Console commands

| Command | Effect |
|---|---|
| `soundphysics debug` | Toggles the debug overlay. |
| `soundphysics on` / `off` | Enables or disables the mod. |
| `soundphysics reverb` | Toggles environment reverb. |
| `soundphysics occlusion` | Toggles sound occlusion. |
| `soundphysics reset` | Clears tracked sounds and caches. |
| `soundphysics save` | Saves the current settings to the config file. |

## Changelog

### 1.0.1
- Rebuilt for .NET 6 so the mod loads on standard RedLoader. The previous build targeted a newer .NET and could stop RedLoader from loading other mods.

### 1.0.0
- Initial public release.

## License

MIT, see `LICENSE`. Author: Zemo.
