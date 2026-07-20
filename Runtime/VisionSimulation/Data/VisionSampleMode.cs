namespace GalaxyGourd.Visioncast
{
    /// <summary>
    /// Strategy used to generate the sample points that are line-of-sight tested against a target.
    /// Denser sampling produces a smoother visibility fraction (see <see cref="DataVisionSeenObject.Visibility"/>)
    /// at the cost of more raycasts per target - useful for stealth exposure, wasteful for simple detection.
    /// </summary>
    public enum VisionSampleMode
    {
        /// <summary>
        /// A grid across each of the 6 collider-bounds faces. Sample count scales with resolution^2
        /// per face (6 * resolution^2). Resolution 1 yields the 6 face centers (legacy behavior).
        /// </summary>
        BoundsFaceGrid = 0,

        /// <summary>
        /// A grid filling the collider-bounds volume. Sample count scales with resolution^3.
        /// Resolution 1 yields a single center point.
        /// </summary>
        BoundsVolumeGrid = 1
    }
}
