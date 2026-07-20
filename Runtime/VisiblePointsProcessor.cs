using System.Collections.Generic;
using UnityEngine;

namespace GalaxyGourd.Visioncast
{
    internal static class VisiblePointsProcessor
    {
        #region PROCESS

        /// <summary>
        /// Appends the line-of-sight sample points for a collider to <paramref name="points"/>,
        /// using the given sampling strategy and density. Writing into a caller-owned (pooled) list
        /// keeps the narrowphase off the per-frame allocation path.
        /// </summary>
        internal static void Process(Collider collider, VisionSampleMode mode, int resolution, List<Vector3> points)
        {
            VisioncastUtility.GetOrientedBounds(
                collider,
                out Vector3 center,
                out Vector3 axisX,
                out Vector3 axisY,
                out Vector3 axisZ,
                out Vector3 extents);

            switch (mode)
            {
                case VisionSampleMode.BoundsVolumeGrid:
                    VisioncastUtility.AppendVolumeGrid(center, axisX, axisY, axisZ, extents, resolution, points);
                    break;
                case VisionSampleMode.BoundsFaceGrid:
                default:
                    VisioncastUtility.AppendFaceGrid(center, axisX, axisY, axisZ, extents, resolution, points);
                    break;
            }
        }

        #endregion PROCESS
    }
}
