using System.Collections.Generic;
using UnityEngine;

namespace GalaxyGourd.Visioncast
{
    /// <summary>
    /// Data to hold raw results from Visioncaster system.
    /// </summary>
    public struct DataVisioncastResult
    {
        public List<Collider> Objects;
        /// <summary>
        /// Per-object world-space sample points with a clear line of sight. Populated by the managed path
        /// (debug visualization); the DoD narrowphase leaves this empty and reports counts via
        /// <see cref="VisiblePointCounts"/> instead. Read counts from there, not <c>VisiblePoints[i].Count</c>.
        /// </summary>
        public List<List<Vector3>> VisiblePoints;
        /// <summary>
        /// Number of sample points with a clear line of sight per object (parallel to <see cref="Objects"/>).
        /// The visibility numerator, populated by both the managed and DoD paths.
        /// </summary>
        public List<int> VisiblePointCounts;
        /// <summary>
        /// Total sample points tested per object (parallel to <see cref="Objects"/>). Acts as the
        /// denominator for a visibility fraction: VisiblePointCounts[i] / SampleCounts[i].
        /// </summary>
        public List<int> SampleCounts;
        /// <summary>
        /// Sample points that fall within the source's FOV cone and range per object (parallel to
        /// <see cref="Objects"/>) - "how much of the target is in the beam", independent of occlusion. The
        /// DoD path tests each point individually; the managed path (per-target FOV gate) reports the full
        /// sample count.
        /// </summary>
        public List<int> InConeCounts;
        /// <summary>
        /// Sample points that are BOTH in-cone and unobstructed per object (parallel to <see cref="Objects"/>)
        /// - "how much of the target is actually lit / seen". The system reports the raw counts; the consumer
        /// decides the meaning: LitCounts[i]/SampleCounts[i] = illumination, InConeCounts[i]/SampleCounts[i] =
        /// beam coverage, LitCounts[i]/InConeCounts[i] = unobstructed fraction of what is in the beam.
        /// </summary>
        public List<int> LitCounts;
        public List<float> Distances;
        public List<float> Angles;
    }
}