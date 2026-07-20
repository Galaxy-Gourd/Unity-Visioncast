using UnityEngine;

namespace GalaxyGourd.Visioncast
{
    /// <summary>
    /// Refined data describing a "seen" target. One entry per TARGET IDENTITY (see
    /// <see cref="VisionTargetsManifest.ResolveTarget"/>): a multi-collider actor is a single entry with its
    /// colliders' observations merged, and a standalone collider is its own identity. Consumers never branch
    /// on actor-vs-collider.
    /// </summary>
    public struct DataVisionSeenObject
    {
        /// <summary>
        /// The target's identity - its registered actor, or the collider itself when standalone. Never null.
        /// Use this to compare/track targets across updates; <see cref="ResultObject"/> may change.
        /// </summary>
        public Component Actor;
        /// <summary>
        /// Representative collider for this target: the most-directly-observed (smallest angle) contributing
        /// collider this update. May differ between updates for a multi-collider actor - key off
        /// <see cref="Actor"/> for identity, use this for positioning (tooltips, markers, hit points).
        /// </summary>
        public Collider ResultObject;
        public bool IsVisible;
        public bool JustBecameVisible;
        public float Distance;
        public float Angle;
        /// <summary>
        /// Number of sample points with a clear line of sight to the object this update
        /// </summary>
        public int VisiblePointCount;
        /// <summary>
        /// Total sample points tested against the object this update
        /// </summary>
        public int SampleCount;
        /// <summary>
        /// Sample points within the source's FOV cone + range ("in the beam"), summed across the target's
        /// colliders. See <see cref="DataVisioncastResult.InConeCounts"/>.
        /// </summary>
        public int InConeCount;
        /// <summary>
        /// Sample points both in-cone and unobstructed ("lit" / seen), summed across the target's colliders.
        /// The consumer forms whatever ratio it needs (illumination, beam coverage, occlusion-in-beam) - the
        /// system does not decide. See <see cref="DataVisioncastResult.LitCounts"/>.
        /// </summary>
        public int LitCount;
        /// <summary>
        /// Fraction of sample points with a clear line of sight, in [0, 1]. Drives stealth exposure -
        /// denser sampling (see <see cref="VisionSampleMode"/>) yields a smoother value.
        /// </summary>
        public float Visibility;
    }
}