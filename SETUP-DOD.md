# Visioncast — DoD Setup

How to run Visioncast with the full data-oriented feature set: the Burst pipeline, the deferred
(off-main-thread) raycast, actor-resolved results, and LOD. See [`PERFORMANCE-DOD.md`](../PERFORMANCE-DOD.md)
for what it buys you and why.

## Requirements

- Unity 6 (developed/validated on **6000.5**; also compiles on 6.3 — the `EntityId` migration is
  version-conditional).
- Packages: `com.unity.burst`, `com.unity.collections`, `com.unity.mathematics`.
- `GG.Raycast` (the raycast utility package) — referenced by the asmdef. The DoD pipeline doesn't route
  through it, but the managed fallback path does.

---

## 1. Turn on the DoD pipeline

**Must be called before `VisioncastManager.Setup()`** — the flag is read when the caster is built.

```csharp
VisionPipeline.UseDod(cellSize: 20f);   // ~ your typical source Range
VisioncastManager.Setup();
```

- `cellSize` ≈ typical vision range. Too small ⇒ more grid cells scanned per source; too large ⇒ more
  distance checks per cell.
- These statics **reset every play session**, so set it every run — put it in the same `Awake` that calls
  `Setup()`.
- DoD has its own internal grid and ignores `VisionBroadphase` entirely. Leave it on the default
  (`UsePhysicsOverlap()`) so you don't allocate a broadphase nothing uses.

## 2. Drive it

Pick **one** driver. Never run two (double-ticking).

### Option A — synchronous (simplest)

Everything happens in one call; the raycast blocks the main thread.

```csharp
void FixedUpdate()
{
    VisioncastManager.TickVisioncasts(Time.fixedDeltaTime);
}
```

> `RaycastManager` is **not** used in DoD mode — the pipeline owns its own raycast batch.

### Option B — deferred (the full suite: raycast off the main thread)

Split the tick so the raycast runs on workers across the inter-tick gap. Costs +1 fixed step of latency.

```csharp
// START of the fixed step — BEFORE anything simulates physics or moves colliders
VisioncastManager.CompleteVisioncasts();

    // ... your physics step, then movement ...

// END of the fixed step — after movement has settled
VisioncastManager.ScheduleVisioncasts(Time.fixedDeltaTime);
```

With `GG.Tick`, that's just group ordering:

```
[ VisionComplete → TickPhysics → …movement… → VisionSchedule ]
```

> **No scheduler? Use the sample.** The `DoD Deferred Driver` sample (`Samples~/DoDDeferred`) is a drop-in
> component that sets manual physics and calls all of this in the correct order with no GG.Tick — good for a
> test scene or a showcase. Import it from the package's Samples tab.

> ⚠️ **The one hard rule.** The raycast job reads the `PhysicsScene` on worker threads. **Nothing may
> simulate physics or move a queried collider between `ScheduleVisioncasts` and the next
> `CompleteVisioncasts`.** Break this and you get a data race (garbage results or a crash). The ordering
> above is safe because colliders only move inside the fixed step, and the batch always completes before the
> next simulate.

## 3. Register targets

Only registered colliders are ever considered — that's what keeps the broadphase cheap.

```csharp
[SerializeField] private Collider _collider;

void OnEnable()  => VisionTargetsManifest.Register(_collider, this);  // `this` = the owning actor
void OnDisable() => VisionTargetsManifest.Unregister(_collider);
```

- **Multi-collider actor:** register every collider against the **same actor** — results collapse to one
  entry per actor.
- **Single collider:** `Register(col)` with no actor. It resolves to itself, so results are still one entry
  per target. You never branch on "actor or collider?".
