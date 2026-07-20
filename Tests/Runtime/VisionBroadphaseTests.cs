using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace GalaxyGourd.Visioncast.Tests
{
    /// <summary>
    /// The three interchangeable spatial-query implementations behind <see cref="IBroadphase"/>. They are
    /// swap-in alternatives, so the contract under test is that they agree: same candidate set for the
    /// same scene, same range and layer filtering, same behaviour with nothing in range.
    ///
    /// Targets are placed either well inside or well outside range - the physics implementations test
    /// collider bounds while the grid tests target centres, which only differ on the boundary.
    /// </summary>
    [TestFixture]
    public class VisionBroadphaseTests
    {
        #region FIXTURE

        private VisionTestScene _scene;

        [SetUp]
        public void SetUp()
        {
            VisionTestStatics.ResetAll();
            _scene = new VisionTestScene();
        }

        [TearDown]
        public void TearDown()
        {
            _scene.Dispose();
            VisionTestStatics.ResetAll();
        }

        private static List<Collider> QueryOne(IBroadphase broadphase, VisioncastSource source)
        {
            List<VisioncastSource> sources = new List<VisioncastSource> { source };
            List<List<Collider>> candidates = new List<List<Collider>> { new List<Collider>() };

            Physics.SyncTransforms();
            broadphase.Query(sources, 1, candidates);
            return candidates[0];
        }

        private static List<List<Collider>> QueryAll(IBroadphase broadphase, List<VisioncastSource> sources)
        {
            List<List<Collider>> candidates = new List<List<Collider>>();
            for (int i = 0; i < sources.Count; i++)
                candidates.Add(new List<Collider>());

            Physics.SyncTransforms();
            broadphase.Query(sources, sources.Count, candidates);
            return candidates;
        }

        private static void AssertSameSet(List<Collider> expected, List<Collider> actual, string message)
        {
            CollectionAssert.AreEquivalent(
                expected.Select(c => c.name).OrderBy(n => n).ToList(),
                actual.Select(c => c.name).OrderBy(n => n).ToList(),
                message);
        }

        private IBroadphase Physics_() => new PhysicsOverlapBroadphase();
        private IBroadphase Batched() => new OverlapCommandBroadphase();
        private IBroadphase Grid() => new VisionGridBroadphase(10f);

        #endregion FIXTURE


        #region RANGE

        [Test]
        public void EveryImplementation_ReturnsTheSameInRangeSet()
        {
            TestVisionSource source = _scene.Source("source", Vector3.zero, Quaternion.identity, 15f);
            _scene.Target("near", new Vector3(0f, 0f, 5f), Vector3.one);
            _scene.Target("side", new Vector3(-6f, 0f, 0f), Vector3.one);
            _scene.Target("behind", new Vector3(0f, 0f, -8f), Vector3.one);
            _scene.Target("distant", new Vector3(0f, 0f, 60f), Vector3.one);

            using IBroadphase physics = Physics_();
            using IBroadphase batched = Batched();
            using IBroadphase grid = Grid();

            List<Collider> fromPhysics = QueryOne(physics, source);
            List<Collider> fromBatched = QueryOne(batched, source);
            List<Collider> fromGrid = QueryOne(grid, source);

            Assert.AreEqual(3, fromPhysics.Count, "The broadphase is range-only - the cone filter comes later");
            AssertSameSet(fromPhysics, fromBatched, "Batched overlap must match the physics overlap");
            AssertSameSet(fromPhysics, fromGrid, "The spatial grid must match the physics overlap");
        }

        [Test]
        public void EveryImplementation_FiltersByBroadphaseLayer()
        {
            TestVisionSource source = _scene.Source("source", Vector3.zero, Quaternion.identity, 15f);
            source.BroadphaseMask = VisionTestScene.MaskOf(VisionTestScene.LayerTarget);
            _scene.Target("onMask", new Vector3(0f, 0f, 5f), Vector3.one);
            _scene.Target("offMask", new Vector3(0f, 0f, 6f), Vector3.one, VisionTestScene.LayerOther);

            using IBroadphase physics = Physics_();
            using IBroadphase grid = Grid();

            List<Collider> fromPhysics = QueryOne(physics, source);
            List<Collider> fromGrid = QueryOne(grid, source);

            Assert.AreEqual(1, fromPhysics.Count);
            Assert.AreEqual("onMask", fromPhysics[0].name);
            AssertSameSet(fromPhysics, fromGrid, "Layer filtering must agree");
        }

        [Test]
        public void EveryImplementation_HandlesAnEmptyScene()
        {
            TestVisionSource source = _scene.Source("source", Vector3.zero);

            using IBroadphase physics = Physics_();
            using IBroadphase batched = Batched();
            using IBroadphase grid = Grid();

            Assert.AreEqual(0, QueryOne(physics, source).Count);
            Assert.AreEqual(0, QueryOne(batched, source).Count);
            Assert.AreEqual(0, QueryOne(grid, source).Count);
        }

        [Test]
        public void EveryImplementation_KeepsPerSourceCandidatesAligned()
        {
            List<VisioncastSource> sources = new List<VisioncastSource>
            {
                _scene.Source("left", new Vector3(-20f, 0f, 0f), Quaternion.identity, 8f),
                _scene.Source("right", new Vector3(20f, 0f, 0f), Quaternion.identity, 8f)
            };
            _scene.Target("leftTarget", new Vector3(-22f, 0f, 0f), Vector3.one);
            _scene.Target("rightTarget", new Vector3(23f, 0f, 0f), Vector3.one);

            using IBroadphase physics = Physics_();
            using IBroadphase grid = Grid();

            List<List<Collider>> fromPhysics = QueryAll(physics, sources);
            List<List<Collider>> fromGrid = QueryAll(grid, sources);

            Assert.AreEqual("leftTarget", fromPhysics[0].Single().name);
            Assert.AreEqual("rightTarget", fromPhysics[1].Single().name);
            AssertSameSet(fromPhysics[0], fromGrid[0], "source 0");
            AssertSameSet(fromPhysics[1], fromGrid[1], "source 1");
        }

        [Test]
        public void Grid_OnlyConsidersRegisteredTargets()
        {
            TestVisionSource source = _scene.Source("source", Vector3.zero, Quaternion.identity, 15f);
            _scene.Target("registered", new Vector3(0f, 0f, 5f), Vector3.one);
            _scene.Box("unregistered", new Vector3(0f, 0f, 6f), Vector3.one); // same layer, not a vision target

            using IBroadphase grid = Grid();
            List<Collider> fromGrid = QueryOne(grid, source);

            Assert.AreEqual(1, fromGrid.Count);
            Assert.AreEqual("registered", fromGrid[0].name, "The grid is built from the manifest, not the physics scene");
        }

        [Test]
        public void Grid_TracksTargetsAcrossCellsAsTheyMove()
        {
            TestVisionSource source = _scene.Source("source", Vector3.zero, Quaternion.identity, 15f);
            BoxCollider target = _scene.Target("mover", new Vector3(0f, 0f, 5f), Vector3.one);

            using IBroadphase grid = Grid();
            Assert.AreEqual(1, QueryOne(grid, source).Count);

            target.transform.position = new Vector3(0f, 0f, 90f); // several cells away, out of range
            Assert.AreEqual(0, QueryOne(grid, source).Count);

            target.transform.position = new Vector3(0f, 0f, 7f);
            Assert.AreEqual(1, QueryOne(grid, source).Count, "Coming back into range must re-enter the candidate set");
        }

        [Test]
        public void Grid_HandlesTargetsSpreadAcrossManyCells()
        {
            TestVisionSource source = _scene.Source("source", Vector3.zero, Quaternion.identity, 30f);
            List<Collider> expected = new List<Collider>();
            for (int i = 0; i < 12; i++)
            {
                float angle = i * (Mathf.PI * 2f / 12f);
                Vector3 position = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 20f;
                expected.Add(_scene.Target($"ring{i}", position, Vector3.one));
            }

            _scene.Target("far", new Vector3(0f, 0f, 120f), Vector3.one);

            using IBroadphase physics = Physics_();
            using IBroadphase grid = Grid();

            AssertSameSet(QueryOne(physics, source), QueryOne(grid, source), "Multi-cell scan must find every target");
            Assert.AreEqual(12, QueryOne(grid, source).Count);
        }

        #endregion RANGE
    }
}
