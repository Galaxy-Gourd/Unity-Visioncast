using System;
using System.Collections.Generic;
using UnityEngine;

namespace GalaxyGourd.Visioncast
{
    [RequireComponent(typeof(MeshFilter))]
    [RequireComponent(typeof(MeshRenderer))]
    public class VisionCone : MonoBehaviour
    {
        #region VARIABLES

        [Range(3, 60)]
        [SerializeField] private int _meshDetail = 16;

        // Subdivisions along the straight lateral side of the cone.
        [Range(1, 12)]
        [SerializeField] private int _sideRings = 3;

        // Subdivisions across the curved front cap (only used when
        // _sphericalCap is true). Higher = smoother dome.
        [Range(1, 12)]
        [SerializeField] private int _capRings = 3;

        // If true the far end is a spherical cap (every surface point is
        // exactly Range from the apex). If false, a flat disc closes it.
        [SerializeField] private bool _sphericalCap = true;

        // VisioncastSource.FieldOfView is authored as a HALF-angle (axis -> rim)
        // to match the detection test, so this defaults to false. Enable it only
        // for a source that instead supplies the FULL cone angle.
        [SerializeField] private bool _fieldOfViewIsFullAngle;

        private MeshRenderer _renderer;
        private MeshFilter _filter;
        private float _cacheRange;
        private float _cacheFoV;
        private int _cacheDetail;
        private int _cacheSideRings;
        private int _cacheCapRings;
        private bool _cacheSpherical;
        private bool _cacheFoVIsFull;
        private bool _hasCache;
        private Mesh _mesh;
        private bool _awake;

        #endregion VARIABLES


        #region INITIALIZATION

        private void Awake()
        {
            _awake = true;
            _filter = GetComponent<MeshFilter>();
            _renderer = GetComponent<MeshRenderer>();
        }
        
        public void Toggle(bool on)
        {
            _renderer.enabled = on;
        }

        #endregion INITIALIZATION
        

        #region CONE

        public void CalculateCone(VisioncastSource source)
        {
            if (!_awake)
                return;
            
            // Only rebuild if any input that affects the mesh has changed.
            // This must include EVERY serialized field the mesh depends on,
            // otherwise tweaking them at runtime silently does nothing.
            bool unchanged =
                _hasCache &&
                Math.Abs(_cacheRange - source.Range) < Mathf.Epsilon &&
                Math.Abs(_cacheFoV - source.FieldOfView) < Mathf.Epsilon &&
                _cacheDetail == _meshDetail &&
                _cacheSideRings == _sideRings &&
                _cacheCapRings == _capRings &&
                _cacheSpherical == _sphericalCap &&
                _cacheFoVIsFull == _fieldOfViewIsFullAngle;

            if (unchanged)
                return;

            BuildConeMesh(source);
            
            // Set caches
            _cacheRange = source.Range;
            _cacheFoV = source.FieldOfView;
            _cacheDetail = _meshDetail;
            _cacheSideRings = _sideRings;
            _cacheCapRings = _capRings;
            _cacheSpherical = _sphericalCap;
            _cacheFoVIsFull = _fieldOfViewIsFullAngle;
            _hasCache = true;
        }

