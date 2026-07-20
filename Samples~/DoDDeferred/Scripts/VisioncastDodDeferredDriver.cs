using UnityEngine;

namespace GalaxyGourd.Visioncast.Samples
{
    /// <summary>
    /// Drop-in driver for the DEFERRED DoD vision path with NO external scheduler — no GG.Tick, no custom
    /// PlayerLoop. It takes ownership of the physics step and calls the vision phases in explicit order, so a
    /// scene can test or showcase the off-main-thread raycast with nothing but this component.
    ///
    /// <b>The order it enforces, every fixed step:</b>
    /// <code>
    ///   1. CompleteVisioncasts()   // reap LAST step's batch, BEFORE anything touches the scene
    ///   2. Physics.Simulate()      // now safe to mutate the physics scene
    ///   3. ...your movement...     // other components' FixedUpdate runs here (execution order 0)
    ///   4. Physics.SyncTransforms()// push transform writes into the physics scene
    ///   5. ScheduleVisioncasts()   // scene settled: kick this step's batch onto worker threads
    ///   // the batch runs across the gap (Update/LateUpdate/render) until step 1 of the NEXT fixed step
    /// </code>
    ///
    /// <b>Why two components?</b> The phases must BRACKET everything else's FixedUpdate: complete must run
    /// before any collider moves, schedule must run after they all have. A single FixedUpdate cannot straddle
    /// other components, so this driver runs at a very early execution order and auto-creates
    /// <see cref="VisioncastDodDeferredDriverLate"/> (very late order) for the schedule phase. Your movement
    /// scripts, at the default order 0, land correctly between them.
    ///
    /// <b>⚠ The one hard rule.</b> The raycast batch reads the PhysicsScene on worker threads while in flight.
    /// Nothing may simulate physics or move a vision-queried collider between step 5 and the next step 1 —
    /// i.e. never move a queried collider from Update/LateUpdate. Break this and you get a data race.
    ///
    /// Use EXACTLY ONE driver in a scene (never alongside VisioncastDriver or your own ticker).
    /// </summary>
    [DefaultExecutionOrder(EarlyOrder)]
    [AddComponentMenu("Visioncast/Samples/Visioncast DoD Deferred Driver")]
    public class VisioncastDodDeferredDriver : MonoBehaviour
    {
        /// <summary>Runs before default-order components, so nothing has moved a collider yet this step.</summary>
        public const int EarlyOrder = -10000;
        /// <summary>Runs after default-order components, once this step's movement has settled.</summary>
        public const int LateOrder = 10000;

        #region VARIABLES

        [Header("Pipeline")]
        [Tooltip("Spatial grid cell size — set roughly to your typical source Range. Too small scans more " +
                 "cells per source; too large does more distance checks per cell.")]
        [SerializeField] private float _gridCellSize = 20f;

        [Header("Physics")]
        [Tooltip("Take ownership of the physics step (simulationMode = Script). The deferred path needs the " +
                 "simulate to sit at a known point so the in-flight raycast batch never spans it. Disable only " +
                 "if something else already drives Physics.Simulate at the right moment.")]
        [SerializeField] private bool _manualPhysics = true;

        [Header("Debug")]
        [Tooltip("Drive VisioncastSourceDebug visualizers from LateUpdate.")]
        [SerializeField] private bool _tickSourceDebug = true;

        private SimulationMode _previousSimulationMode;

        #endregion VARIABLES


        #region LIFECYCLE

        private void Awake()
        {
            _previousSimulationMode = Physics.simulationMode;
            if (_manualPhysics)
                Physics.simulationMode = SimulationMode.Script;

            // Selection MUST precede Setup — the caster reads it when it is built.
            VisionPipeline.UseDod(_gridCellSize);
            // DoD carries its own grid and ignores IBroadphase; leave the default so no unused one is built.
            VisionBroadphase.UsePhysicsOverlap();

            VisioncastManager.Setup();

            // Schedule phase must run after every default-order FixedUpdate (see class docs).
            var late = gameObject.AddComponent<VisioncastDodDeferredDriverLate>();
            late.hideFlags = HideFlags.HideInInspector;
            late.Bind(this);
        }

        private void OnDestroy()
        {
            VisioncastManager.Dispose();   // also completes any batch still in flight
            if (_manualPhysics)
                Physics.simulationMode = _previousSimulationMode;
        }

        #endregion LIFECYCLE


        #region TICK

        /// <summary>Phase 1 (earliest): reap the previous step's batch, then step physics.</summary>
        private void FixedUpdate()
        {
            // Deliver last step's results BEFORE anything is allowed to move.
            VisioncastManager.CompleteVisioncasts();

            // Safe to mutate the scene now that no batch is in flight.
            if (_manualPhysics)
                Physics.Simulate(Time.fixedDeltaTime);

            // ...default-order FixedUpdate (your movement) runs after this, then LateOrder calls SchedulePhase.
        }

        /// <summary>Phase 2 (latest), invoked by <see cref="VisioncastDodDeferredDriverLate"/>.</summary>
        internal void SchedulePhase()
        {
            // Movement wrote transforms; push them into the physics scene so the batch queries settled geometry.
            Physics.SyncTransforms();

            // Kick this step's batch; it runs on workers across the gap until the next CompleteVisioncasts.
            VisioncastManager.ScheduleVisioncasts(Time.fixedDeltaTime);
        }

        private void LateUpdate()
        {
            if (_tickSourceDebug)
                VisioncastManager.TickVisioncastSourceDebug(Time.deltaTime);
        }

        #endregion TICK
    }


    /// <summary>
    /// Schedule-phase half of <see cref="VisioncastDodDeferredDriver"/>, added automatically. Exists only so
    /// the phase can run at a late execution order — after every default-order FixedUpdate has moved its
    /// colliders — which a single component cannot do. Not meant to be added by hand.
    /// </summary>
    [DefaultExecutionOrder(VisioncastDodDeferredDriver.LateOrder)]
    public class VisioncastDodDeferredDriverLate : MonoBehaviour
    {
        private VisioncastDodDeferredDriver _driver;

        internal void Bind(VisioncastDodDeferredDriver driver)
        {
            _driver = driver;
        }

        private void FixedUpdate()
        {
            if (_driver)
                _driver.SchedulePhase();
        }
    }
}
