# THE TRIANGLE — Unity port

A dreamcore horror game about knowing you are dreaming before you know what is
dreaming you. First-person, no weapons, no combat. Survival is observation,
exploration, dream manipulation, puzzles and awareness.

This is a **Unity 2022.3 LTS port** of the original Unreal Engine vertical slice.
The Unreal installation at `D:\Program Files\UE_5.8` was reduced to a 0.34 GB stub
(missing `Build.bat` and `Engine\Binaries\Win64`) and repairing it required a ~120 GB
re-download onto a drive with 114.9 GB free. The slice's systems were source-complete
but had never been compiled or run, because the editor target could not build. The
design intent was preserved exactly; the engine changed.

- **Engine:** Unity 2022.3.62f3 LTS
- **Language:** C# / .NET Standard 2.1
- **Content:** none. Every mesh, material, and sound is generated at runtime, which
  preserves the original slice's "no content assets" constraint.

## Running it

1. Open the project with Unity 2022.3.62f3.
2. `Assets/Scenes/Dream.unity` is the only scene. It contains one GameObject,
   `TriangleBootstrap`, which assembles the entire dream at runtime.
3. Press Play.

If the scene is missing, use the menu item **Triangle → Rebuild Playable Scene**, or
from a terminal:

```
Unity.exe -batchmode -quit -projectPath <project> -executeMethod Triangle.EditorTools.TriangleSceneBuilder.RebuildPlayableScene
```

## Controls

| Action | Keyboard / Mouse | Gamepad |
|---|---|---|
| Move | `W` `A` `S` `D` | Left stick |
| Look | Mouse | Right stick |
| Jump | `Space` | South face button |
| Interact | `F` | West face button |
| Wake (try) | `Q` | East face button |
| Quick save | `F5` | Left shoulder |
| Quick load | `F9` | Right shoulder |
| Pause | `Escape` | Menu |

## Systems

| Area | Type | Responsibility |
|---|---|---|
| `Core/TriangleBootstrap` | MonoBehaviour | Registers every service, builds the dream |
| `Core/TriangleRuntime` | static | Service locator (Unreal's subsystem collection) |
| `Core/TriangleGameFlow` | class | Owns the active procedural environment, fade-and-swap transitions |
| `Core/TriangleMotifSystem` | class | Dream clock, motif scheduling, cooldowns, recognition gates |
| `Core/TriangleEnums` | static | `Cm` unit/axis conversion plus every shared enum |
| `Player/TrianglePlayer` | MonoBehaviour | First-person controller, interaction finder, Lucidity + Recognition |
| `Lucidity/TriangleLucidity` | class | 0..100 awareness meter with four level bands |
| `Lucidity/TriangleRecognition` | class | 0..100 "I know what is here" meter and the pattern catalogue |
| `Interaction/TriangleInteractable` | MonoBehaviour | Marks an object focusable/interactable |
| `Dream/TriangleDreamDirector` | MonoBehaviour | Presence ramp, manifestation selection, motifs, wake withdrawal |
| `Dream/TriangleManifestationDefinition` | ScriptableObject | One body The Triangle can take |
| `Dream/TrianglePoint` | MonoBehaviour | The upper-corner point, first learned shape |
| `Dream/TriangleFalseAwakeningManager` | class | The unreliable wake and its difference lines |
| `Nightmare/TriangleAnomalySystem` | class | Places and budgets the intrusions |
| `Nightmare/TriangleAnomalyActor` | MonoBehaviour | One intrusion's fade envelope |
| `Audio/TriangleAudioDirector` | class | Dream bed states and hum routing |
| `Audio/TriangleProceduralHum` | MonoBehaviour | The hum, synthesised at runtime |
| `Save/TriangleSaveSystem` | class | Versioned snapshot, migration, transactional apply |
| `World/TriangleRoomBuilder` | MonoBehaviour | Procedural childhood bedroom |
| `World/TriangleStationBuilder` | MonoBehaviour | Procedural underground station, Platform 12 |
| `World/TriangleDoor` | MonoBehaviour | High-Lucidity door and the station transition seam |
| `World/TriangleNotebook` | MonoBehaviour | Notebook pages and realization rewards |
| `World/TriangleTrain` | MonoBehaviour | The wrong train at Platform 12 |
| `UI/TriangleHUD` | MonoBehaviour | Prompt, meters, subtitle queue, transition veil |

## Tests

**34 tests, all passing.** This is the validation the Unreal version never got — its
`DEVELOPMENT_STATUS.md` had claimed "source complete; runtime gate pending" for
weeks because the editor target could not build.

`Assets/Tests/EditMode` (12 tests) covers the progression maths, the pattern
catalogue, save-schema defaults, and the centimetre-to-metre axis conversion the
ported builders depend on. These port the four original Unreal automation tests and
add coverage for the conversion.

`Assets/Tests/PlayMode` (22 tests) drives the real scene and walks the slice's
acceptance path end to end: bedroom spawn, notebook pages, corner manifestation,
recognition, the High Lucidity door, the deliberate second interaction, Platform 12,
the wrong train, save, wipe, load, and a queued cross-environment load. Every
assertion reads live system state; nothing is mocked.

```
Unity.exe -batchmode -nographics -projectPath . -runTests -testPlatform EditMode  -testResults edit.xml
Unity.exe -batchmode -nographics -projectPath . -runTests -testPlatform PlayMode -testResults play.xml
```

Both return a non-zero exit code on failure, so they can gate CI.

Validation also caught six real bugs that reading alone would not have found —
including `MonoBehaviour.Invoke` being used to defer callbacks that belong to plain
classes, which fails silently and meant every environment transition never fired.
The commit message lists them all.

## Documentation

- `PORTING.md` — the Unreal → Unity mapping, and every place the port deliberately
  differs from the original.
- `DESIGN.md` — the game design the systems implement.
