using NUnit.Framework;
using UnityEngine;

namespace GalaxyGourd.Visioncast.Tests
{
    /// <summary>
    /// Aggregation of several child sources into one per-target answer: the Max / Sum / Average policies,
    /// the visibility (occlusion) vs coverage (in-beam) split that stealth exposure reads, transitions
    /// across combines, and the rule that a disabled child contributes nothing.
    /// </summary>
    [TestFixture]
    public class VisioncastSourceCompoundTests
    {
        #region FIXTURE

        private VisionTestScene _scene;
        private VisioncastSourceCompound _compound;
        private TestFilteredVisionSource _left;
        private TestFilteredVisionSource _right;
        private BoxCollider _target;

        [SetUp]
        public void SetUp()
        {
            VisionTestStatics.ResetAll();
            _scene = new VisionTestScene();

            GameObject compoundGo = _scene.Empty("compound");
            _compound = compoundGo.AddComponent<VisioncastSourceCompound>();
            _compound.AutoCombine = false; // combines are driven explicitly by the tests

            _left = _scene.FilteredSource("left", new Vector3(-5f, 0f, 0f));
            _right = _scene.FilteredSource("right", new Vector3(5f, 0f, 0f));
            _compound.AddSource(_left);
            _compound.AddSource(_right);

            _target = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one);
        }

        [TearDown]
        public void TearDown()
        {
            _scene.Dispose();
            VisionTestStatics.ResetAll();
        }

        /// <summary>Delivers a partial observation of the shared target to a child source.</summary>
        private void See(TestFilteredVisionSource source, int visiblePoints, int samples, float time = 1f, int lit = -1)
        {
            VisionResultInjector.Deliver(source, time,
                VisionResultInjector.Observation.Partial(_target, visiblePoints, samples, lit: lit));
        }

        #endregion FIXTURE


        #region SOURCES

        [Test]
        public void AddSource_IsDeDuplicatedAndRemovable()
        {
            _compound.AddSource(_left);
            See(_left, 3, 6);
            _compound.Combine();

            Assert.AreEqual(1, _compound.VisionTargets.Count, "A source added twice must not contribute twice");

            _compound.RemoveSource(_left);
            _compound.Combine();

            Assert.AreEqual(0, _compound.VisionTargets.Count);
        }

        [Test]
        public void Combine_SkipsDisabledSources()
        {
            See(_left, 6, 6);
            See(_right, 3, 6);
            _left.enabled = false;

            _compound.Combine();

            Assert.IsTrue(_compound.TryGetVisibility(_target, out float visibility));
            Assert.AreEqual(0.5f, visibility, 1e-4f, "Only the enabled child contributes - its data is the live one");
        }

        [Test]
        public void Combine_WithNoContributions_ReportsNothing()
        {
            _compound.Combine();

            Assert.AreEqual(0, _compound.VisionTargets.Count);
            Assert.IsFalse(_compound.TryGetVisibility(_target, out float visibility));
            Assert.AreEqual(0f, visibility);
            Assert.IsFalse(_compound.IsVisible(_target));
        }

        #endregion SOURCES


        #region AGGREGATION

        [Test]
        public void Combine_Max_TakesTheStrongestContribution()
        {
            _compound.Aggregation = VisibilityAggregation.Max;
            See(_left, 3, 6);   // 0.5
            See(_right, 6, 6);  // 1.0

            _compound.Combine();

            Assert.IsTrue(_compound.TryGetVisibility(_target, out float visibility));
            Assert.AreEqual(1f, visibility, 1e-4f);
        }

        [Test]
        public void Combine_Sum_AccumulatesAndClamps()
        {
            _compound.Aggregation = VisibilityAggregation.Sum;
            See(_left, 2, 6);   // 0.333
            See(_right, 2, 6);  // 0.333

            _compound.Combine();

            Assert.IsTrue(_compound.TryGetVisibility(_target, out float visibility));
            Assert.AreEqual(2f / 3f, visibility, 1e-3f);

            See(_left, 5, 6);   // 0.833
            See(_right, 5, 6);  // 0.833 -> 1.667, clamped
            _compound.Combine();

            Assert.IsTrue(_compound.TryGetVisibility(_target, out visibility));
            Assert.AreEqual(1f, visibility, 1e-4f, "Cumulative exposure is clamped to 1");
        }

        [Test]
        public void Combine_Average_DividesByTheSeeingChildren()
        {
            _compound.Aggregation = VisibilityAggregation.Average;
            See(_left, 6, 6);   // 1.0, visible
            See(_right, 3, 6);  // 0.5, visible

            _compound.Combine();

            Assert.IsTrue(_compound.TryGetVisibility(_target, out float visibility));
            Assert.AreEqual(0.75f, visibility, 1e-4f);
        }

        [Test]
        public void Combine_MergesCountsAcrossChildren()
        {
            See(_left, 2, 6);
            See(_right, 4, 6);

            _compound.Combine();

            Assert.AreEqual(1, _compound.VisionTargets.Count, "One entry per target identity");
            DataVisionSeenTarget entry = _compound.VisionTargets[0];
            Assert.AreEqual(6, entry.VisiblePointCount);
            Assert.AreEqual(12, entry.SampleCount);
            Assert.IsTrue(entry.IsVisible);
        }

