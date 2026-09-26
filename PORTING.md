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
| `FTimerHandle` / `SetTimer` | `MonoBehaviour.Invoke` / `CancelInvoke`, or explicit tick accumulators | |
| `UEnhancedInput` actions | legacy `Input` Manager (project is `activeInputHandler: 0`) | covers keyboard, mouse and gamepad from the same bindings |
| `USoundWaveProcedural` + `QueueAudio` | `AudioClip.Create` with a PCM reader callback | removes the queue/low-water bookkeeping |
| C++ UMG (`UTriangleHUDWidget`) | uGUI assembled in code | |
| `ATextRenderActor` (Platform "12") | emissive bar geometry | avoids shipping a font asset |
| `AExponentialHeightFog` | `RenderSettings` exponential-squared fog | |
| `ASkyLight` | `RenderSettings.ambient*` | |
| `/Engine/BasicShapes/*` | `GameObject.CreatePrimitive` meshes, cached | plus a generated cone |
| Vertex tinting on basic shapes | shared material cache keyed by colour | |

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
   Input System package later is isolated to `TrianglePlayer`.

8. **Player movement uses `CharacterController`.**
   `ACharacter`'s movement component has no direct equivalent, and
   `CharacterController` matches the original capsule (34 cm radius, 88 cm half-height)
   and speeds (320 cm/s walk) with far less code.

## Preserved exactly

The parts that carry the design were ported without alteration:

- Every tuning value: lucidity/recognition band thresholds, the six manifestation
  bodies with their gates, weights and cooldowns, presence ramp rates, motif
  cooldowns, wake recognition floor, the 2.2 s difference delay.
- Every line of player-facing dream text, verbatim.
- The save schema: version sentinel, legacy migrations for schema 0 and 1, bounded
  collection sizes, environment-relative transforms, transactional
  `Applied`/`Queued`/`Failed` results.
- The safety rules: the director withdraws presence when the pawn is replaced so a
  stale manifestation timer cannot award recognition to a new player; restoration
  assigns values directly so level and milestone delegates never replay; a save
  cannot be applied mid-transition; unknown environment ids are rejected.

## What is still missing

The Unreal slice stopped at Phase 2. These remain unimplemented, exactly as they were
before the port:

- Phases 3–6, 9, 10: the mansion, the castle and the Floating Man, the witches, the
  final fused dream, the ending and the menu transformation.
- Dream bleeding beyond director-driven intrusions.
- Real audio content. The dream bed is silent; the hum is synthesised.
- Lighting is flat-lit with no baked data and no post-processing. The original budget
  targeted a GTX 1080 at 1080p/30+; that has not been profiled.
