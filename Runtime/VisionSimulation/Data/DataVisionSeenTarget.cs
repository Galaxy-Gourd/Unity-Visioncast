using UnityEngine;

namespace GalaxyGourd.Visioncast
{
    /// <summary>
    /// A combined vision target produced by <see cref="VisioncastSourceCompound"/>: one target identity
    /// with its visibility aggregated across every collider it owns and every child source that sees it.
    /// </summary>
    public struct DataVisionSeenTarget
    {
        /// <summary>
        /// The target's identity (see <see cref="VisionTargetsManifest.ResolveTarget"/>) - its registered
        /// actor, or the collider itself when standalone. Never null. Key off this to track targets.
        /// </summary>
        public Component Actor;
        /// <summary>
        /// A representative collider for the target - the most-visible contributing collider this update.
        /// May change between updates; use <see cref="Actor"/> for identity, this for positioning.
        /// </summary>
        public Collider Collider;
        public bool IsVisible;
        public bool JustBecameVisible;
        /// <summary>Distance of the closest contributing observation.</summary>
        public float Distance;
        /// <summary>Angle of the most-direct contributing observation.</summary>
        public float Angle;
        /// <summary>Sample points with a clear line of sight, summed across contributions.</summary>
        public int VisiblePointCount;
        /// <summary>Total sample points tested, summed across contributions.</summary>
        public int SampleCount;
        /// <summary>Sample points within a source's FOV cone + range ("in the beam"), summed across contributions.</summary>
        public int InConeCount;
        /// <summary>Sample points both in-cone and unobstructed ("lit" / seen), summed across contributions.</summary>
        public int LitCount;
        /// <summary>
        /// Aggregated visibility in [0, 1] (reached / sample, cone-agnostic occlusion) across the child
        /// sources, combined per <see cref="VisibilityAggregation"/>.
        /// </summary>
        public float Visibility;
        /// <summary>
        /// Aggregated coverage in [0, 1] (lit / sample - in-cone AND unobstructed) across the child sources,
        /// combined per <see cref="VisibilityAggregation"/>. This is the "how lit across all lights" signal
        /// for stealth; <see cref="Visibility"/> ignores the cone.
        /// </summary>
        public float Coverage;
        /// <summary>
        /// Vision-time of the most recent contributing source update (see
        /// <see cref="VisioncastManager.VisionTime"/>). Children update on different cadences under LOD,
        /// so this is the freshest observation folded into the target.
        /// </summary>
        public float LastUpdatedTime;
    }
}
