using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace GalaxyGourd.Visioncast.Tests
{
    /// <summary>
    /// The DoD gather: every registered target's world-space oriented box, rebuilt from the manifest on
    /// registration changes and re-projected through its transform each tick. The OBB must match what the
    /// managed path derives from the collider - it is what the native narrowphase aims its samples at.
    /// </summary>
    [TestFixture]
    public class VisionTargetGatherTests
    {
        #region FIXTURE

        private VisionTestScene _scene;
        private VisionTargetGather _gather;

        [SetUp]
        public void SetUp()
        {
            VisionTestStatics.ResetAll();
            _scene = new VisionTestScene();
            _gather = new VisionTargetGather();
        }

        [TearDown]
        public void TearDown()
        {
            _gather.Dispose();
            _scene.Dispose();
            VisionTestStatics.ResetAll();
        }

        private static void AssertVector(float3 expected, float3 actual, string message = "")
        {
            Assert.AreEqual(expected.x, actual.x, 1e-3f, $"{message} (x)");
            Assert.AreEqual(expected.y, actual.y, 1e-3f, $"{message} (y)");
            Assert.AreEqual(expected.z, actual.z, 1e-3f, $"{message} (z)");
        }

        private TargetObb GatherObbOf(Collider col)
        {
            _gather.Gather();
            int id = VisionTargetsManifest.GetId(col);
            Assert.GreaterOrEqual(id, 0, "Collider must be registered to be gathered");
            return _gather.WorldObbs[id];
        }

        #endregion FIXTURE


        #region GATHER

        [Test]
        public void Gather_MirrorsTheManifest()
        {
            _scene.Target("a", Vector3.zero, Vector3.one);
            _scene.Target("b", new Vector3(5f, 0f, 0f), Vector3.one);

            _gather.Gather();

            Assert.AreEqual(VisionTargetsManifest.TargetCount, _gather.Count);
        }

        [Test]
        public void Gather_AxisAlignedBox_ProducesTheWorldBox()
        {
            BoxCollider box = _scene.Target("box", new Vector3(2f, 3f, 4f), new Vector3(2f, 4f, 6f));

            TargetObb obb = GatherObbOf(box);

            AssertVector(new float3(2f, 3f, 4f), obb.Center, "centre");
            AssertVector(new float3(1f, 2f, 3f), obb.Extents, "half-extents");
            AssertVector(new float3(1f, 0f, 0f), obb.AxisX, "axis X");
            AssertVector(new float3(0f, 1f, 0f), obb.AxisY, "axis Y");
            AssertVector(new float3(0f, 0f, 1f), obb.AxisZ, "axis Z");
        }

        [Test]
        public void Gather_RotatedBox_KeepsTheBoxOrientedRatherThanInflated()
        {
            GameObject go = _scene.Empty("box", new Vector3(0f, 0f, 10f), Quaternion.Euler(0f, 45f, 0f));
            BoxCollider box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(2f, 2f, 8f);
            _scene.Register(box);
            Physics.SyncTransforms();

            TargetObb obb = GatherObbOf(box);

            // The world AABB of this rotated box is far larger - the OBB must keep the true half-sizes
            AssertVector(new float3(1f, 1f, 4f), obb.Extents, "half-extents stay in the box's own axes");
            AssertVector(go.transform.right, obb.AxisX, "axis X follows the transform");
            AssertVector(go.transform.forward, obb.AxisZ, "axis Z follows the transform");
            AssertVector(new float3(0f, 0f, 10f), obb.Center, "centre");
        }

        [Test]
        public void Gather_ScaledAndOffsetBox_AppliesBothToTheWorldBox()
        {
            GameObject go = _scene.Empty("box", new Vector3(1f, 0f, 0f));
            go.transform.localScale = new Vector3(2f, 3f, 4f);
            BoxCollider box = go.AddComponent<BoxCollider>();
            box.center = new Vector3(0.5f, 0f, 0f);
            box.size = Vector3.one;
            _scene.Register(box);
            Physics.SyncTransforms();

            TargetObb obb = GatherObbOf(box);

            AssertVector(new float3(2f, 0f, 0f), obb.Center, "local offset scales with the transform");
            AssertVector(new float3(1f, 1.5f, 2f), obb.Extents, "half-extents scale per axis");
        }

        [Test]
        public void Gather_MatchesTheManagedOrientedBounds()
        {
            GameObject go = _scene.Empty("box", new Vector3(-3f, 2f, 7f), Quaternion.Euler(15f, 40f, 5f));
            go.transform.localScale = new Vector3(1.5f, 2f, 0.5f);
            BoxCollider box = go.AddComponent<BoxCollider>();
            box.center = new Vector3(0.25f, -0.5f, 0f);
            box.size = new Vector3(1f, 2f, 3f);
            _scene.Register(box);
            Physics.SyncTransforms();

            TargetObb obb = GatherObbOf(box);
            VisioncastUtility.GetOrientedBounds(
                box, out Vector3 center, out Vector3 axisX, out Vector3 axisY, out Vector3 axisZ, out Vector3 extents);

            AssertVector(center, obb.Center, "centre");
            AssertVector(extents, obb.Extents, "half-extents");
            AssertVector(axisX, obb.AxisX, "axis X");
            AssertVector(axisY, obb.AxisY, "axis Y");
            AssertVector(axisZ, obb.AxisZ, "axis Z");
        }

        [Test]
        public void Gather_Sphere_UsesTheRadiusAsHalfExtents()
        {
            GameObject go = _scene.Empty("sphere", new Vector3(0f, 4f, 0f));
            go.transform.localScale = Vector3.one * 2f;
            SphereCollider sphere = go.AddComponent<SphereCollider>();
            sphere.radius = 1.5f;
            _scene.Register(sphere);
            Physics.SyncTransforms();

            TargetObb obb = GatherObbOf(sphere);

            AssertVector(new float3(0f, 4f, 0f), obb.Center);
            AssertVector(new float3(3f, 3f, 3f), obb.Extents, "radius * scale on every axis");
        }

        [Test]
        public void Gather_Capsule_UsesHeightAlongItsDirectionAxis()
        {
            GameObject go = _scene.Empty("capsule", Vector3.zero);
            CapsuleCollider capsule = go.AddComponent<CapsuleCollider>();
            capsule.direction = 1; // Y
            capsule.radius = 0.5f;
            capsule.height = 3f;
            _scene.Register(capsule);
            Physics.SyncTransforms();

            TargetObb obb = GatherObbOf(capsule);

            AssertVector(new float3(0.5f, 1.5f, 0.5f), obb.Extents, "half height along Y, radius across");
        }

        [Test]
        public void Gather_TracksTransformMovementWithoutARebuild()
        {
            BoxCollider box = _scene.Target("box", Vector3.zero, Vector3.one * 2f);
            GatherObbOf(box);

            box.transform.position = new Vector3(0f, 0f, 12f);
            TargetObb moved = GatherObbOf(box);

            AssertVector(new float3(0f, 0f, 12f), moved.Center, "the gather re-projects every tick");
        }

        [Test]
        public void Gather_RebuildsWhenTheManifestChanges()
        {
            BoxCollider first = _scene.Target("first", Vector3.zero, Vector3.one);
            _gather.Gather();
            int countBefore = _gather.Count;

            BoxCollider second = _scene.Target("second", new Vector3(4f, 0f, 0f), Vector3.one);
            _gather.Gather();

            Assert.AreEqual(countBefore + 1, _gather.Count);
            AssertVector(new float3(4f, 0f, 0f), _gather.WorldObbs[VisionTargetsManifest.GetId(second)].Center);
            AssertVector(float3.zero, _gather.WorldObbs[VisionTargetsManifest.GetId(first)].Center);
        }

        [Test]
        public void Gather_CapturesTheColliderLayer()
        {
            BoxCollider target = _scene.Target("target", Vector3.zero, Vector3.one, VisionTestScene.LayerOther);

            _gather.Gather();

            Assert.AreEqual(VisionTestScene.LayerOther, _gather.Layers[VisionTargetsManifest.GetId(target)]);
        }

        [Test]
        public void Gather_WithNoTargets_IsEmptyAndSafe()
        {
            Assert.DoesNotThrow(() => _gather.Gather());
            Assert.AreEqual(0, _gather.Count);
        }

        #endregion GATHER
    }
}
