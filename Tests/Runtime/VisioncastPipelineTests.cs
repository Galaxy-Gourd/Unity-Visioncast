using NUnit.Framework;
using UnityEngine;

namespace GalaxyGourd.Visioncast.Tests
{
    /// <summary>
    /// End-to-end vision against a real physics scene, run TWICE: once through the managed
    /// broadphase/narrowphase path and once through the native DoD pipeline. Both must agree on what is
    /// seen, what is occluded, and what is filtered out by range / cone / layer - that parity is the
    /// contract the DoD rewrite has to hold.
    ///
    /// Exact sample counts intentionally are NOT compared between the paths: the managed path reports the
    /// straight-ahead confirmation ray as an extra visible point on top of the sample grid, so its
    /// numerator runs one higher. Assertions are on the observable contract instead.
    /// </summary>
    [TestFixture(false, TestName = "VisioncastPipeline_Managed")]
    [TestFixture(true, TestName = "VisioncastPipeline_Dod")]
    public class VisioncastPipelineTests
    {
        #region FIXTURE

        private readonly bool _dod;
        private VisionTestScene _scene;

        public VisioncastPipelineTests(bool dod)
        {
            _dod = dod;
        }

        [SetUp]
        public void SetUp()
        {
            VisionTestStatics.ResetAll();
            if (_dod)
                VisionPipeline.UseDod();

            VisioncastManager.Setup();
            _scene = new VisionTestScene();
        }

        [TearDown]
        public void TearDown()
        {
            _scene.Dispose();
            VisionTestStatics.ResetAll();
        }

        /// <summary>Source at the origin looking down +Z.</summary>
        private TestVisionSource Observer(float range = 20f, float fov = 60f)
        {
            return _scene.Source("observer", Vector3.zero, Quaternion.identity, range, fov);
        }

        #endregion FIXTURE


        #region SEEING

        [Test]
        public void TargetInFrontOfSource_IsSeen()
        {
            TestVisionSource source = Observer();
            BoxCollider target = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);

            VisionTickHarness.Warmup();

            Assert.GreaterOrEqual(source.ReceiveCount, 1, "The source should have cast by now");
            Assert.GreaterOrEqual(source.IndexOf(target), 0, "Target should be reported");
            Assert.Greater(source.VisibleCountFor(target), 0, "An unobstructed target has visible sample points");
            Assert.Greater(source.SampleCountFor(target), 0);
        }

        [Test]
        public void UnobstructedTarget_IsFullyVisible()
        {
            TestVisionSource source = Observer();
            BoxCollider target = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);

            VisionTickHarness.Warmup();

