using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace GalaxyGourd.Visioncast.Tests
{
    /// <summary>
    /// The drop-in pieces a scene actually uses: the standalone driver that ticks the system from
    /// FixedUpdate, the interim relevance feeder, and the debug visualisation seam (which the DoD
    /// pipeline branches on to decide whether to capture visible sample points at all).
    /// </summary>
    [TestFixture]
    public class VisioncastDriverTests
    {
        #region FIXTURE

        private VisionTestScene _scene;
        private GameObject _driverObject;

        [SetUp]
        public void SetUp()
        {
            VisionTestStatics.ResetAll();
            _scene = new VisionTestScene();
        }

        [TearDown]
        public void TearDown()
        {
            // Destroy the driver first: its LateUpdate touches the system it set up in Awake
            if (_driverObject)
                VisionTestScene.DestroyObject(_driverObject);
            _driverObject = null;

            _scene.Dispose();
            VisionTestStatics.ResetAll();
        }

        #endregion FIXTURE


        #region DRIVER

        [UnityTest]
        public IEnumerator Driver_TicksTheSystemFromFixedUpdate()
        {
            _driverObject = new GameObject("driver") { hideFlags = HideFlags.DontSave };
            _driverObject.AddComponent<VisioncastDriver>(); // Awake -> VisioncastManager.Setup()

            TestVisionSource source = _scene.Source("source", Vector3.zero);
            BoxCollider target = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);
            Physics.SyncTransforms();

            for (int i = 0; i < 4; i++)
                yield return new WaitForFixedUpdate();

            Assert.Greater(source.ReceiveCount, 0, "The driver should have cast without any external scheduler");
            Assert.IsTrue(source.Sees(target));
            Assert.Greater(VisioncastManager.VisionTime, 0f);
        }

        [UnityTest]
        public IEnumerator Driver_TearsTheSystemDownOnDestroy()
        {
            _driverObject = new GameObject("driver") { hideFlags = HideFlags.DontSave };
            _driverObject.AddComponent<VisioncastDriver>();

            TestVisionSource source = _scene.Source("source", Vector3.zero);
            _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);

            for (int i = 0; i < 3; i++)
                yield return new WaitForFixedUpdate();

            int received = source.ReceiveCount;
            Assert.Greater(received, 0);

            VisionTestScene.DestroyObject(_driverObject);
            _driverObject = null;

            for (int i = 0; i < 3; i++)
                yield return new WaitForFixedUpdate();

            Assert.AreEqual(received, source.ReceiveCount, "The system is disposed with its driver");
        }

        [Test]
        public void DebugTick_AfterDispose_IsHarmless()
        {
            VisioncastManager.Setup();
            _scene.Source("source", Vector3.zero);
            VisionTickHarness.Warmup();

            VisioncastManager.Dispose();

            Assert.DoesNotThrow(() => VisioncastManager.TickVisioncastSourceDebug(0.02f));
        }

        #endregion DRIVER


        #region RELEVANCE FEEDER

        [Test]
        public void RelevanceFeeder_InstallsAndRemovesItselfAsTheProvider()
        {
            GameObject focus = _scene.Empty("focus", new Vector3(0f, 0f, 30f));
            GameObject feederGo = _scene.Empty("feeder");
            VisionRelevanceFeeder feeder = feederGo.AddComponent<VisionRelevanceFeeder>(); // OnEnable installs

            Assert.IsNotNull(VisionRelevance.Provider, "Enabling the feeder installs a relevance provider");

            TestVisionSource source = _scene.Source("source", Vector3.zero);
            float distance = VisionRelevance.GetDistance(source);
            Assert.GreaterOrEqual(distance, 0f, "With no focus and no camera it falls back to fully relevant");

            feeder.enabled = false;
            Assert.IsNull(VisionRelevance.Provider, "Disabling it must clear its own provider");

            VisionTestScene.DestroyObject(focus);
        }

        [Test]
        public void RelevanceFeeder_DoesNotClobberALaterProvider()
        {
            GameObject feederGo = _scene.Empty("feeder");
            VisionRelevanceFeeder feeder = feederGo.AddComponent<VisionRelevanceFeeder>();

            System.Func<VisioncastSource, float> realSystem = _ => 12f;
            VisionRelevance.Provider = realSystem;

            feeder.enabled = false;

            Assert.AreSame(realSystem, VisionRelevance.Provider,
                "A feeder that is no longer the active provider must leave the real one alone");
        }

        #endregion RELEVANCE FEEDER


        #region VISUALS

        [Test]
        public void VisionCone_BuildsAMeshBoundedByTheSourceRange()
        {
            TestVisionSource source = _scene.Source("source", Vector3.zero, Quaternion.identity, 12f, 30f);
            GameObject coneGo = _scene.Empty("cone");
            VisionCone cone = coneGo.AddComponent<VisionCone>();

            cone.CalculateCone(source);

            Mesh mesh = coneGo.GetComponent<MeshFilter>().sharedMesh;
            _scene.Track(mesh);
            Assert.IsNotNull(mesh);
            Assert.Greater(mesh.vertexCount, 0);
            foreach (Vector3 vertex in mesh.vertices)
            {
                Assert.LessOrEqual(vertex.magnitude, 12f + 1e-2f,
                    "Every cone vertex lies within the source's range of the apex");
            }

            Assert.DoesNotThrow(() => cone.Toggle(false));
            Assert.IsFalse(coneGo.GetComponent<MeshRenderer>().enabled);
        }

        [Test]
        public void AttachedDebugVisualizer_MakesTheDodPipelineCaptureVisiblePoints()
        {
            VisionPipeline.UseDod();
            VisioncastManager.Setup();

            TestVisionSource source = _scene.Source("source", Vector3.zero);
            source.gameObject.AddComponent<VisioncastSourceDebug>(); // Awake attaches to the source
            BoxCollider target = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);

            VisionTickHarness.Warmup();

            Assert.IsTrue(source.HasDebug);
            Assert.IsTrue(source.Sees(target));
            DataVisioncastResult results = source.LastResults;
            Assert.AreEqual(results.Objects.Count, results.VisiblePoints.Count,
                "A visualized source gets its per-object point vectors back");
            Assert.AreEqual(
                results.VisiblePointCounts[0],
                results.VisiblePoints[0].Count,
                "The captured points must match the counted ones");

            Assert.DoesNotThrow(() => VisioncastManager.TickVisioncastSourceDebug(0.02f));
        }

        [Test]
        public void WithoutADebugVisualizer_TheDodPipelineSkipsThePointVectors()
        {
            VisionPipeline.UseDod();
            VisioncastManager.Setup();

            TestVisionSource source = _scene.Source("source", Vector3.zero);
            BoxCollider target = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);

            VisionTickHarness.Warmup();

            Assert.IsFalse(source.HasDebug);
            Assert.IsTrue(source.Sees(target));
            Assert.AreEqual(0, source.LastResults.VisiblePoints.Count,
                "Point vectors are debug-only - consumers read VisiblePointCounts");
        }

        #endregion VISUALS
    }
}