        /// <summary>
        /// Builds a line-of-sight cone as TWO distinct surfaces, which is the
        /// fix for the persistent inversion:
        ///
        ///   1. The LATERAL SIDE  - straight lines from the apex out to the
        ///      rim circle. Ring depth and radius scale together with t.
        ///   2. The FRONT CAP     - either a curved spherical cap (every point
        ///      exactly Range from the apex) sweeping from the rim to the
        ///      forward tip, or a flat disc.
        ///
        /// Every earlier version used the spherical-cap equations
        /// (depth = Range*cos, radius = Range*sin) for the ENTIRE cone body
        /// and fanned the apex into that. That builds only the curved front
        /// face and treats it as the whole cone, so the apex connected to a
        /// near-axis point at almost full Range while the wide rim was pulled
        /// back toward the apex - an inside-out funnel. Separating the two
        /// surfaces removes the inversion entirely.
        ///
        /// Geometry is built in WORLD space from source.Position / .Heading
        /// (both world space per VisioncastSource), then converted into this
        /// mesh object's local space in one consistent final pass.
        /// </summary>
        private void BuildConeMesh(VisioncastSource source)
        {
            int radial = Mathf.Max(3, _meshDetail);
            int sideRings = Mathf.Max(1, _sideRings);
            int capRings = Mathf.Max(1, _capRings);
            float range = Mathf.Max(0.001f, source.Range);

            float fov = source.FieldOfView;
            float halfAngleDeg = _fieldOfViewIsFullAngle ? fov * 0.5f : fov;
            halfAngleDeg = Mathf.Clamp(halfAngleDeg, 0.01f, 89.9f);
            float halfAngleRad = halfAngleDeg * Mathf.Deg2Rad;

            // World-space cone frame straight from the source.
            Vector3 apexWS = source.Position;
            Vector3 axisWS = source.Heading.sqrMagnitude > 1e-6f
                ? source.Heading.normalized
                : Vector3.forward;

            // Stable perpendicular basis for the circular cross-section.
            Vector3 rightWS = Vector3.Cross(axisWS, Vector3.up);
            if (rightWS.sqrMagnitude < 1e-6f)
                rightWS = Vector3.Cross(axisWS, Vector3.right);
            rightWS.Normalize();
            Vector3 upWS = Vector3.Cross(rightWS, axisWS).normalized;

            // Rim circle: where the lateral side meets the front cap.
            float rimDepth = range * Mathf.Cos(halfAngleRad);
            float rimRadius = range * Mathf.Sin(halfAngleRad);

            var worldVerts = new List<Vector3>();
            var triangles = new List<int>();

            // Local helper: add a ring of `radial` verts at (depth, radius),
            // return the index of the ring's first vertex.
            int AddRing(float depth, float radius)
            {
                int start = worldVerts.Count;
                Vector3 center = apexWS + axisWS * depth;
                for (int s = 0; s < radial; s++)
                {
                    float a = (2f * Mathf.PI / radial) * s;
                    Vector3 off = (rightWS * Mathf.Cos(a) + upWS * Mathf.Sin(a)) * radius;
                    worldVerts.Add(center + off);
                }
                return start;
            }

            void StripBetween(int innerStart, int outerStart)
            {
                for (int s = 0; s < radial; s++)
                {
                    int sNext = (s + 1) % radial;
                    int i0 = innerStart + s;
                    int i1 = innerStart + sNext;
                    int o0 = outerStart + s;
                    int o1 = outerStart + sNext;

                    triangles.Add(i0); triangles.Add(o0); triangles.Add(o1);
                    triangles.Add(i0); triangles.Add(o1); triangles.Add(i1);
                }
            }

            // Apex.
            int apexIndex = worldVerts.Count;
            worldVerts.Add(apexWS);

            // --- 1. Lateral side: apex -> rim, depth & radius scale together.
            int prevRing = -1;
            for (int r = 1; r <= sideRings; r++)
            {
                float t = r / (float)sideRings; // (0,1]
                int ring = AddRing(rimDepth * t, rimRadius * t);

                if (r == 1)
                {
                    // Apex fan into the first (narrowest) side ring.
                    for (int s = 0; s < radial; s++)
                    {
                        int a = ring + s;
                        int b = ring + (s + 1) % radial;
                        triangles.Add(apexIndex);
                        triangles.Add(a);
                        triangles.Add(b);
                    }
                }
                else
                {
                    StripBetween(prevRing, ring);
                }
                prevRing = ring;
            }
            // prevRing is now the rim ring.

            // --- 2. Front cap.
            if (_sphericalCap)
            {
                // Sweep arc-angle phi from halfAngle (rim) down to 0 (tip).
                // Each ring sits on the sphere of radius=Range about the apex.
                for (int r = 1; r <= capRings; r++)
                {
                    float t = r / (float)capRings;          // (0,1]
                    float phi = halfAngleRad * (1f - t);    // halfAngle -> 0
                    float depth = range * Mathf.Cos(phi);
                    float radius = range * Mathf.Sin(phi);

                    if (r == capRings)
                    {
                        // Final ring degenerates to the forward tip point.
                        int tipIndex = worldVerts.Count;
                        worldVerts.Add(apexWS + axisWS * range);
                        for (int s = 0; s < radial; s++)
                        {
                            int a = prevRing + s;
                            int b = prevRing + (s + 1) % radial;
                            triangles.Add(tipIndex);
                            triangles.Add(b);
                            triangles.Add(a);
                        }
                    }
                    else
                    {
                        int ring = AddRing(depth, radius);
                        StripBetween(prevRing, ring);
                        prevRing = ring;
                    }
                }
            }
            else
            {
                // Flat disc cap: fan the rim to a single center point.
                int capCenter = worldVerts.Count;
                worldVerts.Add(apexWS + axisWS * rimDepth);
                for (int s = 0; s < radial; s++)
                {
                    int a = prevRing + s;
                    int b = prevRing + (s + 1) % radial;
                    triangles.Add(capCenter);
                    triangles.Add(b);
                    triangles.Add(a);
                }
            }

            // Convert all world-space verts into this mesh object's local
            // space exactly once - the single consistent space change.
            var vertices = new List<Vector3>(worldVerts.Count);
            for (int i = 0; i < worldVerts.Count; i++)
                vertices.Add(transform.InverseTransformPoint(worldVerts[i]));

            // --- Commit to mesh ---
            if (_mesh == null)
            {
                _mesh = new Mesh { name = "visioncone_mesh" };
            }
            else
            {
                _mesh.Clear();
            }

            _mesh.SetVertices(vertices);
            _mesh.SetTriangles(triangles, 0);
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();

            _filter.mesh = _mesh;
        }
        
        #endregion CONE
    }
}