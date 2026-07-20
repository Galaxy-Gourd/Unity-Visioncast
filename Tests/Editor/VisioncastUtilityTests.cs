using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace GalaxyGourd.Visioncast.Tests
{
    /// <summary>
    /// The sample-point generator and the oriented-bounds resolver. These define how many raycasts a
    /// target costs and where they are aimed, so both the counts and the point layout are pinned here.
    /// </summary>
    [TestFixture]
    public class VisioncastUtilityTests
    {
        #region FIXTURE

        private VisionTestScene _scene;
        private List<Vector3> _points;

        [SetUp]
        public void SetUp()
        {
            _scene = new VisionTestScene();
            _points = new List<Vector3>();
        }

        [TearDown]
        public void TearDown()
        {
            _scene.Dispose();
        }

        private static void AssertVector(Vector3 expected, Vector3 actual, string message = "")
        {
            Assert.AreEqual(expected.x, actual.x, 1e-3f, $"{message} (x)");
            Assert.AreEqual(expected.y, actual.y, 1e-3f, $"{message} (y)");
            Assert.AreEqual(expected.z, actual.z, 1e-3f, $"{message} (z)");
        }

        private static bool ContainsPoint(List<Vector3> points, Vector3 expected)
        {
            for (int i = 0; i < points.Count; i++)
            {
                if (Vector3.Distance(points[i], expected) < 1e-3f)
                    return true;
            }

            return false;
        }

        #endregion FIXTURE


        #region FACE GRID

        [Test]
        public void AppendFaceGrid_Resolution1_ProducesTheSixFaceCentres()
        {
            VisioncastUtility.AppendFaceGrid(
                Vector3.zero, Vector3.right, Vector3.up, Vector3.forward,
                new Vector3(1f, 2f, 3f), 1, _points);

            Assert.AreEqual(6, _points.Count);
            AssertVector(new Vector3(1f, 0f, 0f), _points[0], "+X face");
            AssertVector(new Vector3(-1f, 0f, 0f), _points[1], "-X face");
            AssertVector(new Vector3(0f, 2f, 0f), _points[2], "+Y face");
            AssertVector(new Vector3(0f, -2f, 0f), _points[3], "-Y face");
            AssertVector(new Vector3(0f, 0f, 3f), _points[4], "+Z face");
            AssertVector(new Vector3(0f, 0f, -3f), _points[5], "-Z face");
        }

        [Test]
        public void AppendFaceGrid_ScalesAsSixTimesResolutionSquared()
        {
            for (int resolution = 1; resolution <= 4; resolution++)
            {
                _points.Clear();
                VisioncastUtility.AppendFaceGrid(
                    Vector3.zero, Vector3.right, Vector3.up, Vector3.forward,
                    Vector3.one, resolution, _points);

                Assert.AreEqual(6 * resolution * resolution, _points.Count, $"resolution {resolution}");
            }
        }

        [Test]
        public void AppendFaceGrid_Resolution2_LaysCellCentresOnEachFace()
        {
            VisioncastUtility.AppendFaceGrid(
                Vector3.zero, Vector3.right, Vector3.up, Vector3.forward,
                Vector3.one, 2, _points);

            Assert.AreEqual(24, _points.Count);

            // The +X face sits at x = 1 and is spanned by y/z at the cell centres +-0.5
            Assert.IsTrue(ContainsPoint(_points, new Vector3(1f, -0.5f, -0.5f)));
            Assert.IsTrue(ContainsPoint(_points, new Vector3(1f, -0.5f, 0.5f)));
            Assert.IsTrue(ContainsPoint(_points, new Vector3(1f, 0.5f, -0.5f)));
            Assert.IsTrue(ContainsPoint(_points, new Vector3(1f, 0.5f, 0.5f)));

            // Every point lies on the surface of the box (exactly one axis is saturated)
            foreach (Vector3 p in _points)
            {
                int saturated = 0;
                if (Mathf.Abs(Mathf.Abs(p.x) - 1f) < 1e-3f) saturated++;
                if (Mathf.Abs(Mathf.Abs(p.y) - 1f) < 1e-3f) saturated++;
                if (Mathf.Abs(Mathf.Abs(p.z) - 1f) < 1e-3f) saturated++;
                Assert.GreaterOrEqual(saturated, 1, $"{p} is not on the bounds surface");
            }
        }

        [Test]
        public void AppendFaceGrid_UsesTheSuppliedAxes()
        {
            // Axes swapped (X <-> Z): the "+X" face must follow the supplied axis, not world X
            VisioncastUtility.AppendFaceGrid(
                new Vector3(10f, 0f, 0f), Vector3.forward, Vector3.up, Vector3.right,
                new Vector3(2f, 1f, 1f), 1, _points);

            AssertVector(new Vector3(10f, 0f, 2f), _points[0]);
            AssertVector(new Vector3(10f, 0f, -2f), _points[1]);
        }

        [Test]
        public void AppendFaceGrid_ClampsNonPositiveResolutionToOne()
        {
            VisioncastUtility.AppendFaceGrid(
                Vector3.zero, Vector3.right, Vector3.up, Vector3.forward, Vector3.one, 0, _points);
            Assert.AreEqual(6, _points.Count);

            _points.Clear();
            VisioncastUtility.AppendFaceGrid(
                Vector3.zero, Vector3.right, Vector3.up, Vector3.forward, Vector3.one, -3, _points);
            Assert.AreEqual(6, _points.Count);
        }

        [Test]
        public void AppendFaceGrid_AppendsWithoutClearingTheTargetList()
        {
            _points.Add(new Vector3(99f, 99f, 99f));
            VisioncastUtility.AppendFaceGrid(
                Vector3.zero, Vector3.right, Vector3.up, Vector3.forward, Vector3.one, 1, _points);

            Assert.AreEqual(7, _points.Count);
            AssertVector(new Vector3(99f, 99f, 99f), _points[0], "pre-existing point must survive");
        }

        #endregion FACE GRID


        #region VOLUME GRID

        [Test]
        public void AppendVolumeGrid_Resolution1_ProducesTheCentre()
        {
            VisioncastUtility.AppendVolumeGrid(
                new Vector3(4f, 5f, 6f), Vector3.right, Vector3.up, Vector3.forward,
                Vector3.one * 3f, 1, _points);

            Assert.AreEqual(1, _points.Count);
            AssertVector(new Vector3(4f, 5f, 6f), _points[0]);
        }

        [Test]
        public void AppendVolumeGrid_ScalesAsResolutionCubed()
        {
            for (int resolution = 1; resolution <= 4; resolution++)
            {
                _points.Clear();
                VisioncastUtility.AppendVolumeGrid(
                    Vector3.zero, Vector3.right, Vector3.up, Vector3.forward,
                    Vector3.one, resolution, _points);

                Assert.AreEqual(resolution * resolution * resolution, _points.Count, $"resolution {resolution}");
            }
        }

        [Test]
        public void AppendVolumeGrid_PointsFillTheBoundsInterior()
        {
            VisioncastUtility.AppendVolumeGrid(
                Vector3.zero, Vector3.right, Vector3.up, Vector3.forward,
                Vector3.one, 2, _points);

            Assert.AreEqual(8, _points.Count);
            foreach (Vector3 p in _points)
            {
                AssertVector(new Vector3(
                    Mathf.Sign(p.x) * 0.5f,
                    Mathf.Sign(p.y) * 0.5f,
                    Mathf.Sign(p.z) * 0.5f), p, "cell centre");
            }
        }

        [Test]
        public void AppendVolumeGrid_ClampsNonPositiveResolutionToOne()
        {
            VisioncastUtility.AppendVolumeGrid(
                Vector3.zero, Vector3.right, Vector3.up, Vector3.forward, Vector3.one, 0, _points);

            Assert.AreEqual(1, _points.Count);
        }

        #endregion VOLUME GRID


        #region ORIENTED BOUNDS

        [Test]
        public void GetOrientedBounds_BoxCollider_UsesTransformAxesAndScale()
        {
            GameObject go = _scene.Empty("box", new Vector3(5f, 0f, 0f), Quaternion.Euler(0f, 90f, 0f));
            go.transform.localScale = new Vector3(2f, 1f, 1f);
            BoxCollider box = go.AddComponent<BoxCollider>();
            box.center = new Vector3(1f, 0f, 0f);
            box.size = new Vector3(2f, 2f, 2f);
            Physics.SyncTransforms();

            VisioncastUtility.GetOrientedBounds(
                box, out Vector3 center, out Vector3 axisX, out Vector3 axisY, out Vector3 axisZ, out Vector3 extents);

            // Local centre (1,0,0) -> scaled to (2,0,0) -> rotated 90deg about Y -> (0,0,-2), then translated
            AssertVector(new Vector3(5f, 0f, -2f), center, "centre");
            AssertVector(new Vector3(2f, 1f, 1f), extents, "half-extents follow size * lossyScale");
            AssertVector(go.transform.right, axisX, "axis X");
            AssertVector(go.transform.up, axisY, "axis Y");
            AssertVector(go.transform.forward, axisZ, "axis Z");
            AssertVector(new Vector3(0f, 0f, -1f), axisX, "rotated axis X points down -Z");
        }

        [Test]
        public void GetOrientedBounds_NonBoxCollider_FallsBackToWorldAxisAlignedBounds()
        {
            GameObject go = _scene.Empty("sphere", new Vector3(0f, 5f, 0f), Quaternion.Euler(0f, 45f, 0f));
            go.transform.localScale = Vector3.one * 3f;
            SphereCollider sphere = go.AddComponent<SphereCollider>();
            sphere.radius = 1f;
            Physics.SyncTransforms();

            VisioncastUtility.GetOrientedBounds(
                sphere, out Vector3 center, out Vector3 axisX, out Vector3 axisY, out Vector3 axisZ, out Vector3 extents);

            AssertVector(new Vector3(0f, 5f, 0f), center, "centre");
            AssertVector(Vector3.one * 3f, extents, "radius * scale");
            AssertVector(Vector3.right, axisX, "world axis X");
            AssertVector(Vector3.up, axisY, "world axis Y");
            AssertVector(Vector3.forward, axisZ, "world axis Z");
        }

        #endregion ORIENTED BOUNDS


        #region PROCESSOR

        [Test]
        public void VisiblePointsProcessor_RoutesModeToTheMatchingGrid()
        {
            BoxCollider box = _scene.Target("target", Vector3.zero, Vector3.one * 2f);

            VisiblePointsProcessor.Process(box, VisionSampleMode.BoundsFaceGrid, 2, _points);
            Assert.AreEqual(24, _points.Count, "face grid = 6 * resolution^2");

            _points.Clear();
            VisiblePointsProcessor.Process(box, VisionSampleMode.BoundsVolumeGrid, 3, _points);
            Assert.AreEqual(27, _points.Count, "volume grid = resolution^3");
        }

        [Test]
        public void VisiblePointsProcessor_GeneratesPointsAroundTheColliderPosition()
        {
            BoxCollider box = _scene.Target("target", new Vector3(0f, 0f, 10f), Vector3.one * 2f);

            VisiblePointsProcessor.Process(box, VisionSampleMode.BoundsFaceGrid, 1, _points);

            Assert.AreEqual(6, _points.Count);
            foreach (Vector3 p in _points)
                Assert.AreEqual(1f, Vector3.Distance(p, new Vector3(0f, 0f, 10f)), 1e-3f);
        }

        #endregion PROCESSOR
    }
}
