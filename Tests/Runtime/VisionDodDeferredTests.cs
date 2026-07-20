using NUnit.Framework;
using UnityEngine;

namespace GalaxyGourd.Visioncast.Tests
{
    /// <summary>
    /// The deferred DoD path: schedule the raycast batch at the end of a step and complete it at the top
    /// of the next, so the cast hides across the gap. Ordering is load-bearing here - a second schedule
    /// before the pending batch completes would reuse native buffers still in flight.
    /// </summary>
    [TestFixture]
    public class VisionDodDeferredTests
    {
        #region FIXTURE

        private VisionTestScene _scene;

        [SetUp]
        public void SetUp()
        {
            VisionTestStatics.ResetAll();
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

        #endregion FIXTURE


        #region DEFERRED CYCLE

        [Test]
        public void DeferredCycle_DeliversResults()
        {
            TestVisionSource source = _scene.Source("source", Vector3.zero);
            BoxCollider target = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);

            VisionTickHarness.RunDeferred(3);

            Assert.Greater(source.ReceiveCount, 0);
            Assert.IsTrue(source.Sees(target));
        }

        [Test]
        public void DeferredCycle_ScheduleAloneDoesNotDeliver()
        {
            TestVisionSource source = _scene.Source("source", Vector3.zero);
            _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);

            VisionTickHarness.RunDeferred(2); // gets the source registered and a batch in flight
            int received = source.ReceiveCount;

            VisioncastManager.ScheduleVisioncasts(VisionTickHarness.CONST_Delta);

            Assert.AreEqual(received, source.ReceiveCount, "Results arrive on the completing call, not the scheduling one");
        }

        [Test]
        public void SchedulingTwiceWithoutCompleting_IsIgnored()
        {
            TestVisionSource source = _scene.Source("source", Vector3.zero);
            _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);
            VisionTickHarness.RunDeferred(2);

            VisioncastManager.CompleteVisioncasts();
            VisioncastManager.ScheduleVisioncasts(VisionTickHarness.CONST_Delta);
            int builds = source.BeforeVisioncastCount;

            VisioncastManager.ScheduleVisioncasts(VisionTickHarness.CONST_Delta);

            Assert.AreEqual(builds, source.BeforeVisioncastCount,
                "A batch is already in flight - the second schedule must be dropped, not overwrite its buffers");

            VisioncastManager.CompleteVisioncasts();
            Assert.Greater(source.ReceiveCount, 0);
        }

        [Test]
        public void CompletingWithNothingPending_IsHarmless()
        {
            _scene.Source("source", Vector3.zero);

            Assert.DoesNotThrow(VisioncastManager.CompleteVisioncasts);
            Assert.DoesNotThrow(VisioncastManager.CompleteVisioncasts);
        }

        [Test]
        public void DeferredCycle_WithNoTargets_StillDeliversAnEmptyResult()
        {
            TestVisionSource source = _scene.Source("source", Vector3.zero);

            VisionTickHarness.RunDeferred(3);

            Assert.Greater(source.ReceiveCount, 0, "A source with nothing in range still gets an (empty) update");
            Assert.AreEqual(0, source.LastObjects.Count);
        }

        [Test]
        public void DeferredCycle_TargetsBecomeVisibleAsTheyComeIntoView()
        {
            TestVisionSource source = _scene.Source("source", Vector3.zero, Quaternion.identity, 20f, 30f);
            BoxCollider target = _scene.Target("target", new Vector3(0f, 0f, -6f), Vector3.one * 2f);

            VisionTickHarness.RunDeferred(3);
            Assert.IsFalse(source.Sees(target), "Starts behind the source");

            target.transform.position = new Vector3(0f, 0f, 6f);
            VisionTickHarness.RunDeferred(3);

            Assert.IsTrue(source.Sees(target));
        }

        [Test]
        public void DisposingWithABatchInFlight_IsSafe()
        {
            _scene.Source("source", Vector3.zero);
            _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);
            VisionTickHarness.RunDeferred(2);

            // A batch is pending; tearing the system down must complete it before freeing native buffers
            Assert.DoesNotThrow(VisioncastManager.Dispose);
        }

        #endregion DEFERRED CYCLE


        #region MODE GUARDS

        [Test]
        public void DeferredEntryPoints_AreNoOpsInManagedMode()
        {
            VisioncastManager.Dispose();
            VisionPipeline.UseManaged();
            VisioncastManager.Setup();

            TestVisionSource source = _scene.Source("source", Vector3.zero);
            _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one * 2f);

            VisionTickHarness.RunDeferred(4);

            Assert.AreEqual(0, source.ReceiveCount, "The deferred path only drives the DoD pipeline");
        }

        #endregion MODE GUARDS
    }
}
