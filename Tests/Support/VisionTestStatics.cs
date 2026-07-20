using System.Collections.Generic;
using UnityEngine;

namespace GalaxyGourd.Visioncast.Tests
{
    /// <summary>
    /// The package keeps system-wide policy in statics that only reset on
    /// <see cref="RuntimeInitializeLoadType.SubsystemRegistration"/> - which never happens between edit
    /// mode tests, and only once per play mode run. Fixtures call <see cref="ResetAll"/> in setup AND
    /// teardown so a test can never inherit (or leak) pipeline mode, LOD tiers or a relevance provider.
    /// </summary>
    public static class VisionTestStatics
    {
        public static void ResetAll()
        {
            VisioncastManager.Dispose();
            VisioncastManager.OnSourceComponentsModified = null;
            VisioncastManager.PreVisioncastTick = null;
            VisioncastManager.PostVisioncastTick = null;

            VisionPipeline.UseManaged();
            VisionPipeline.DodCellSize = 20f;
            VisionBroadphase.UsePhysicsOverlap();

            VisionLOD.Tiers = new[]
            {
                new VisionLODTier { MaxDistance = float.PositiveInfinity, Cadence = 1 }
            };
            VisionLOD.TierHysteresis = 1f;

            VisionRelevance.Provider = null;
        }
    }

    /// <summary>
    /// Writes results straight into a source's back buffer and delivers them, driving the consumer half
    /// of the system (filtering, compound aggregation, transitions) with no physics or scheduling in the
    /// way. Mirrors exactly what the pipelines write: parallel per-object lists plus the delivery flip.
    /// </summary>
    public static class VisionResultInjector
    {
        /// <summary>One per-collider observation, as a pipeline would report it.</summary>
        public struct Observation
        {
            public Collider Collider;
            public int VisiblePoints;
            public int SampleCount;
            public int InConeCount;
            public int LitCount;
            public float Distance;
            public float Angle;

            /// <summary>Observation with all sample points reached (fully visible).</summary>
            public static Observation Visible(Collider col, float distance = 5f, float angle = 10f, int samples = 6)
            {
                return new Observation
                {
                    Collider = col,
                    VisiblePoints = samples,
                    SampleCount = samples,
                    InConeCount = samples,
                    LitCount = samples,
                    Distance = distance,
                    Angle = angle
                };
            }

            /// <summary>Observation with no sample point reached (in range, fully occluded).</summary>
            public static Observation Occluded(Collider col, float distance = 5f, float angle = 10f, int samples = 6)
            {
                return new Observation
                {
                    Collider = col,
                    VisiblePoints = 0,
                    SampleCount = samples,
                    InConeCount = samples,
                    LitCount = 0,
                    Distance = distance,
                    Angle = angle
                };
            }

            /// <summary>Observation with a partial fraction of sample points reached.</summary>
            public static Observation Partial(
                Collider col,
                int visiblePoints,
                int samples,
                float distance = 5f,
                float angle = 10f,
                int inCone = -1,
                int lit = -1)
            {
                return new Observation
                {
                    Collider = col,
                    VisiblePoints = visiblePoints,
                    SampleCount = samples,
                    InConeCount = inCone < 0 ? samples : inCone,
                    LitCount = lit < 0 ? visiblePoints : lit,
                    Distance = distance,
                    Angle = angle
                };
            }
        }

        /// <summary>Fills the source's back buffer with <paramref name="observations"/> and delivers it.</summary>
        public static void Deliver(VisioncastSource source, float visionTime, params Observation[] observations)
        {
            Deliver(source, visionTime, (IReadOnlyList<Observation>)observations);
        }

        public static void Deliver(VisioncastSource source, float visionTime, IReadOnlyList<Observation> observations)
        {
            DataVisioncastResult buffer = source.ResultBackBuffer;

            buffer.Objects.Clear();
            buffer.VisiblePoints.Clear();
            buffer.VisiblePointCounts.Clear();
            buffer.SampleCounts.Clear();
            buffer.InConeCounts.Clear();
            buffer.LitCounts.Clear();
            buffer.Distances.Clear();
            buffer.Angles.Clear();

            for (int i = 0; i < observations.Count; i++)
            {
                Observation o = observations[i];
                buffer.Objects.Add(o.Collider);
                buffer.VisiblePointCounts.Add(o.VisiblePoints);
                buffer.SampleCounts.Add(o.SampleCount);
                buffer.InConeCounts.Add(o.InConeCount);
                buffer.LitCounts.Add(o.LitCount);
                buffer.Distances.Add(o.Distance);
                buffer.Angles.Add(o.Angle);
            }

            source.DeliverResults(visionTime);
        }
    }
}
