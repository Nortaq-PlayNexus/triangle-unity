# Porting notes: Unreal Engine → Unity

This document records what moved, what stayed identical, and every place the port
deliberately differs from the original Unreal slice. It exists so the next person
does not have to re-derive the reasoning.

## Why the port happened

The Unreal project at `C:\Users\natha\projects\the-triangle` is preserved untouched.
It was not abandoned for design reasons:

- `D:\Program Files\UE_5.8` had been reduced to a **0.34 GB stub**. `Build.bat` and
  `Engine\Binaries\Win64` were gone, so `UnrealBuildTool` could not construct a target.
- A real UE 5.8 install is ~120 GB. `D:` had **114.9 GB free**, so a repair meant a
  full re-download onto a different drive.
- The slice was source-complete (59 files, ~7,700 lines of C++) but had **never been
  compiled or run**. The `DEVELOPMENT_STATUS.md` gate read "runtime validation
  remains pending" and had for weeks.

Unity 2022.3.62f3 LTS was already installed and licensed, so the systems were ported
rather than rewritten. The design intent is unchanged; the engine is not.

## Structural mapping

| Unreal | Unity | Notes |
|---|---|---|
| `AActor` | `MonoBehaviour` on a `GameObject` | |
| `UActorComponent` | plain C# class, or `MonoBehaviour` if it needs the scene | |
| `UWorldSubsystem` | plain C# class owned by `TriangleBootstrap` | no equivalent engine-wide collection exists |
| `UGameInstanceSubsystem` | plain C# class owned by `TriangleBootstrap` | |
| `UDataAsset` | `ScriptableObject` | |
| `DECLARE_DYNAMIC_MULTICAST_DELEGATE` | C# `event Action<T>` | |
| `DECLARE_EVENT` | C# `event Action` | |
| `FName` | `string` with `Name.IsNone()` for the empty sentinel | |
| `USaveGame` + `SaveGameToSlot` | `[Serializable]` class + `JsonUtility` to `Application.persistentDataPath` | |
| `FTimerHandle` / `SetTimer` | `TriangleScheduler`, keyed by string | unscaled by default; the UE world timer also ran on scaled time |
| `UEnhancedInput` actions | legacy `Input` Manager (project is `activeInputHandler: 0`) | covers keyboard, mouse and gamepad from the same bindings |
| `USoundWaveProcedural` + `QueueAudio` | `AudioClip.Create` with a PCM reader callback | removes the queue/low-water bookkeeping |
| C++ UMG (`UTriangleHUDWidget`) | uGUI assembled in code | |
| `ATextRenderActor` (Platform "12") | emissive bar geometry | avoids shipping a font asset |
| `AExponentialHeightFog` | `RenderSettings` exponential-squared fog | global, not per-environment |
| `ASkyLight` | `RenderSettings.ambient*` | global, not per-environment |
| `/Engine/BasicShapes/*` | `Resources.GetBuiltinResource`, cached | plus a generated cone |
| Vertex tinting on basic shapes | shared material cache keyed by colour, surface and tiling | |

## Units and axes

Unreal authored the environments in centimetres, Z-up, X-forward/Y-right. Unity is
metres, Y-up, Z-forward. `Core/TriangleEnums.cs` centralises this in `Cm`:

```
UE  X (forward) -> Unity Z
UE  Y (right)   -> Unity X
UE  Z (up)      -> Unity Y
```

That permutation is even, so it is a pure rotation — handedness is preserved and
authored facing directions survive. `Cm.Pos`, `Cm.Dir` and `Cm.Rot` convert
positions, directions and rotators respectively. `EditMode/TriangleCoreTests.cs`
asserts the mapping so a future edit cannot silently break it.

A UE relative scale of `S` on the engine's 100 cm cube becomes a Unity local scale of
`S` directly, because that cube is exactly 1 m.

## Deliberate differences

These are the places the port is **not** a literal translation. Each is a case where
the original could not have worked as written, because it was never executed.

