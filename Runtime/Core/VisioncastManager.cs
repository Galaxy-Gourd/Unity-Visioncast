using System;
using System.Collections.Generic;
using GalaxyGourd.Raycast;
using UnityEngine;

namespace GalaxyGourd.Visioncast
{
    /// <summary>
    /// Manages high-level visioncast system flow.
    /// </summary>
    [DefaultExecutionOrder(-499)]
    public static class VisioncastManager
    {
        #region VARIABLES

        public static Action<List<VisioncastSource>> OnSourceComponentsModified;
        public static Action PreVisioncastTick { get; set; }
        public static Action PostVisioncastTick { get; set; }

        private static Visioncaster _visioncaster;
        private static readonly List<VisioncastSource> _addQueue = new();

        #endregion VARIABLES


        #region INIT

        public static void Setup()
        {
            OnSourceComponentsModified = null;
            _visioncaster = new Visioncaster(RaycastManager.ScheduledRaycaster, VisionBroadphase.Factory());
            
            //
            foreach (VisioncastSource source in _addQueue)
            {
                RegisterVisionSource(source);
            }
            _addQueue.Clear();
        }

        public static void Dispose()
        {
            _visioncaster?.Dispose();
            _addQueue.Clear();
            _visioncaster = null;
        }

        #endregion INIT
        
        
        #region API

        public static void TickVisioncasts(float delta)
        {
            PreVisioncastTick?.Invoke();
            _visioncaster?.Tick(delta);
            PostVisioncastTick?.Invoke();
        }

        /// <summary>
        /// Deferred DoD phase 1: build this cycle's batch and schedule the raycast on worker threads without
        /// waiting. Call at the END of the fixed step (after movement settles). Pair with
        /// <see cref="CompleteVisioncasts"/> at the start of the next step, before Physics.Simulate. Only
        /// active when the pipeline is in DoD mode (<see cref="VisionPipeline.UseDod"/>); no-op otherwise.
        /// </summary>
        public static void ScheduleVisioncasts(float delta)
        {
            PreVisioncastTick?.Invoke();
            _visioncaster?.TickDodSchedule(delta);
        }

        /// <summary>
        /// Deferred DoD phase 2: complete the batch scheduled last step and distribute results. Call at the
        /// START of the fixed step, before any Physics.Simulate or collider movement.
        /// </summary>
        public static void CompleteVisioncasts()
        {
            _visioncaster?.TickDodComplete();
            PostVisioncastTick?.Invoke();
        }

        /// <summary>
        /// Accumulated vision-time (sum of tick deltas, driver-relative — not Unity Time.time). Compare
        /// against a source's <see cref="VisioncastSource.LastUpdatedTime"/> to gauge result staleness.
        /// </summary>
        public static float VisionTime => _visioncaster?.VisionTime ?? 0f;

        /// <summary>
        /// Updates the debug visualization for the visioncast sources
        /// </summary>
        public static void TickVisioncastSourceDebug(float delta)
        {
            // A driver's LateUpdate can outlive Dispose (teardown order between the driver and the
            // system is not guaranteed), so this is null-safe like every other entry point here
            if (_visioncaster == null)
                return;

            foreach (VisioncastSource component in _visioncaster.Components)
            {
                component.TickDebug(delta);
            }
        }

        public static void RegisterVisionSource(VisioncastSource source)
        {
            // Unreliable init ordering can cause components to be added before the visioncaster is initialized, this
            // queue will enable that to still work
            if (_visioncaster == null)
                _addQueue.Add(source);
            else
                _visioncaster?.RegisterComponent(source);
        }
        
        public static void UnregisterVisionSource(VisioncastSource source)
        {
            _visioncaster?.RemoveComponent(source);
        }

        public static void VisionSourceComponentsModified(List<VisioncastSource> sources)
        {
            OnSourceComponentsModified?.Invoke(sources);
        }

        #endregion API


        #region UTILITY

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            _visioncaster?.Dispose();
            _addQueue.Clear();
            _visioncaster = null;
        }

        #endregion UTILITY
    }
}