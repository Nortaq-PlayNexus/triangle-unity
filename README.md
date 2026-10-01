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
- **Content:** none. Every mesh, material, texture, and sound is generated at runtime,
  which preserves the original slice's "no content assets" constraint.

A development build logs how long the opening assembly takes and how much of that was
procedural texture generation, so the one-time cost of the first frame is a number
rather than a guess.

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
| `Core/TriangleBootstrap` | MonoBehaviour | Registers every service, builds the dream, reconciles frame pacing |
| `Core/TriangleRuntime` | static | Service locator (Unreal's subsystem collection) |
| `Core/TriangleScheduler` | MonoBehaviour | Keyed deferred callbacks, unscaled by default |
| `Core/TriangleAccessibility` | static | Comfort and accessibility settings, persisted to PlayerPrefs |
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
| `Audio/TriangleAudioDirector` | class | Dream bed states, silence holds, and hum routing |
| `Audio/TriangleProceduralHum` | MonoBehaviour | The hum, synthesised at runtime |
| `Save/TriangleSaveSystem` | class | Versioned snapshot, migration, transactional apply |
| `World/TriangleRoomBuilder` | MonoBehaviour | Procedural childhood bedroom |
| `World/TriangleStationBuilder` | MonoBehaviour | Procedural underground station, Platform 12 |
| `World/TriangleEnvironmentAtmosphere` | MonoBehaviour | Owns the global ambient and fog for the active environment |
| `World/TriangleDoor` | MonoBehaviour | High-Lucidity door and the station transition seam |
| `World/TriangleNotebook` | MonoBehaviour | Notebook pages and realization rewards |
| `World/TriangleTrain` | MonoBehaviour | The wrong train at Platform 12 |
| `UI/TriangleHUD` | MonoBehaviour | Prompt, meters, subtitle queue, transition veil |

## Accessibility and options

Durable horror is not playable for everyone. The slice ships with a small set of
comfort settings, all adjustable at runtime and all persisted between sessions:

| Setting | Why it exists |
|---|---|
| Subtitle scale / opacity | The game is told almost entirely in text; the bedroom alone narrates through the notebook, the window, the bed and the clock |
| Subtitle backdrop | Reduced-opacity text over a near-black bed can become unreadable, and opacity alone cannot fix that |
| Mouse / gamepad sensitivity | Default rates that are comfortable for one person are not for another |
| Invert look Y | Standard for flight-sim and left-handed stick players |
| Mouse smoothing | Aiming by feel needs a little inertia; 0 is unmodified input |
| Head bob | **Off by default.** A camera bob that cannot be disabled is a real barrier, and this game already leans on camera motion for its effect |
| Music / hum volume, separately | The hum is a sustained tone around 43–47 Hz, the register people find uncomfortable. One combined control would force a choice between the bed and the presence cue |

These live in `Core/TriangleAccessibility`, backed by `PlayerPrefs` rather than the
save file. They are device and comfort preferences, not dream state: a player who
raises the subtitle size should not have that undone by loading an older save, and
wiping the dream should not reset how the game looks.

## Tests

**59 tests.** This is the validation the Unreal version never got — its
`DEVELOPMENT_STATUS.md` had claimed "source complete; runtime gate pending" for
weeks because the editor target could not build.

`Assets/Tests/EditMode` (18 tests) covers the progression maths, the pattern
catalogue, save-schema defaults, the centimetre-to-metre axis conversion the
ported builders depend on, and the pole behaviour of that conversion. These port
the four original Unreal automation tests and add coverage for the conversion.

`Assets/Tests/PlayMode` (41 tests) drives the real scene and walks the slice's
acceptance path end to end: bedroom spawn, notebook pages, corner manifestation,
recognition, the High Lucidity door, the deliberate second interaction, Platform 12,
the wrong train, save, wipe, load, and a queued cross-environment load. It also
covers environment ownership of the global render settings, intrusion parenting, the
accessibility wiring, and the dream bed's reaction to presence. Every assertion
reads live system state; nothing is mocked.

```
Unity.exe -batchmode -nographics -projectPath . -runTests -testPlatform EditMode  -testResults edit.xml
Unity.exe -batchmode -nographics -projectPath . -runTests -testPlatform PlayMode -testResults play.xml
```

Both return a non-zero exit code on failure, so they can gate CI.

The PlayMode setup unpauses the game, resets the accessibility settings to their
defaults, and the teardown deletes the save slot *and* its backup. That is
load-bearing rather than defensive: several tests exercise the pause menu and several
adjust a volume or a subtitle size, and either leaking would silently poison every
test after it.

### Bugs the suite caught

Reading the source found most of what is listed in `PORTING.md`, but a handful only
showed up as failing assertions:

- `MonoBehaviour.Invoke` was being used to defer callbacks that belong to plain
  classes. It fails silently, which meant every environment transition never fired.
- The motif clock mutated its own dictionary mid-enumeration, throwing "Collection was
  modified" on roughly one frame in thirty.
- Mouse look read pitch back from `transform.eulerAngles`, which Unity reports in
  0..360, so looking up a few degrees read as ~350 and the clamp slammed the view to
  the floor.
- The station's "player is on the platform" assertion only checked height, which the
  broken spawn satisfied. It now checks the lateral position against the track gap.
- Two tests would have passed against a blank save: one adopted a file with no format
  marker, another would have restored nothing and reported success.

## Documentation

- `PORTING.md` — the Unreal → Unity mapping, and every place the port deliberately
  differs from the original.
- `DESIGN.md` — the game design the systems implement.