1. **Builders are authored in metres, not converted from centimetres.**
   `ATriangleRoomBuilder` positioned every piece in centimetres and lifted the whole
   room 5000 cm above the template landscape to dodge stray terrain collision. Unity
   has neither constraint. The ported builders therefore state their dimensions
   directly (8.2 × 6.2 × 3.0 m room, 14 × 8 × 3.5 m tunnel) with the originating
   Unreal half-extents named in comments. Behaviour is identical; the awkwardness is
   gone.

2. **Orientation is expressed the obvious Unity way.**
   Several Unreal rotations existed only to compensate for the Z-up authoring space —
   the bedroom clock is the clearest case: a cylinder with a `FRotator(90,0,0)` and
   scale `(0.68, 0.04, 0.68)`, which leaves the disc parallel to the wall it hangs
   on. The port places it on the wall with its face into the room. Since no one ever
   saw the original, replicating the bug had no value.

3. **The Triangle's glow uses an emissive material as well as a light.**
   The Unreal point used a faint point light with a `Cone` mesh beside it. In a dark
   room a light with nothing to catch it is invisible, so the port adds emissive
   shading to the cone.

4. **Platform 12's number is drawn from bars.**
   Unreal used `ATextRenderActor` with `WorldSize 52`. Unity's `TextMesh` needs a
   font asset, which the "no content assets" constraint forbids, so the numerals are
   built from small unlit emissive boxes.

5. **`JsonUtility` cannot serialise dictionaries.**
   `TMap<FString, bool> EnvironmentFlags` became parallel
   `EnvironmentFlagKeys` / `EnvironmentFlagValues` lists with accessor methods.

6. **The manifest pool is built in code.**
   The Unreal director created its six default bodies as constructor-time default
   subobjects on a `UDataAsset`. Unity cannot do that, so the director instantiates
   them with `ScriptableObject.CreateInstance` in `Awake` and destroys them in
   `OnDestroy`. Authoring can still replace the pool with real `.asset` files.

7. **Input uses the legacy Input Manager.**
   Porting Unreal's Enhanced Input stack would have meant authoring an input asset
   graph by hand. The project is configured for `activeInputHandler: 0`, so
   `UnityEngine.Input` works immediately and includes gamepad support. Swapping to the
   Input System package later is isolated to `TrianglePlayer`. The interaction prompt
   resolves its own label from the live binding via `Input.GetKeyName`, so a rebind
   cannot leave the on-screen hint pointing at the wrong key.

8. **Player movement uses `CharacterController`.**
   `ACharacter`'s movement component has no direct equivalent, and
   `CharacterController` matches the original capsule (34 cm radius, 88 cm half-height)
   and speeds (320 cm/s walk) with far less code.

9. **Deferred work is unscaled.**
   The UE world timer respected `SetGamePaused` through `timeDilation`, but this slice's
   pause is a hard `timeScale = 0` on a presentation overlay. Every deferred callback
   the port creates is a story beat or a transition rather than simulation, so the
   scheduler ignores `timeScale` unless a caller asks it not to.

10. **V Sync takes priority over the framerate ceiling.**
    The bootstrap sets one or the other, never both. `Application.targetFrameRate` is
    ignored on desktop while `vSyncCount` is non-zero, so setting both pinned a 144 Hz
    machine to 60 with no way to opt out. Tearing is worse than a dropped frame on a
    slow-moving dream, so V Sync wins when it is enabled.

## Preserved exactly

The parts that carry the design were ported without alteration:

- Every tuning value: lucidity/recognition band thresholds, the six manifestation
  bodies with their gates, weights and cooldowns, presence ramp rates, motif
  cooldowns, wake recognition floor, the 2.2 s difference delay.
- Every line of player-facing dream text, verbatim.
- The save schema: version sentinel, legacy migrations for schema 0 and 1, bounded
  collection sizes, environment-relative transforms, transactional
  `Applied`/`Queued`/`Failed` results. (A format marker and a retained backup were
  added afterwards; see the fixes section.)
- The safety rules: the director withdraws presence when the pawn is replaced so a
  stale manifestation timer cannot award recognition to a new player; restoration
  assigns values directly so level and milestone delegates never replay; a save
  cannot be applied mid-transition; unknown environment ids are rejected.

## Fixes made after the port

