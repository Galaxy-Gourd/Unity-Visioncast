# DoD Deferred Driver (sample)

A self-contained driver for the **deferred data-oriented vision path** — the one that runs the raycast batch
on worker threads instead of blocking the main thread — with **no external scheduler**. No GG.Tick, no custom
PlayerLoop. Drop the component into a scene and it drives everything in explicit order.

Use it to test or showcase the DoD path in isolation. For the full API, see
[`SETUP-DOD.md`](../../SETUP-DOD.md).

## Usage

1. Add **`VisioncastDodDeferredDriver`** to one GameObject in the scene. That's it — it sets manual physics,
   enables the DoD pipeline, and calls `VisioncastManager.Setup()` in `Awake`.
2. Add your vision sources (a `VisioncastSource` subclass) and register your targets with
   `VisionTargetsManifest.Register(collider, actor)`.
3. Press play.

Use **exactly one** driver — never alongside `VisioncastDriver` or your own ticker (double-ticking).

### Inspector

| Field | Meaning |
|---|---|
| **Grid Cell Size** | Spatial grid cell size; set ≈ your typical source `Range`. |
| **Manual Physics** | Take ownership of the physics step (`simulationMode = Script`). Leave on unless something else already drives `Physics.Simulate` at the right moment. |
| **Tick Source Debug** | Drive `VisioncastSourceDebug` visualizers from `LateUpdate`. |

## What it does each fixed step

```
1. CompleteVisioncasts()    // reap LAST step's batch, BEFORE anything touches the scene
2. Physics.Simulate()       // now safe to mutate the physics scene
3. ...your movement...      // other components' FixedUpdate (execution order 0) runs here
4. Physics.SyncTransforms() // push transform writes into the physics scene
5. ScheduleVisioncasts()    // scene settled: kick this step's batch onto worker threads

   the batch then runs across the gap (Update / LateUpdate / render)
   until step 1 of the NEXT fixed step
```

That gap is the whole point: the raycasts execute on workers while the main thread does other work, instead of
blocking on them.

## Why it's two components

The phases must **bracket** everything else's `FixedUpdate` — complete has to run before any collider moves,
schedule has to run after they all have. One `FixedUpdate` can't straddle other components, so the driver runs
at execution order `-10000` (complete + simulate) and auto-creates `VisioncastDodDeferredDriverLate` at
`+10000` (sync + schedule). Your movement scripts, at the default order 0, land correctly between them. The
companion is added at runtime and hidden; you never place it yourself.

## ⚠ The one hard rule

While a batch is in flight it reads the `PhysicsScene` **on worker threads**. Between step 5 and the next step
1, **nothing may simulate physics or move a vision-queried collider** — in particular, don't move queried
colliders from `Update`/`LateUpdate`. Breaking this is a data race: garbage results or a crash.

Keeping collider movement inside the fixed step (where this driver puts it, at step 3) satisfies the rule
automatically.

## Falling back

The driver hard-selects the DoD pipeline. To compare against the managed path, call
`VisionPipeline.UseManaged()` before `Setup()` instead — but note the managed path does **not** use these
deferred entry points; drive it with `RaycastManager.TickRaycasts(delta)` + `VisioncastManager.TickVisioncasts(delta)`.
