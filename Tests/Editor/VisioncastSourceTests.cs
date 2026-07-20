using NUnit.Framework;
using UnityEngine;

namespace GalaxyGourd.Visioncast.Tests
{
    /// <summary>
    /// The consumer half of a vision source, driven by injecting results straight into the back buffer:
    /// per-source double buffering, the filtered source's enter/exit sets and targeting, and the simple
    /// source's one-notification-per-actor contract. No physics or scheduling involved.
    /// </summary>
    [TestFixture]
    public class VisioncastSourceTests
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


        #region BUFFERING

        [Test]
        public void DeliverResults_PublishesTheBackBufferAndFlips()
        {
            TestVisionSource source = _scene.Source("source", Vector3.zero);
            BoxCollider target = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one);

            DataVisioncastResult first = source.ResultBackBuffer;
            VisionResultInjector.Deliver(source, 1f, VisionResultInjector.Observation.Visible(target));

            Assert.AreEqual(1, source.ReceiveCount);
            Assert.AreSame(first.Objects, source.LastResults.Objects, "LastResults is the buffer just written");
            Assert.AreNotSame(first.Objects, source.ResultBackBuffer.Objects, "The next write must target the other buffer");

            DataVisioncastResult second = source.ResultBackBuffer;
            VisionResultInjector.Deliver(source, 2f, VisionResultInjector.Observation.Visible(target));

