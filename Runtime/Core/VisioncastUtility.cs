using System.Collections.Generic;
using UnityEngine;

namespace GalaxyGourd.Visioncast
{
    public static class VisioncastUtility
    {
        #region BOUNDS

        /// <summary>
        /// Resolves an oriented bounding box for a collider: box colliders use their own transform
        /// axes (respecting rotation/scale), everything else falls back to the world-axis-aligned bounds.
        /// </summary>
        internal static void GetOrientedBounds(
            Collider col,
            out Vector3 center,
            out Vector3 axisX,
            out Vector3 axisY,
            out Vector3 axisZ,
            out Vector3 extents)
        {
            if (col is BoxCollider box)
            {
                Transform t = box.transform;
                center = t.TransformPoint(box.center);

                Vector3 size = box.size;
                Vector3 lossyScale = t.lossyScale;
                extents = new Vector3(size.x * lossyScale.x, size.y * lossyScale.y, size.z * lossyScale.z) * 0.5f;

                axisX = t.right;
                axisY = t.up;
                axisZ = t.forward;
            }
            else
            {
                Bounds bounds = col.bounds;
                center = bounds.center;
                extents = bounds.extents;

                axisX = Vector3.right;
                axisY = Vector3.up;
                axisZ = Vector3.forward;
            }
        }

        #endregion BOUNDS


        #region SAMPLING

        /// <summary>
        /// Appends a grid of sample points across each of the 6 faces of the oriented bounds.
        /// Produces 6 * resolution^2 points; resolution 1 produces the 6 face centers.
        /// </summary>
        internal static void AppendFaceGrid(
            Vector3 center,
            Vector3 axisX,
            Vector3 axisY,
            Vector3 axisZ,
            Vector3 extents,
            int resolution,
            List<Vector3> points)
        {
            int n = Mathf.Max(1, resolution);
            Vector3 ex = axisX * extents.x;
            Vector3 ey = axisY * extents.y;
            Vector3 ez = axisZ * extents.z;

            // Each face is offset along one axis and spanned by the other two
            AppendFace(center + ex, ey, ez, n, points); // +X
            AppendFace(center - ex, ey, ez, n, points); // -X
            AppendFace(center + ey, ex, ez, n, points); // +Y
            AppendFace(center - ey, ex, ez, n, points); // -Y
            AppendFace(center + ez, ex, ey, n, points); // +Z
            AppendFace(center - ez, ex, ey, n, points); // -Z
        }

        /// <summary>
        /// Appends a grid of sample points filling the oriented bounds volume.
        /// Produces resolution^3 points; resolution 1 produces the single center point.
        /// </summary>
        internal static void AppendVolumeGrid(
            Vector3 center,
            Vector3 axisX,
            Vector3 axisY,
            Vector3 axisZ,
            Vector3 extents,
            int resolution,
            List<Vector3> points)
        {
            int n = Mathf.Max(1, resolution);
            Vector3 ex = axisX * extents.x;
            Vector3 ey = axisY * extents.y;
            Vector3 ez = axisZ * extents.z;

            for (int x = 0; x < n; x++)
            {
                float fx = CellCoord(x, n);
                for (int y = 0; y < n; y++)
                {
                    float fy = CellCoord(y, n);
                    for (int z = 0; z < n; z++)
                    {
                        float fz = CellCoord(z, n);
                        points.Add(center + (ex * fx) + (ey * fy) + (ez * fz));
                    }
                }
            }
        }

        #endregion SAMPLING


        #region UTILITY

        /// <summary>
        /// Lays an n x n grid of cell-center points on a face defined by its center and two
        /// half-extent span vectors.
        /// </summary>
        private static void AppendFace(Vector3 faceCenter, Vector3 uAxis, Vector3 vAxis, int n, List<Vector3> points)
        {
            for (int u = 0; u < n; u++)
            {
                float fu = CellCoord(u, n);
                for (int v = 0; v < n; v++)
                {
                    float fv = CellCoord(v, n);
                    points.Add(faceCenter + (uAxis * fu) + (vAxis * fv));
                }
            }
        }

        /// <summary>
        /// Normalized cell-center coordinate in [-1, 1] for cell <paramref name="index"/> of
        /// <paramref name="n"/>. n == 1 returns 0 (exact center).
        /// </summary>
        private static float CellCoord(int index, int n)
        {
            return Mathf.Lerp(-1f, 1f, (index + 0.5f) / n);
        }

        #endregion UTILITY
    }
}