The slice was ported and then audited before it was ever played. Reading the source
against the design docs and running the test suite turned up defects the original
Unreal code could not have surfaced, because it had never executed. Recorded here
because several are behavioural changes rather than refactors.

### Correctness

- **Gravity stopped running when no movement key was held.** `TrianglePlayer.HandleMove`
  early-returned on zero input, so the pawn stopped falling the moment the player
  released WASD. A jump made without movement input never returned, and `isGrounded`
  was never re-evaluated. Horizontal and vertical motion are now integrated separately,
  every frame.
- **Two progression objects could never be reached.** The interaction finder enumerates
  colliders only. The clock had no collider and the window pane's was explicitly
  disabled, so neither inspectable was focusable. Both the window and the bed now
  carry an authored `Configure` call, because `AddComponent` runs `Awake`
  synchronously and the old `Awake`-time copy of the prompt name beat the builder's
  field assignment - every inspectable prompted as "Look".
- **`Cm.Rot` returned identity at ±90° of pitch.** It built a forward vector and handed
  it to `Quaternion.LookRotation` with `Vector3.up`; at the poles those are parallel and
  Unity silently falls back to identity. `float cos(90°)` is about 6e-17 rather than
  exactly zero, which is what let the degenerate case pass unnoticed. Every anomaly body
  the builder rotates by 90° - the razor gap, all three doorway blades - came out
  unrotated. The basis is now built explicitly, and roll is honoured instead of dropped.
- **The pause menu froze every deferred callback.** The scheduler ran on
  `Time.deltaTime`, and pausing zeroes `timeScale`, so a wake's 2.2 s difference line
  stalled on the "You wake up" frame for as long as the player sat in the menu. The
  scheduler is unscaled by default, with an opt-in `scaled` flag for anything that is
  genuinely simulation-timed. The wake cooldown moved to `Time.unscaledTime` for the
  same reason: on scaled time the player could pause to freeze the cooldown clock and
  mash the key.
- **Pause had two sources of truth.** The HUD held a boolean and the pawn separately
  assigned `timeScale` and the cursor, reading the boolean back to decide what to
  toggle. Any other system touching the timescale desynchronised them. The HUD now owns
  all three.
- **A parseable file that was not a save would be adopted.** `JsonUtility` will
  deserialise almost any JSON object into the snapshot type, leaving every field at its
  default - and a default object passes the schema, range and collection checks. Saves
  now carry a format marker, refused when absent at the current schema.
- **The station spawned the player over the track bed.** The spawn sat at `x = 0`,
  the middle of the rails, while its own comment said "on the right-hand platform".
- **Several authored sizes were wrong.** The bedroom clock was 1.36 m across and
  centred inside the wall. The train's right-hand door was at `y = -0.93`, below its
  own carriage floor, and its interior light was sealed inside an opaque box.
- **The Lucidity meter could never hide.** Its reveal test was `level >= Low`, and
  `Low` is the zero value of the enum, so it was true for every level.

### Robustness

- Saves are written through a staging file and swapped, and the previous save is
  retained as a `.bak`. A damaged primary now falls back to it.
- `TriangleShapes` and `TriangleProceduralTextures` release their static caches on
  `SubsystemRegistration`, and `TriangleRuntime` clears itself there too, so a play
  session entered without a domain reload cannot inherit a dead scene.
- `TriangleScheduler`, `TriangleFalseAwakeningManager`, `TrianglePoint`,
  `TriangleDoor` and `TriangleAnomalyActor` all divide by inspector-tunable values
  that could be zero, negative, NaN or infinite. Every one is now guarded, and the
  anomaly's fade envelope is normalised against its lifespan so the shape is always
  well defined.
- The HUD created a `StandaloneInputModule` and an enabled `GraphicRaycaster` for a UI
  built entirely from `Text` and `Image`, neither of which handles pointer input. The
  input module polled the mouse and every Input Manager axis every frame for no
  possible consumer. Both are now created disabled, so a pause menu can enable the
  raycaster rather than rebuild the plumbing.
- `TrianglePlayerBuilder` called `EditorApplication.Exit(0)` unconditionally, so
  building from the menu quit the whole editor. It is now batchmode-only.