            // Every sample ray reaches the target itself (its own far faces are hit through the near face)
            Assert.GreaterOrEqual(
                source.VisibleCountFor(target),
                source.SampleCountFor(target),
                "Nothing is in the way, so every sample point should resolve to the target");
        }

        [Test]
        public void SeveralTargets_AreAllReported()
        {
            TestVisionSource source = Observer();
            BoxCollider left = _scene.Target("left", new Vector3(-3f, 0f, 6f), Vector3.one);
            BoxCollider right = _scene.Target("right", new Vector3(3f, 0f, 6f), Vector3.one);
            BoxCollider centre = _scene.Target("centre", new Vector3(0f, 0f, 8f), Vector3.one);

            VisionTickHarness.Warmup();

            Assert.IsTrue(source.Sees(left));
            Assert.IsTrue(source.Sees(right));
            Assert.IsTrue(source.Sees(centre));
        }

        [Test]
        public void UnregisteredCollider_IsNeverATarget()
        {
            TestVisionSource source = Observer();
            GameObject prop = _scene.Box("prop", new Vector3(0f, 0f, 5f), Vector3.one * 2f);

            VisionTickHarness.Warmup();

            Assert.Less(source.IndexOf(prop.GetComponent<BoxCollider>()), 0,
                "Only manifest-registered colliders are vision targets");
        }

        #endregion SEEING


        #region OCCLUSION

        [Test]
        public void OccluderBetweenSourceAndTarget_HidesIt()
        {
            TestVisionSource source = Observer();
            BoxCollider target = _scene.Target("target", new Vector3(0f, 0f, 8f), Vector3.one * 2f);
            _scene.Box("wall", new Vector3(0f, 0f, 4f), new Vector3(20f, 20f, 0.5f), VisionTestScene.LayerOccluder);

            VisionTickHarness.Warmup();

            Assert.GreaterOrEqual(source.IndexOf(target), 0, "An occluded target is still resolved (in range, in cone)");
            Assert.AreEqual(0, source.VisibleCountFor(target), "Every line of sight is blocked");
        }

        [Test]
        public void OccluderOnAnUnraycastableLayer_DoesNotBlock()
        {
            TestVisionSource source = Observer();
            source.RaycastMask = VisionTestScene.MaskOf(VisionTestScene.LayerTarget); // occluders excluded
            BoxCollider target = _scene.Target("target", new Vector3(0f, 0f, 8f), Vector3.one * 2f);
            _scene.Box("wall", new Vector3(0f, 0f, 4f), new Vector3(20f, 20f, 0.5f), VisionTestScene.LayerOccluder);

            VisionTickHarness.Warmup();

            Assert.Greater(source.VisibleCountFor(target), 0, "A wall the ray cannot hit cannot occlude");
        }

        [Test]
        public void PartialOccluder_ReportsAPartialVisibility()
        {
            TestVisionSource source = Observer();
            source.Resolution = 2; // 6 * 2^2 face samples + the closest point
            BoxCollider target = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);

            // Blocks only the +X half of the sample set (spans x from 0.05 outwards)
            _scene.Box("halfWall", new Vector3(5.05f, 0f, 2.5f), new Vector3(10f, 20f, 0.2f),
                VisionTestScene.LayerOccluder);

            VisionTickHarness.Warmup();

            int visible = source.VisibleCountFor(target);
            int samples = source.SampleCountFor(target);
            Assert.Greater(visible, 0, "The unblocked half is still visible");
            Assert.Less(visible, samples, "The blocked half must not count as visible");
        }

        [Test]
        public void TargetsDoNotOccludeEachOther_ForTheirOwnSamples()
        {
            TestVisionSource source = Observer();
            BoxCollider near = _scene.Target("near", new Vector3(0f, 0f, 4f), Vector3.one * 2f);
            BoxCollider far = _scene.Target("far", new Vector3(0f, 0f, 9f), Vector3.one * 2f);

            VisionTickHarness.Warmup();

            Assert.Greater(source.VisibleCountFor(near), 0);
            Assert.AreEqual(0, source.VisibleCountFor(far), "The near target occludes the far one");
        }

        #endregion OCCLUSION


        #region FILTERING

        [Test]
        public void TargetBehindTheSource_IsFilteredOut()
        {
            TestVisionSource source = Observer();
            BoxCollider behind = _scene.Target("behind", new Vector3(0f, 0f, -5f), Vector3.one * 2f);

            VisionTickHarness.Warmup();

            Assert.Less(source.IndexOf(behind), 0, "Outside the field of view");
        }

        [Test]
        public void NarrowFieldOfView_ExcludesOffAxisTargets()
        {
            TestVisionSource narrow = _scene.Source("narrow", Vector3.zero, Quaternion.identity, 20f, 15f);
            TestVisionSource wide = _scene.Source("wide", Vector3.zero, Quaternion.identity, 20f, 70f);
            BoxCollider offAxis = _scene.Target("offAxis", new Vector3(5f, 0f, 5f), Vector3.one); // ~45 degrees

            VisionTickHarness.Warmup();

            Assert.Less(narrow.IndexOf(offAxis), 0, "45 degrees is outside a 15 degree half-angle cone");
            Assert.GreaterOrEqual(wide.IndexOf(offAxis), 0, "...and inside a 70 degree one");
        }

        [Test]
        public void TargetBeyondRange_IsFilteredOut()
        {
            TestVisionSource source = Observer(range: 10f);
            BoxCollider near = _scene.Target("near", new Vector3(0f, 0f, 5f), Vector3.one);
            BoxCollider distant = _scene.Target("distant", new Vector3(0f, 0f, 50f), Vector3.one);

            VisionTickHarness.Warmup();

            Assert.GreaterOrEqual(source.IndexOf(near), 0);
            Assert.Less(source.IndexOf(distant), 0);
        }

        [Test]
        public void BroadphaseLayerMask_ExcludesUnwantedTargets()
        {
            TestVisionSource source = Observer();
            BoxCollider visible = _scene.Target("visible", new Vector3(-2f, 0f, 6f), Vector3.one);
            BoxCollider offMask = _scene.Target("offMask", new Vector3(2f, 0f, 6f), Vector3.one,
                VisionTestScene.LayerOther);

            VisionTickHarness.Warmup();

            Assert.GreaterOrEqual(source.IndexOf(visible), 0);
            Assert.Less(source.IndexOf(offMask), 0, "The layer is not in the source's broadphase mask");
        }

        [Test]
        public void HeadingFollowsTheTransform()
        {
            TestVisionSource source = Observer(fov: 45f);
            BoxCollider side = _scene.Target("side", new Vector3(6f, 0f, 0f), Vector3.one * 2f);

            VisionTickHarness.Warmup();
            Assert.Less(source.IndexOf(side), 0, "Target is 90 degrees off the initial heading");

            source.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
            VisionTickHarness.Run(2);

            Assert.GreaterOrEqual(source.IndexOf(side), 0, "Turning to face it brings it into the cone");
        }

        #endregion FILTERING


        #region SAMPLING

        [Test]
        public void SampleResolution_ScalesTheSampleCount()
        {
            TestVisionSource coarse = _scene.Source("coarse", Vector3.zero);
            TestVisionSource dense = _scene.Source("dense", Vector3.zero);
            dense.Resolution = 3;
            BoxCollider target = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);

            VisionTickHarness.Warmup();

            Assert.Greater(
                dense.SampleCountFor(target),
                coarse.SampleCountFor(target),
                "A denser grid must test more points");
        }

        [Test]
        public void VolumeSampleMode_UsesTheVolumeGridBudget()
        {
            TestVisionSource source = _scene.Source("volume", Vector3.zero);
            source.Mode = VisionSampleMode.BoundsVolumeGrid;
            source.Resolution = 3;
            BoxCollider target = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);

            VisionTickHarness.Warmup();

            // resolution^3 interior points (+ the closest-point ray in the DoD budget)
            int samples = source.SampleCountFor(target);
            Assert.GreaterOrEqual(samples, 27);
            Assert.LessOrEqual(samples, 28);
        }

        [Test]
        public void LodSampleResolutionCap_CoarsensDistantSources()
        {
            VisionLOD.Tiers = new[]
            {
                new VisionLODTier { MaxDistance = 5f, Cadence = 1 },
                new VisionLODTier { MaxDistance = 1000f, Cadence = 1, SampleResolution = 1 }
            };

            TestVisionSource near = _scene.Source("near", Vector3.zero);
            TestVisionSource far = _scene.Source("far", Vector3.zero);
            near.Resolution = 3;
            far.Resolution = 3;
            BoxCollider target = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);

            // Relevance distance decides the tier; the "far" source is pushed into the capped tier
            VisionRelevance.Provider = s => s == far ? 500f : 0f;
            VisionTickHarness.Warmup();

            Assert.Greater(near.SampleCountFor(target), far.SampleCountFor(target),
                "The far tier caps sample resolution at 1");
        }

        #endregion SAMPLING


        #region LIFECYCLE

        [Test]
        public void DisablingASource_StopsItCasting()
        {
            TestVisionSource source = Observer();
            _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);

            VisionTickHarness.Warmup();
            Assert.Greater(source.ReceiveCount, 0);

            source.enabled = false;   // OnDisable queues the removal...
            VisionTickHarness.Run(1); // ...which resolves at the end of the next cycle
            int received = source.ReceiveCount;

            VisionTickHarness.Run(3);

            Assert.AreEqual(received, source.ReceiveCount, "A disabled source must not keep casting");
        }

        [Test]
        public void ReEnablingASource_ResumesCasting()
        {
            TestVisionSource source = Observer();
            BoxCollider target = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);

            VisionTickHarness.Warmup();
            source.enabled = false;
            VisionTickHarness.Run(2);
            int received = source.ReceiveCount;

            source.enabled = true;
            VisionTickHarness.Run(3);

            Assert.Greater(source.ReceiveCount, received);
            Assert.IsTrue(source.Sees(target));
        }

        [Test]
        public void DestroyingASourceMidFlight_DoesNotBreakTheTick()
        {
            TestVisionSource source = Observer();
            _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);

            VisionTickHarness.Warmup();
            VisionTestScene.DestroyObject(source.gameObject);

            Assert.DoesNotThrow(() => VisionTickHarness.Run(3));
        }

        [Test]
        public void UnregisteringATargetMidFlight_DoesNotBreakTheTick()
        {
            TestVisionSource source = Observer();
            BoxCollider target = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);
            BoxCollider other = _scene.Target("other", new Vector3(2f, 0f, 6f), Vector3.one);

            VisionTickHarness.Warmup();
            _scene.Unregister(target);
            VisionTestScene.DestroyObject(target.gameObject);

            Assert.DoesNotThrow(() => VisionTickHarness.Run(3));
            Assert.IsTrue(source.Sees(other), "The remaining target is unaffected by the id compaction");
        }

        [Test]
        public void SourceAddedLater_JoinsTheNextCycle()
        {
            Observer();
            BoxCollider target = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);
            VisionTickHarness.Warmup();

            TestVisionSource latecomer = _scene.Source("latecomer", new Vector3(0f, 0f, 1f));
            VisionTickHarness.Run(VisionTickHarness.CONST_WarmupCycles);

            Assert.Greater(latecomer.ReceiveCount, 0);
            Assert.IsTrue(latecomer.Sees(target));
        }

        [Test]
        public void MovingTargets_AreTrackedAcrossCycles()
        {
            TestVisionSource source = Observer(fov: 30f);
            BoxCollider target = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one);

            VisionTickHarness.Warmup();
            Assert.IsTrue(source.Sees(target));

            target.transform.position = new Vector3(0f, 0f, -5f); // behind the source
            VisionTickHarness.Run(2);

            Assert.IsFalse(source.Sees(target));
        }

        #endregion LIFECYCLE
    }
}