        [Test]
        public void Combine_CoverageUsesLitCountsIndependentlyOfVisibility()
        {
            _compound.Aggregation = VisibilityAggregation.Max;
            // Same reached count, but only half of them are inside the beam
            See(_left, 6, 6, lit: 3);
            See(_right, 6, 6, lit: 6);

            _compound.Combine();

            Assert.IsTrue(_compound.TryGetCoverage(_target, out float coverage));
            Assert.AreEqual(1f, coverage, 1e-4f, "Max coverage across the lights");

            _compound.Aggregation = VisibilityAggregation.Average;
            _compound.Combine();

            Assert.IsTrue(_compound.TryGetCoverage(_target, out coverage));
            Assert.AreEqual(0.75f, coverage, 1e-4f, "(0.5 + 1.0) / 2");
        }

        [Test]
        public void Combine_TracksTheFreshestContributingUpdateTime()
        {
            See(_left, 6, 6, time: 4f);
            See(_right, 6, 6, time: 9f);

            _compound.Combine();

            Assert.AreEqual(9f, _compound.VisionTargets[0].LastUpdatedTime, 1e-4f);
        }

        #endregion AGGREGATION


        #region IDENTITY

        [Test]
        public void Combine_GroupsAnActorsCollidersIntoOneTarget()
        {
            GameObject actorGo = _scene.Empty("actor");
            TestVisibleObject actor = actorGo.AddComponent<TestVisibleObject>();
            BoxCollider head = _scene.Target("head", new Vector3(0f, 2f, 5f), Vector3.one, actor: actor);
            BoxCollider torso = _scene.Target("torso", new Vector3(0f, 0f, 5f), Vector3.one, actor: actor);

            VisionResultInjector.Deliver(_left, 1f, VisionResultInjector.Observation.Visible(head));
            VisionResultInjector.Deliver(_right, 1f, VisionResultInjector.Observation.Visible(torso));

            _compound.Combine();

            Assert.AreEqual(1, _compound.VisionTargets.Count);
            Assert.AreSame(actor, _compound.VisionTargets[0].Actor);
            Assert.IsTrue(_compound.IsVisible(head), "Either collider resolves to the same target");
            Assert.IsTrue(_compound.IsVisible(torso));
            Assert.IsTrue(_compound.TryGetVisibility(actor, out float byActor));
            Assert.AreEqual(1f, byActor, 1e-4f);
        }

        [Test]
        public void Queries_ForUnknownTargets_ReturnFalse()
        {
            BoxCollider stranger = _scene.Target("stranger", new Vector3(20f, 0f, 0f), Vector3.one);
            See(_left, 6, 6);
            _compound.Combine();

            Assert.IsFalse(_compound.TryGetVisibility(stranger, out float visibility));
            Assert.AreEqual(0f, visibility);
            Assert.IsFalse(_compound.TryGetCoverage(stranger, out float coverage));
            Assert.AreEqual(0f, coverage);
            Assert.IsFalse(_compound.IsVisible(stranger));
            Assert.IsFalse(_compound.TryGetVisibility((Collider)null, out _));
            Assert.IsFalse(_compound.TryGetCoverage((Collider)null, out _));
        }

        #endregion IDENTITY


        #region TRANSITIONS

        [Test]
        public void Combine_ReportsNewlySeenOnceAndNewlyLostWhenAllChildrenLoseIt()
        {
            See(_left, 6, 6);
            _compound.Combine();
            CollectionAssert.Contains(_compound.NewlySeenObjects, _target);

            See(_left, 6, 6, time: 2f);
            _compound.Combine();
            CollectionAssert.IsEmpty(_compound.NewlySeenObjects, "Continuing visibility is not a transition");
            CollectionAssert.IsEmpty(_compound.NewlyLostObjects);

            VisionResultInjector.Deliver(_left, 3f); // nothing in range any more
            _compound.Combine();

            CollectionAssert.Contains(_compound.NewlyLostObjects, _target);
            Assert.IsFalse(_compound.IsVisible(_target));
        }

        [Test]
        public void Combine_TargetStaysSeenWhileAnyChildSeesIt()
        {
            See(_left, 6, 6);
            See(_right, 6, 6);
            _compound.Combine();

            // The left source loses it; the right one still sees it
            VisionResultInjector.Deliver(_left, 2f);
            See(_right, 6, 6, time: 2f);
            _compound.Combine();

            Assert.IsTrue(_compound.IsVisible(_target));
            CollectionAssert.IsEmpty(_compound.NewlyLostObjects);
        }

        [Test]
        public void Combine_OccludedForEveryChild_IsLost()
        {
            See(_left, 6, 6);
            _compound.Combine();

            VisionResultInjector.Deliver(_left, 2f, VisionResultInjector.Observation.Occluded(_target));
            _compound.Combine();

            Assert.IsFalse(_compound.IsVisible(_target));
            CollectionAssert.Contains(_compound.NewlyLostObjects, _target);
            Assert.AreEqual(1, _compound.VisionTargets.Count, "Still resolved, just not visible");
        }

        #endregion TRANSITIONS
    }
}