- To be notified when seen, implement `IVisibleObject` on the actor (or the collider's object if standalone).

## 4. Make a vision source

Subclass one of these and drop it on a GameObject:

| Base | Use for |
|---|---|
| `VisioncastSourceSimple` | fire-and-forget detection (`IVisibleObject.Seen`) |
| `VisioncastSourceFiltered` | seen/lost transitions, visibility %, key target |
| `VisioncastSourceCompound` | merge several child sources into one output (e.g. multi-light stealth) |

```csharp
public class GuardVision : VisioncastSourceFiltered
{
    protected override void PostVisionFilter()
    {
        foreach (DataVisionSeenObject t in VisionTargets)
        {
            // t.Actor      -> identity (never null) — key off this
            // t.ResultObject -> representative collider (for tooltips/markers)
            // t.Visibility -> 0..1 (unobstructed fraction; occlusion, cone-agnostic)
            // t.SampleCount / t.InConeCount / t.LitCount -> raw counts; YOU pick the ratio (see below)
        }
        // NewlySeenObjects / NewlyLostObjects / TargetedObject also available
    }
}
```

Config comes from a `DataConfigVisioncastSource` asset (layers, range, FOV, sample mode/resolution), or
override the properties directly.

> **Overriding config in code?** A source built entirely in code (no `DataConfigVisioncastSource` asset) that
> overrides `Range` / `FieldOfView` / layers **must also override `SampleMode` and `SampleResolution`** — the
> filtered/simple bases read those two from the (now null) config, so leaving them throws a `NullReferenceException`
> every tick and silently aborts all vision. See Gotchas.

> **Read counts, never `VisiblePoints[i].Count`.** The point vectors are debug-only and are empty unless a
> visualizer is attached.

> **Samples.** `Guard Patrol` drives a Patrol → Alert → Search state machine off `NewlySeenObjects` /
> `NewlyLostObjects`; `Interaction Focus` uses `TargetedObject` for a "look to interact" focus highlight.

### Visibility, coverage, occlusion — you choose

Each result carries three raw counts per target; the system reports facts and lets the consumer decide what
they mean. Per sample point it knows two things — **in-cone** (inside the FOV cone and range) and **reached**
(its ray hit the target, i.e. unobstructed) — and reports:

| Count | Meaning |
|---|---|
| `SampleCount` | total points tested (the denominator) |
| `InConeCount` | points inside the beam |
| `LitCount` | points that are **both** in-cone and reached |

Form whatever ratio the gameplay needs:

- **Illumination / "how lit"** = `LitCount / SampleCount` — ramps as the target enters the beam (stealth).
- **Beam coverage** = `InConeCount / SampleCount` — how much of the target is in the cone, ignoring occlusion.
- **Occlusion within the beam** = `LitCount / InConeCount` — of what's in the beam, how much isn't blocked.
- **Legacy occlusion** = `DataVisionSeenObject.Visibility` (= `VisiblePointCount / SampleCount`) — unobstructed
  fraction, cone-agnostic. Unchanged; fine for simple "can I see them" detection.

These are available on `DataVisioncastResult` (per collider, via a base/simple source), on
`DataVisionSeenObject` (per target identity, filtered), and on `DataVisionSeenTarget` (compound). For
**per-body-part** weighting (a lit head worth more than a lit hand), register each part **standalone** so it
stays a separate entry, read each part's `LitCount / SampleCount`, and combine with your own weights — the
system never sees a weight.

For **multiple lights**, a `VisioncastSourceCompound` aggregates coverage across its child sources per
`Aggregation` (Max / Sum / Average): `compound.TryGetCoverage(collider, out float lit)` gives the combined
"how lit across all lights" in one call (`TryGetVisibility` remains the cone-agnostic occlusion version). The
`Stealth Exposure` sample shows the whole pattern — multi-collider weighted player, two lights via a compound,
a wall occluder, and coverage that drops in the wall's shadow even where the target is in-cone.

## 5. LOD / relevance (optional, big win at scale)

Cuts how many sources cast per tick. Multiplies on top of the DoD gains.

```csharp
// smaller = more relevant. 0 = always important.
VisionRelevance.Provider = src => Vector3.Distance(_player.position, src.Position);

VisionLOD.Tiers = new[]
{
    new VisionLODTier { MaxDistance = 30f,  Cadence = 1,  SampleResolution = 0 }, // near: every tick, full res
    new VisionLODTier { MaxDistance = 80f,  Cadence = 4,  SampleResolution = 1 }, // mid
    new VisionLODTier { MaxDistance = 200f, Cadence = 16, SampleResolution = 1 }, // far
    // beyond the last tier => dormant (never casts)
};
```

## 6. Debug visualization (optional)

Add a **`VisioncastSourceDebug`** component next to the source. Only sources with it attached pay for the
visible-point vectors — everything else stays on the fast path.

---

## Minimal complete example

```csharp
// --- Driver (one per scene) -------------------------------------------------
[DefaultExecutionOrder(-500)]
public class VisionDriver : MonoBehaviour
{
    void Awake()
    {
        VisionPipeline.UseDod(20f);   // before Setup!
        VisioncastManager.Setup();
    }

    void OnDestroy() => VisioncastManager.Dispose();

    void FixedUpdate() => VisioncastManager.TickVisioncasts(Time.fixedDeltaTime); // Option A
}

// --- Target -----------------------------------------------------------------
[RequireComponent(typeof(Collider))]
public class VisibleProp : MonoBehaviour, IVisibleObject
{
    private Collider _col;
    void Awake()     => _col = GetComponent<Collider>();
    void OnEnable()  => VisionTargetsManifest.Register(_col, this);
    void OnDisable() => VisionTargetsManifest.Unregister(_col);
    public void Seen(VisioncastSource source) { /* ... */ }
}

// --- Source -----------------------------------------------------------------
public class GuardVision : VisioncastSourceFiltered
{
    protected override void PostVisionFilter()
    {
        if (TargetedObject) { /* the thing most directly in view */ }
    }
}
```

---

## Gotchas

1. **`UseDod()` must precede `Setup()`**, and the statics reset each play session.
2. **Startup choice, not a live toggle.** Switching mid-session needs `Dispose()` + `Setup()`, which drops
   registered sources (their `OnEnable` won't re-fire). Re-register manually if you really need it:
   ```csharp
   foreach (var s in FindObjectsByType<VisioncastSource>(FindObjectsSortMode.None))
       VisioncastManager.RegisterVisionSource(s);
   ```
3. **One driver only.** Don't run `VisioncastDriver` alongside your own ticker.
4. **Deferred mode: respect the physics window** (see §2 Option B).
5. **Results are per target identity.** A multi-collider actor is one entry — but it still costs N× the
   raycasts (one set of samples per collider). Collapsing is presentation, not work reduction.
6. **Falling back:** `VisionPipeline.UseManaged()` restores the managed path. Worth keeping reachable as a
   kill-switch. Measured crossover: managed only wins at **1–2 sources** (DoD's fixed cost is ~0.11 ms for
   2000 targets and is repaid by the ~3rd source). The crossover scales with target count — roughly
   `sources ≈ targets/500` — so unless you have single-digit sources, use DoD.
7. **Code-built sources must override `SampleMode` / `SampleResolution`.** `VisioncastSourceFiltered` and
   `VisioncastSourceSimple` read those from a serialized `DataConfigVisioncastSource`. A source configured
   entirely in code never assigns one, so a bare subclass dereferences null in `SampleMode` **every tick** —
   a `NullReferenceException` that aborts the whole vision tick before any results are delivered (looks like a
   total detection failure). Override both (e.g. `=> VisionSampleMode.BoundsFaceGrid` and `=> 1`).

## Switching back

```csharp
VisionPipeline.UseManaged();            // managed pipeline
VisionBroadphase.UsePhysicsOverlap();   // default broadphase (or UseGrid / UseBatchedOverlap)
VisioncastManager.Setup();
// drive with: RaycastManager.TickRaycasts(delta); VisioncastManager.TickVisioncasts(delta);
```
