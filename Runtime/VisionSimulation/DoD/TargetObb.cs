using Unity.Mathematics;

namespace GalaxyGourd.Visioncast
{
    /// <summary>
    /// World-space oriented bounding box of a target, gathered per tick (see <see cref="VisionTargetGather"/>)
    /// and consumed by the Burst narrowphase for angle/closest-point filtering and sample-point generation.
    /// Axes are unit vectors; <see cref="Extents"/> are half-sizes along each axis. Exact for BoxColliders
    /// (transform basis); for other colliders it is the transform-oriented approximation of the world bounds.
    /// </summary>
    internal struct TargetObb
    {
        public float3 Center;
        public float3 AxisX;
        public float3 AxisY;
        public float3 AxisZ;
        public float3 Extents;
    }
}
