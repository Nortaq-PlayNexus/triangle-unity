# Dreamcore horror. Dream logic. No combat.

## Core loop

Explore → observe → remember → recognize → question reality → become lucid →
manipulate the dream → encounter recurring entities → discover connections → lose
certainty → return to previous environments → confront the original nightmare.

No weapons, no conventional combat. Survival is observation, exploration, dream
manipulation, puzzles and awareness.

## The Triangle

Never a conventional monster. Rarely fully visible. It manifests as intrusions into
the dream: a tiny point, a triangular shape, a sharp silhouette, an impossible
shadow, an absence where something should be. The player should sometimes know it is
present **before** seeing it — via sound, silence, lighting, camera behaviour, a
recurring motif.

The Triangle stays ambiguous: supernatural, psychological, symbolic, a dream
construct — or something else. Never explained.

## Horror style

Anticipation, dread, impossible spaces, recognition, repetition, silence, distorted
familiarity, things appearing where they shouldn't, subtle wrongness. The player
should frequently think *"something is wrong"* before they know why. Jumpscares are
not the tool.

## Two meters, deliberately separate

**Lucidity** is "I know I am dreaming." 0..100, event-gated, never a passive timer.

- Low (0–30): the dream is accepted; impossibilities pass unnoticed.
- Medium (30–60): repeated locations get noticed; doors misbehave.
- High (60–90): the player can manipulate dream elements.
- Maximum (90–100): dream rules break; the Triangle becomes unmistakable.

**Recognition** is "I know what is here." 0..100, five stages.

- Unknowing (0–25): strangeness passes without note.
- Noticing (25–50): the same shape keeps returning; it feels arranged.
- Recognizing (50–75): the pattern is recognised before it completes.
- Anticipating (75–95): the presence is felt behind surfaces; the hum has meaning.
- Master (95–100): "it's here" — known without seeing.

The Triangle is an intrusion the player learns to recognise *before* they can
understand it. Recognition climbs by observing the same pattern in different bodies —
the corner, a gap in the wallpaper, a clock stopped at twelve — until presence can be
sensed before anything is visible.

## The hum

Two detuned partials around 43/47 Hz, breathing in and out of phase, synthesised at
runtime. It follows presence. A hum that starts and never resolves into a visible
shape is a valid manifestation in its own right, and it teaches only a fragment of
recognition until the player can reliably attribute it.

## Wake

Not a health bar. Certain actions destabilise the dream. Waking shows screen
distortion, audio collapse and a hard cut to a bedroom — but waking is **not always
real**.

The dream refuses to let go of a player who cannot tell they are in it: a wake
attempt needs Recognition 75 or better. When it resolves, the first thing is "You
wake up", and 2.2 seconds later a single small wrongness arrives:

- The clock still says twelve.
- The moon is on the wrong side of the window.
- The hallway is longer than it was.
- The hum is still here.
- You are still ten years old.

Each difference line is a "how does the player recognise it" beat, not a jumpscare.

## The six bodies

The Director picks from these by weight, once the recognition gate is satisfied and
once the per-body cooldown has expired.

| Id | Body | Gate | Weight | Cooldown |
|---|---|---|---|---|
| `UpperCornerPoint` | the corner point | 0 | 5.0 | 14 s |
| `GapInTheWall` | razor-thin triangular gap | 25 | 1.2 | 16 s |
| `CornerPulled` | a corner set apart from itself | 50 | 1.0 | 20 s |
| `DistantPoint` | a tiny black point in the distance | 50 | 0.8 | 18 s |
| `TheHumOnly` | presence, nothing visible | 75 | 0.7 | 20 s |
| `DoorwayCut` | an impossible triangular doorway | 75 | 0.6 | 24 s |

Recognition is only granted when the body is actually **witnessed** — in view, in
range, and not occluded.

## Environments

1. **Childhood bedroom** — the anchor; familiar but subtly wrong; the upper corner
   draws the eye. A clock stopped at twelve. A notebook. A door that opens only at
   High Lucidity.
2. **Underground station** — fluorescent dread; Platform 12; the train is wrong.

Later phases: clock room, marble mansion, castle and the Floating Man, the witches,
the false-awakening bedroom, the final fused dream.

## The notebook

Fills as the player explores. Each read can reveal the next page; re-reading a
discovered page grants nothing. It ends up containing drawings of places the player
has not visited yet — and one page with only a triangle.

## Ending

No boss battle. The final Triangle starts as an extremely small point. The game asks
the player to approach it. As they do: sound disappears, geometry simplifies, the HUD
vanishes, the camera stills. Black. Silence. *WAKE UP.*

The player wakes in the childhood bedroom, exits the house… and outside is the
mansion. Cut to black: **THE TRIANGLE / DREAM AGAIN.**
