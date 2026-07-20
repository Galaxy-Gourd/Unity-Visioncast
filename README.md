# Visioncast

Line of sight simulation package for Unity. **Vision sources** will collect **vision targets** within their sight range, then raycast to determine if the object is obstructed or visible. Use this data to simulate NPCs seeing important objects, security cameras, etc.

**WebGL demo** available here: https://mjstephens.github.io/VisioncastBuild/

> **Data-oriented mode.** A Burst/Jobs pipeline computes the whole per-tick vision natively — ~8× faster at
> 1000 sources, ~15× less main-thread work with the deferred (off-main-thread) raycast, zero per-frame GC.
> Same MonoBehaviour authoring and API; opt in with `VisionPipeline.UseDod()`. See
> [`SETUP-DOD.md`](./SETUP-DOD.md) to enable it and [`../PERFORMANCE-DOD.md`](../PERFORMANCE-DOD.md) for the
> measurements and design.

![Screenshot 2024-02-02 at 10 07 54 AM](https://github.com/mjstephens/Visioncast/assets/4731148/df78fd9f-9168-4c5a-8d8e-cf4de3568471)


## How It Works

Visioncast runs in two phases each tick.

In the **broadphase**, each source gathers the registered vision targets within its **range** and **field of view** (an angle test against the source's heading — local forward by default, overridable), filtered by layer. This fast first pass eliminates everything a source can't possibly see, leaving a short list of candidates.

In the **narrowphase**, the system samples points across each surviving target and **raycasts** to them to test for obstruction — a target within range and FOV can still be behind a wall, so it passes the broadphase but fails the narrowphase. Obstruction layers are per-source. Every source's rays are batched into a single asynchronous `RaycastCommand` job across worker threads, then the results are distributed back to each source.

The **data-oriented pipeline** (recommended — enable with `VisionPipeline.UseDod()`) does all of this natively in Burst jobs over flat arrays: a spatial-grid broadphase over the registered targets (no physics-scene query), Burst sample generation that writes the ray batch directly, and a parallel reduce that scatters results back — ~8× the throughput at zero per-frame GC, plus an optional deferred split that runs the raycast off the main thread. A managed path with the identical authoring surface and API remains as a fallback. See [`SETUP-DOD.md`](./SETUP-DOD.md) and [`../PERFORMANCE-DOD.md`](../PERFORMANCE-DOD.md).


![Screenshot 2024-02-02 at 10 09 35 AM](https://github.com/mjstephens/Visioncast/assets/4731148/41bb7cb0-d85b-497f-8cc1-bab3c09be73c)
_The orange box behind the wall has passed the broadphase, but failed the narrowphase. It is within the range and field of view of the source, but all visible points on the target are obstructed by the blue wall. Thus this object is considered "not visible"._


## Results: visibility, coverage, occlusion

The system reports raw counts per seen target and lets you decide what they mean — it never bakes in a policy. For each sample point it records whether the point is **in-cone** (inside the FOV cone and range) and **reached** (its ray hit the target, unobstructed), and reports three counts per target:

- `SampleCount` — points tested (the denominator)
- `InConeCount` — points inside the beam
- `LitCount` — points that are both in-cone **and** reached

Form whatever ratio the game needs:

- **Illumination** (`LitCount / SampleCount`) — how lit the target is; ramps smoothly as it enters a light. This is the stealth signal.
- **Beam coverage** (`InConeCount / SampleCount`) — how much of the target is in the cone, ignoring occlusion.
- **Occlusion within the beam** (`LitCount / InConeCount`) — of what's in the beam, how much isn't blocked.

The legacy `Visibility` (`VisiblePointCount / SampleCount`, cone-agnostic occlusion) is unchanged, and fine for simple "can I see them" detection. For **per-body-part** stealth (a lit head worth more than a lit hand), register each part standalone, read each part's coverage, and combine with your own weights — the package never sees a weight. See [`SETUP-DOD.md`](./SETUP-DOD.md) for the full contract.



## Implementation Components

### VisioncastSource
The base vision component that exposes the object to the visioncast system. Create an override of this class, or use one of the included overrides. **VisioncastSourceSimple** provides a rudimentary implementation that still provides detailed results. **VisioncastSourceFiltered** is similar, but will automatically filter the results into more details, defining targets that are newly seen, newly lost, as well as the "key" target (ie the visible target most directly in front of the source). **VisioncastSourceCompound** combines several child sources into a single de-duplicated output — useful when one logical observer owns multiple vision sources, or for aggregating across several sources (e.g. how "lit" an actor is across all nearby lights). It aggregates both occlusion (`TryGetVisibility`) and in-beam coverage (`TryGetCoverage`) across its children, by Max / Sum / Average.

![Screenshot 2024-02-02 at 10 06 37 AM](https://github.com/mjstephens/Visioncast/assets/4731148/c624d7b7-f30b-4488-a56e-39111e717d27)


### Vision Targets
For an object to be "seen" by the visioncast system, it registers its collider with **VisionTargetsManifest** (typically in `OnEnable`/`OnDisable`). Only registered colliders are ever considered as targets, so you expose only the objects that matter — interactables, actors, etc. — rather than every collider in the scene, which keeps the broadphase cheap. For multi-collider actors, register each collider against the owning actor (`Register(collider, actor)`) so results collapse to a single target per actor. Or register a collider on its own (`Register(collider)`) to keep it a distinct target — e.g. weighted body parts you want to score individually for stealth.

To be notified when it's seen, an object implements **IVisibleObject**; its `Seen(VisioncastSource)` callback fires when a source has line of sight to it.

You don't hand-place the points that get raycasted against — the narrowphase samples them from the target's collider automatically. How densely they're sampled is configurable per-source via **SampleMode** / **SampleResolution**: the default is a handful of points (fast, fine for simple detection), while denser sampling covers more of the object and yields a smoother visibility fraction for uses like stealth exposure.

![Screenshot 2024-02-02 at 10 17 57 AM](https://github.com/mjstephens/Visioncast/assets/4731148/4a93882b-79f3-46ed-90dc-7f96afbe1a01)
_Sample points (red spheres) around a target. The system generates these from the target's collider — they are the raycast targets for the narrowphase, and denser sampling (SampleResolution) covers more of the object for a finer visibility result._


## Samples

Download samples by selecting the visioncast package in the package manager, then navigating to the "samples" tab.

### Core

Basic implementations of all major components — vision sources, vision targets, and realtime debug visualization of line of sight and narrowphase (raycast) results.

### DoD Deferred Driver

A drop-in driver for the data-oriented path with the deferred (off-main-thread) raycast, using no external scheduler. It takes ownership of the physics step and calls the vision phases in the correct order, so you can showcase the DoD path with a single component. See [`SETUP-DOD.md`](./SETUP-DOD.md).

### Crowd LOD

Vision level-of-detail and relevance at scale. One component spawns hundreds of rotating sources and targets around an orbiting relevance origin, drives the DoD pipeline, and shows the per-tick cost live. Toggle LOD to watch the tick collapse from every-source-every-tick to a tiered cadence — near sources cast every tick at full resolution, far ones rarely, out-of-range ones not at all (see `VisionLOD` / `VisionRelevance`). Sources brighten the tick they cast and dim as their data goes stale, so the scheduler is visible.

### Stealth Exposure

Stealth exposure as weighted light **coverage**. A multi-collider player (head, torso, arms, legs) walks through two spotlights past a wall; each body part is a separate target, a `VisioncastSourceCompound` aggregates the lights, and the sample folds per-part coverage into one exposure score using authored per-part weights. Shows the coverage ramp (partial-in-beam), occlusion (the wall carves a shadow even where the target is in-cone), multi-light aggregation via `TryGetCoverage`, semi-transparent light cones, and a runtime walk-speed slider. 

### Guard Patrol

AI perception driven by the filtered enter/exit events. A guard (`VisioncastSourceFiltered`) reacts to `NewlySeenObjects` / `NewlyLostObjects` with a Patrol → Alert → Search state machine: it locks on and tracks when it first sees an intruder, heads to the last-known position and counts down when it loses line of sight, and lapses back to patrol on timeout. The intruder is a multi-collider actor (head + torso registered under one identity), so the filter collapses it to a single target. The FOV cone is drawn semi-transparent and tinted by state.

### Interaction Focus

"Look to interact" using `TargetedObject` — the single visible target nearest the centre of the view. An interactor faces a fan of props; several are in view at once, but only the one you are pointing at is focused (it glows, grows, and lifts). Sweep the aim slider and the focus hops from prop to prop; click Interact to act on whatever is focused. Demonstrates the single-object disambiguation an interaction system needs: not "what can I see" but "what am I pointing at".

## Tests

The package ships edit mode and play mode tests under `Tests/`. To run them, add the package to the
`testables` array of your project's `Packages/manifest.json` — Unity only surfaces a package's tests
when it is listed there:

```json
"testables": [ "com.galaxygourd.visioncast" ]
```

They then appear in **Window > General > Test Runner**:

- **EditMode** (`GG.Visioncast.EditorTests`) — the deterministic half of the system, with results
  injected straight into a source's result buffer: sample-point generation and oriented bounds, LOD tier
  resolution and hysteresis, the target manifest (identity mapping + the dense id registry), the
  raw-to-refined results filter, the filtered source's enter/exit sets and targeting, and compound
  aggregation (Max / Sum / Average, visibility vs coverage).
- **PlayMode** (`GG.Visioncast.Tests`) — the whole pipeline against a real physics scene, run twice
  (managed and DoD) for parity: seeing, occlusion, cone / range / layer filtering, sampling density,
  source lifecycle, the deferred DoD schedule-complete split, broadphase equivalence across all three
  implementations, the DoD target gather, and the standalone driver.

`GG.Visioncast.TestSupport` holds the shared scene builder, configurable test sources and result
injector; it is referenced by both test assemblies and is excluded from builds like them
(`UNITY_INCLUDE_TESTS`).

Headless: `Unity -batchmode -nographics -projectPath <project> -runTests -testPlatform EditMode|PlayMode -testResults results.xml`.
