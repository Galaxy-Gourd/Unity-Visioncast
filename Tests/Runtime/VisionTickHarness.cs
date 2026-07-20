using GalaxyGourd.Raycast;
using UnityEngine;

namespace GalaxyGourd.Visioncast.Tests
{
    /// <summary>
    /// Drives the vision system the way <see cref="VisioncastDriver"/> does, but synchronously and on
    /// demand so play mode fixtures never depend on frame timing.
    ///
    /// A cycle is (vision tick -> raycast tick). Registration is resolved at the END of a cycle, so a
    /// source registered this frame first casts on the NEXT cycle - hence <see cref="CONST_WarmupCycles"/>.
    /// </summary>
    internal static class VisionTickHarness
    {
        /// <summary>Cycles needed before a freshly registered source has delivered its first results.</summary>
        public const int CONST_WarmupCycles = 2;

        public const float CONST_Delta = 0.02f;

        /// <summary>Runs <paramref name="cycles"/> full vision + raycast cycles.</summary>
        public static void Run(int cycles = 1)
        {
            for (int i = 0; i < cycles; i++)
            {
                Physics.SyncTransforms();
                VisioncastManager.TickVisioncasts(CONST_Delta);
                RaycastManager.TickRaycasts(CONST_Delta);
            }
        }

        /// <summary>Runs enough cycles for newly registered sources to have produced results.</summary>
        public static void Warmup()
        {
            Run(CONST_WarmupCycles);
        }

        /// <summary>
        /// Runs <paramref name="cycles"/> cycles of the DEFERRED DoD path: complete last cycle's batch at
        /// the top of the step, then schedule this cycle's at the end - the ordering a game's own manager
        /// uses to hide the raycast across the physics window.
        /// </summary>
        public static void RunDeferred(int cycles = 1)
        {
            for (int i = 0; i < cycles; i++)
            {
                Physics.SyncTransforms();
                VisioncastManager.CompleteVisioncasts();
                VisioncastManager.ScheduleVisioncasts(CONST_Delta);
            }
        }
    }
}