            Assert.AreSame(second.Objects, source.LastResults.Objects);
            Assert.AreSame(first.Objects, source.ResultBackBuffer.Objects, "Buffers alternate");
        }

        [Test]
        public void DeliverResults_StampsTheUpdateTime()
        {
            TestVisionSource source = _scene.Source("source", Vector3.zero);
            BoxCollider target = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one);

            VisionResultInjector.Deliver(source, 12.5f, VisionResultInjector.Observation.Visible(target));

            Assert.AreEqual(12.5f, source.LastUpdatedTime, 1e-4f);
        }

        [Test]
        public void Source_ReportsWhatThePipelineWrote()
        {
            TestVisionSource source = _scene.Source("source", Vector3.zero);
            BoxCollider visible = _scene.Target("visible", new Vector3(0f, 0f, 5f), Vector3.one);
            BoxCollider occluded = _scene.Target("occluded", new Vector3(2f, 0f, 5f), Vector3.one);

            VisionResultInjector.Deliver(source, 1f,
                VisionResultInjector.Observation.Visible(visible),
                VisionResultInjector.Observation.Occluded(occluded));

            Assert.IsTrue(source.Sees(visible));
            Assert.IsFalse(source.Sees(occluded));
            Assert.AreEqual(2, source.LastObjects.Count);
        }

        #endregion BUFFERING


        #region FILTERED SOURCE

        [Test]
        public void Filtered_FirstSighting_IsNewlySeenAndTargeted()
        {
            TestFilteredVisionSource source = _scene.FilteredSource("source", Vector3.zero);
            BoxCollider target = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one);

            VisionResultInjector.Deliver(source, 1f, VisionResultInjector.Observation.Visible(target));

            Assert.AreEqual(1, source.VisionTargets.Count);
            Assert.IsTrue(source.VisionTargets[0].IsVisible);
            CollectionAssert.Contains(source.NewlySeenObjects, target);
            CollectionAssert.IsEmpty(source.NewlyLostObjects);
            Assert.AreSame(target, source.TargetedObject);
            Assert.AreEqual(1, source.PostFilterCount, "PostVisionFilter runs once per delivery");
        }

        [Test]
        public void Filtered_StillVisible_ProducesNoTransitions()
        {
            TestFilteredVisionSource source = _scene.FilteredSource("source", Vector3.zero);
            BoxCollider target = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one);

            VisionResultInjector.Deliver(source, 1f, VisionResultInjector.Observation.Visible(target));
            VisionResultInjector.Deliver(source, 2f, VisionResultInjector.Observation.Visible(target));

            CollectionAssert.IsEmpty(source.NewlySeenObjects);
            CollectionAssert.IsEmpty(source.NewlyLostObjects);
            Assert.AreEqual(1, source.VisionTargets.Count);
        }

        [Test]
        public void Filtered_TargetDroppedFromResults_IsNewlyLost()
        {
            TestFilteredVisionSource source = _scene.FilteredSource("source", Vector3.zero);
            BoxCollider target = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one);

            VisionResultInjector.Deliver(source, 1f, VisionResultInjector.Observation.Visible(target));
            VisionResultInjector.Deliver(source, 2f); // out of range entirely

            CollectionAssert.Contains(source.NewlyLostObjects, target);
            CollectionAssert.IsEmpty(source.NewlySeenObjects);
            Assert.AreEqual(0, source.VisionTargets.Count);
            Assert.IsNull(source.TargetedObject);
        }

        [Test]
        public void Filtered_TargetStillPresentButOccluded_IsNewlyLost()
        {
            TestFilteredVisionSource source = _scene.FilteredSource("source", Vector3.zero);
            BoxCollider target = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one);

            VisionResultInjector.Deliver(source, 1f, VisionResultInjector.Observation.Visible(target));
            VisionResultInjector.Deliver(source, 2f, VisionResultInjector.Observation.Occluded(target));

            CollectionAssert.Contains(source.NewlyLostObjects, target);
            Assert.AreEqual(1, source.VisionTargets.Count, "Still resolved, just not visible");
            Assert.IsFalse(source.VisionTargets[0].IsVisible);
            Assert.IsNull(source.TargetedObject, "An occluded target cannot be the focus");
        }

        [Test]
        public void Filtered_LostThenSeenAgain_ReportsBothTransitions()
        {
            TestFilteredVisionSource source = _scene.FilteredSource("source", Vector3.zero);
            BoxCollider target = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one);

            VisionResultInjector.Deliver(source, 1f, VisionResultInjector.Observation.Visible(target));
            VisionResultInjector.Deliver(source, 2f, VisionResultInjector.Observation.Occluded(target));
            VisionResultInjector.Deliver(source, 3f, VisionResultInjector.Observation.Visible(target));

            CollectionAssert.Contains(source.NewlySeenObjects, target);
            CollectionAssert.IsEmpty(source.NewlyLostObjects);
        }

        [Test]
        public void Filtered_TargetedObject_IsTheMostDirectVisibleTarget()
        {
            TestFilteredVisionSource source = _scene.FilteredSource("source", Vector3.zero);
            BoxCollider wide = _scene.Target("wide", new Vector3(3f, 0f, 5f), Vector3.one);
            BoxCollider centre = _scene.Target("centre", new Vector3(0f, 0f, 6f), Vector3.one);

            VisionResultInjector.Deliver(source, 1f,
                VisionResultInjector.Observation.Visible(wide, angle: 25f),
                VisionResultInjector.Observation.Visible(centre, angle: 3f));

            Assert.AreSame(centre, source.TargetedObject);
        }

        [Test]
        public void Filtered_TargetedObject_IgnoresOccludedTargets()
        {
            TestFilteredVisionSource source = _scene.FilteredSource("source", Vector3.zero);
            BoxCollider blocked = _scene.Target("blocked", new Vector3(0f, 0f, 5f), Vector3.one);
            BoxCollider visible = _scene.Target("visible", new Vector3(3f, 0f, 5f), Vector3.one);

            VisionResultInjector.Deliver(source, 1f,
                VisionResultInjector.Observation.Occluded(blocked, angle: 1f),
                VisionResultInjector.Observation.Visible(visible, angle: 25f));

            Assert.AreSame(visible, source.TargetedObject);
        }

        [Test]
        public void Filtered_MultiColliderActor_ChangingRepresentativeIsNotANewSighting()
        {
            GameObject actorGo = _scene.Empty("actor");
            TestVisibleObject actor = actorGo.AddComponent<TestVisibleObject>();
            TestFilteredVisionSource source = _scene.FilteredSource("source", Vector3.zero);
            BoxCollider head = _scene.Target("head", new Vector3(0f, 2f, 5f), Vector3.one, actor: actor);
            BoxCollider torso = _scene.Target("torso", new Vector3(0f, 0f, 5f), Vector3.one, actor: actor);

            VisionResultInjector.Deliver(source, 1f,
                VisionResultInjector.Observation.Visible(head, angle: 4f),
                VisionResultInjector.Observation.Occluded(torso, angle: 20f));
            CollectionAssert.Contains(source.NewlySeenObjects, head);

            // The torso becomes the most direct collider on the next update - same actor
            VisionResultInjector.Deliver(source, 2f,
                VisionResultInjector.Observation.Occluded(head, angle: 20f),
                VisionResultInjector.Observation.Visible(torso, angle: 4f));

            Assert.AreEqual(1, source.VisionTargets.Count);
            CollectionAssert.IsEmpty(source.NewlySeenObjects, "Same actor - not a new sighting");
            CollectionAssert.IsEmpty(source.NewlyLostObjects, "Same actor - not a loss");
            Assert.IsTrue(source.TryGetTarget(actor, out DataVisionSeenObject entry));
            Assert.IsTrue(entry.IsVisible);
        }

        [Test]
        public void Filtered_ResultWithNoObjectList_ClearsAndReportsEverythingLost()
        {
            TestFilteredVisionSource source = _scene.FilteredSource("source", Vector3.zero);
            BoxCollider target = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one);

            VisionResultInjector.Deliver(source, 1f, VisionResultInjector.Observation.Visible(target));

            // Defensive path: a result that carries no object list at all
            source.FilterDirect(new DataVisioncastResult());

            Assert.AreEqual(0, source.VisionTargets.Count);
            Assert.IsNull(source.TargetedObject);
            CollectionAssert.Contains(source.NewlyLostObjects, target);
        }

        #endregion FILTERED SOURCE


        #region SIMPLE SOURCE

        [Test]
        public void Simple_NotifiesVisibleObjectsOnce()
        {
            GameObject sourceGo = _scene.Empty("source");
            VisioncastSourceSimple source = sourceGo.AddComponent<VisioncastSourceSimple>();

            GameObject targetGo = _scene.Box("target", new Vector3(0f, 0f, 5f), Vector3.one);
            TestVisibleObject visible = targetGo.AddComponent<TestVisibleObject>();
            BoxCollider col = targetGo.GetComponent<BoxCollider>();
            _scene.Register(col);

            VisionResultInjector.Deliver(source, 1f, VisionResultInjector.Observation.Visible(col));

            Assert.AreEqual(1, visible.SeenCount);
            Assert.AreSame(source, visible.LastSeenBy);
        }

        [Test]
        public void Simple_MultiColliderActor_IsNotifiedOncePerUpdate()
        {
            GameObject sourceGo = _scene.Empty("source");
            VisioncastSourceSimple source = sourceGo.AddComponent<VisioncastSourceSimple>();

            GameObject actorGo = _scene.Empty("actor");
            TestVisibleObject visible = actorGo.AddComponent<TestVisibleObject>();
            BoxCollider head = _scene.Target("head", new Vector3(0f, 2f, 5f), Vector3.one, actor: visible);
            BoxCollider torso = _scene.Target("torso", new Vector3(0f, 0f, 5f), Vector3.one, actor: visible);

            VisionResultInjector.Deliver(source, 1f,
                VisionResultInjector.Observation.Visible(head),
                VisionResultInjector.Observation.Visible(torso));

            Assert.AreEqual(1, visible.SeenCount, "One notification per actor, not per collider");
        }

        [Test]
        public void Simple_OccludedTarget_IsNotNotified()
        {
            GameObject sourceGo = _scene.Empty("source");
            VisioncastSourceSimple source = sourceGo.AddComponent<VisioncastSourceSimple>();

            GameObject targetGo = _scene.Box("target", new Vector3(0f, 0f, 5f), Vector3.one);
            TestVisibleObject visible = targetGo.AddComponent<TestVisibleObject>();
            BoxCollider col = targetGo.GetComponent<BoxCollider>();
            _scene.Register(col);

            VisionResultInjector.Deliver(source, 1f, VisionResultInjector.Observation.Occluded(col));

            Assert.AreEqual(0, visible.SeenCount);
        }

        [Test]
        public void Simple_TargetWithoutAVisibleObjectComponent_IsSkipped()
        {
            GameObject sourceGo = _scene.Empty("source");
            VisioncastSourceSimple source = sourceGo.AddComponent<VisioncastSourceSimple>();
            BoxCollider col = _scene.Target("plain", new Vector3(0f, 0f, 5f), Vector3.one);

            Assert.DoesNotThrow(() =>
                VisionResultInjector.Deliver(source, 1f, VisionResultInjector.Observation.Visible(col)));
        }

        [Test]
        public void Simple_OverridesApplyToTheCastConfiguration()
        {
            GameObject sourceGo = _scene.Empty("source");
            VisioncastSourceSimple source = sourceGo.AddComponent<VisioncastSourceSimple>();

            source.OverrideRange(37f);
            source.OverrideFieldOfView(12f);

            Assert.AreEqual(37f, source.Range, 1e-4f);
            Assert.AreEqual(12f, source.FieldOfView, 1e-4f);
        }

        #endregion SIMPLE SOURCE
    }
}