- The scene builder no longer offers to replace the open scene while unsaved work is
  queued, and refuses to run during a play session.

### World ownership

- **Ambient and fog leaked across environments.** Both are process-wide in Unity — there
  is no per-camera or per-volume equivalent short of a custom shader — so an
  environment cannot own them in the ordinary sense. Both builders wrote them and walked
  away, leaving the project render settings permanently mutated after play mode exited.
  `World/TriangleEnvironmentAtmosphere` makes the ownership explicit.
  The ordering here is the subtle part: the flow destroys the outgoing builder and
  creates the incoming one in the same statement pair, but `Object.Destroy` is deferred
  to the end of the frame while the new builder's `Awake` runs immediately. A
  save-and-restore in `OnDestroy` would therefore put the bedroom's fog back *after* the
  station had applied its own. The atmosphere tracks the current owner, and the flow
  calls `MarkReplaced` before tearing an environment down.
- **Intrusions were parented to the scene root.** An anomaly created at the root is
  invisible to the teardown: when the dream ended, the intrusion survived and the
  director kept updating an object whose world no longer existed. They now hang off a
  single root parented to the dream host — deliberately the host rather than a builder,
  since an intrusion is world-owned and the director despawns them on an environment
  change.

### Audio and accessibility

- **The dream bed never changed state.** The state machine was fully implemented — four
  named states, a fade, a silence override — and nothing ever called it, so the bed sat
  at `Calm` for the entire game while the hum rose over an unchanged bed. The bootstrap
  now binds it to the director's existing presence and wake signals, so the bed ducks
  as The Triangle arrives, drops to nothing at maximum presence, and silences on a
  completed false awakening.
- **Subtitle scale and opacity were unreachable.** They existed on the HUD as
  inspector-only values, so the accessibility affordance was authored but no player
  could ever change it. `Core/TriangleAccessibility` moves them to `PlayerPrefs` and adds
  sensitivity, invert-Y, mouse smoothing, an opt-in head bob, a subtitle backdrop, and
  separate music and hum volumes. Backed by `PlayerPrefs` rather than the save file on
  purpose: these are comfort preferences, not dream state, so an older save cannot undo
  them and wiping the dream cannot reset them.
- **Head bob is off by default**, and the hum has its own volume control. The hum is a
  sustained tone around 43/47 Hz — the register people find uncomfortable — so a single
  combined volume would force a choice between the bed and the presence cue.

### Performance

- `TriangleProceduralTextures` cached each octave's value-noise lattice instead of
  re-deriving four hashes per pixel. The low-frequency octaves were the worst case: a
  period-8 lattice was being evaluated 65536 times to produce 81 distinct values.
  Output is bit-identical; the height fields build roughly 1.7× faster.
- The generators resolve their octaves outside the pixel loop. The first attempt did a
  dictionary probe per sample, which measured at about the cost of the hashes it saved
  and made the whole change a wash.
- `TriangleShapes` resolves built-in meshes through `Resources.GetBuiltinResource`
  rather than instantiating a primitive `GameObject` per mesh and calling
  `DestroyImmediate` on it at runtime.
- Procedural texture generation and the drone bed are now timed in development builds
  and logged per stage, so the real cost on the target machine is visible instead of
  estimated.

## What is still missing

The Unreal slice stopped at Phase 2. These remain unimplemented, exactly as they were
before the port:

- Phases 3–6, 9, 10: the mansion, the castle and the Floating Man, the witches, the
  final fused dream, the ending and the menu transformation.
- Dream bleeding beyond director-driven intrusions.
- Real audio content. The dream bed is a generated drone; the hum is synthesised.
- Lighting has no baked data and no post-processing. The original budget targeted a
  GTX 1080 at 1080p/30+; that has not been profiled.
- The first dream is still assembled in a single frame, so the remaining procedural
  generation cost is a one-time stutter on the opening beat. The two levers, if it
  proves noticeable, are reducing the generated texture resolution and moving the
  sampling off the main thread. The timings are logged so the decision can be made on
  a measurement rather than a guess.
