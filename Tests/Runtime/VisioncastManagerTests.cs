using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace GalaxyGourd.Visioncast.Tests
{
    /// <summary>
    /// System lifecycle and scheduling: setup/dispose, the registration queue that lets sources come up
    /// before the system does, vision-time bookkeeping (the staleness clock consumers read under LOD),
    /// tick hooks, and the LOD cadence that decides which sources cast on a given tick.
    /// </summary>
    [TestFixture]
    public class VisioncastManagerTests
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

        #endregion FIXTURE


        #region SETUP

        [Test]
        public void SourcesRegisteredBeforeSetup_AreQueuedAndPickedUp()
        {
            // No Setup() yet - the source's OnEnable registration has nowhere to go but the queue
            TestVisionSource source = _scene.Source("early", Vector3.zero);
            BoxCollider target = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);

            VisioncastManager.Setup();
            VisionTickHarness.Warmup();

            Assert.Greater(source.ReceiveCount, 0, "A source registered before Setup must still be picked up");
            Assert.IsTrue(source.Sees(target));
        }

        [Test]
        public void TickingBeforeSetup_IsHarmless()
        {
            _scene.Source("orphan", Vector3.zero);

            Assert.DoesNotThrow(() => VisionTickHarness.Run(2));
        }

        [Test]
        public void Dispose_StopsTheSystemAndIsIdempotent()
        {
            VisioncastManager.Setup();
            TestVisionSource source = _scene.Source("source", Vector3.zero);
            _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);
            VisionTickHarness.Warmup();
            int received = source.ReceiveCount;

            VisioncastManager.Dispose();
            VisioncastManager.Dispose();

            Assert.DoesNotThrow(() => VisionTickHarness.Run(3));
            Assert.AreEqual(received, source.ReceiveCount, "A disposed system must not deliver results");
        }

        [Test]
        public void ComponentsModifiedEvent_FiresWhenTheSourceSetChanges()
        {
            VisioncastManager.Setup(); // clears any previous subscribers

            List<VisioncastSource> reported = null;
            int calls = 0;
            VisioncastManager.OnSourceComponentsModified += sources =>
            {
                calls++;
                reported = new List<VisioncastSource>(sources);
            };

            TestVisionSource source = _scene.Source("source", Vector3.zero);
            VisionTickHarness.Run(1);

            Assert.AreEqual(1, calls);
            CollectionAssert.Contains(reported, source);

            VisionTickHarness.Run(2);
            Assert.AreEqual(1, calls, "No further events while the source set is stable");
        }

        #endregion SETUP


        #region VISION TIME

        [Test]
        public void VisionTime_AccumulatesTickDeltas()
        {
            VisioncastManager.Setup();
            Assert.AreEqual(0f, VisioncastManager.VisionTime, 1e-4f);

            VisionTickHarness.Run(5);

            Assert.AreEqual(5f * VisionTickHarness.CONST_Delta, VisioncastManager.VisionTime, 1e-4f);
        }

        [Test]
        public void VisionTime_IsZeroWithoutASystem()
        {
            VisioncastManager.Dispose();

            Assert.AreEqual(0f, VisioncastManager.VisionTime, 1e-4f);
        }

        [Test]
        public void LastUpdatedTime_TracksTheCycleTheSourceCastOn()
        {
            VisioncastManager.Setup();
            TestVisionSource source = _scene.Source("source", Vector3.zero);
            _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);

            VisionTickHarness.Warmup();

            Assert.AreEqual(VisioncastManager.VisionTime, source.LastUpdatedTime, 1e-4f);
            Assert.AreEqual(0f, source.TimeSinceUpdate, 1e-4f, "Just updated - nothing is stale");
        }

        [Test]
        public void TickHooks_FireAroundEachVisionTick()
        {
            VisioncastManager.Setup();
            int pre = 0;
            int post = 0;
            VisioncastManager.PreVisioncastTick = () => pre++;
            VisioncastManager.PostVisioncastTick = () => post++;

            VisionTickHarness.Run(3);

            Assert.AreEqual(3, pre);
            Assert.AreEqual(3, post);
        }

        #endregion VISION TIME


        #region LOD CADENCE

        [Test]
        public void DefaultLod_CastsEverySourceEveryTick()
        {
            VisioncastManager.Setup();
            TestVisionSource source = _scene.Source("source", Vector3.zero);
            _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);
            VisionTickHarness.Warmup();

            int before = source.ReceiveCount;
            VisionTickHarness.Run(6);

            Assert.AreEqual(before + 6, source.ReceiveCount);
        }

        [Test]
        public void CoarseTierCadence_ThrottlesTheUpdateRate()
        {
            VisionLOD.Tiers = new[]
            {
                new VisionLODTier { MaxDistance = float.PositiveInfinity, Cadence = 3 }
            };

            VisioncastManager.Setup();
            TestVisionSource source = _scene.Source("source", Vector3.zero);
            _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);
            VisionTickHarness.Warmup();

            int before = source.ReceiveCount;
            VisionTickHarness.Run(12);
            int casts = source.ReceiveCount - before;

            Assert.GreaterOrEqual(casts, 3, "Roughly one cast every third tick");
            Assert.LessOrEqual(casts, 5);
        }

        [Test]
        public void DormantSources_NeverCast()
        {
            VisionLOD.Tiers = new[]
            {
                new VisionLODTier { MaxDistance = 10f, Cadence = 1 }
            };
            VisionRelevance.Provider = _ => 500f; // beyond every tier

            VisioncastManager.Setup();
            TestVisionSource source = _scene.Source("source", Vector3.zero);
            _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);

            VisionTickHarness.Run(8);

            Assert.AreEqual(0, source.ReceiveCount, "Out of relevance range - the source is dormant");
        }

        [Test]
        public void RelevanceDistance_SelectsThePerSourceCadence()
        {
            VisionLOD.Tiers = new[]
            {
                new VisionLODTier { MaxDistance = 10f, Cadence = 1 },
                new VisionLODTier { MaxDistance = 1000f, Cadence = 4 }
            };

            VisioncastManager.Setup();
            TestVisionSource near = _scene.Source("near", Vector3.zero);
            TestVisionSource far = _scene.Source("far", Vector3.zero);
            _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);
            VisionRelevance.Provider = s => s == far ? 500f : 0f;

            VisionTickHarness.Warmup();
            int nearBefore = near.ReceiveCount;
            int farBefore = far.ReceiveCount;
            VisionTickHarness.Run(12);

            Assert.AreEqual(nearBefore + 12, near.ReceiveCount, "The relevant source casts every tick");
            Assert.Less(far.ReceiveCount - farBefore, 12 - 2, "The distant source is throttled");
            Assert.Greater(far.ReceiveCount - farBefore, 0, "...but is not dormant");
        }

        [Test]
        public void StaleResults_AreDetectableViaTimeSinceUpdate()
        {
            VisionLOD.Tiers = new[]
            {
                new VisionLODTier { MaxDistance = float.PositiveInfinity, Cadence = 5 }
            };

            VisioncastManager.Setup();
            TestVisionSource source = _scene.Source("source", Vector3.zero);
            _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);

            VisionTickHarness.Run(12);

            Assert.Greater(source.ReceiveCount, 0, "A cadence-5 source still casts within 12 ticks");
            Assert.GreaterOrEqual(source.TimeSinceUpdate, 0f);
            Assert.LessOrEqual(
                source.TimeSinceUpdate,
                5f * VisionTickHarness.CONST_Delta + 1e-4f,
                "Staleness cannot exceed the source's own cadence");
        }

        #endregion LOD CADENCE
    }
}
