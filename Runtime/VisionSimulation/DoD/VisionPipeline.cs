using UnityEngine;

namespace GalaxyGourd.Visioncast
{
    /// <summary>
    /// Selects the per-tick vision computation the Visioncaster uses. Default is the managed broadphase/
    /// narrowphase/distribute path. Call <see cref="UseDod"/> before <see cref="VisioncastManager.Setup"/>
    /// to route the whole tick through the native Burst pipeline (<see cref="VisionDodPipeline"/>) instead.
    /// The DoD pipeline has its own spatial-grid broadphase, so it does not use the <see cref="IBroadphase"/>
    /// selection - <paramref name="cellSize"/> here sets its grid cell size.
    /// </summary>
    public static class VisionPipeline
    {
        internal static bool UseDodNarrowphase;
        internal static float DodCellSize = 20f;

        /// <summary>Route the tick through the native Burst pipeline (set before Setup).</summary>
        public static void UseDod(float cellSize = 20f)
        {
            UseDodNarrowphase = true;
            DodCellSize = Mathf.Max(0.01f, cellSize);
        }

        /// <summary>Revert to the managed pipeline (default).</summary>
        public static void UseManaged()
        {
            UseDodNarrowphase = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            UseDodNarrowphase = false;
            DodCellSize = 20f;
        }
    }
}
