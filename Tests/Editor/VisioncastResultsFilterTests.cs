using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace GalaxyGourd.Visioncast.Tests
{
    /// <summary>
    /// The raw-to-refined resolve step: per-collider observations collapse to one entry per target
    /// identity, counts merge, the representative collider is the most-direct one, and the
    /// visible transition is diffed against the previous update.
    /// </summary>
    [TestFixture]
    public class VisioncastResultsFilterTests
    {
        #region FIXTURE

        private VisionTestScene _scene;
        private List<DataVisionSeenObject> _previous;
        private List<DataVisionSeenObject> _output;
        private Dictionary<Component, int> _previousIndex;
        private Dictionary<Component, int> _actorIndex;

        [SetUp]
        public void SetUp()
        {
            _scene = new VisionTestScene();
            _previous = new List<DataVisionSeenObject>();
            _output = new List<DataVisionSeenObject>();
            _previousIndex = new Dictionary<Component, int>();
            _actorIndex = new Dictionary<Component, int>();
        }

        [TearDown]
        public void TearDown()
        {
            _scene.Dispose();
        }

        /// <summary>Builds a raw result with the parallel per-object lists the pipelines produce.</summary>
        private static DataVisioncastResult Raw(params VisionResultInjector.Observation[] observations)
        {
            DataVisioncastResult result = new DataVisioncastResult
            {
                Objects = new List<Collider>(),
                VisiblePoints = new List<List<Vector3>>(),
                VisiblePointCounts = new List<int>(),
                SampleCounts = new List<int>(),
                InConeCounts = new List<int>(),
                LitCounts = new List<int>(),
                Distances = new List<float>(),
                Angles = new List<float>()
            };

            foreach (VisionResultInjector.Observation o in observations)
            {
                result.Objects.Add(o.Collider);
                result.VisiblePointCounts.Add(o.VisiblePoints);
                result.SampleCounts.Add(o.SampleCount);
                result.InConeCounts.Add(o.InConeCount);
                result.LitCounts.Add(o.LitCount);
                result.Distances.Add(o.Distance);
                result.Angles.Add(o.Angle);
            }

            return result;
        }

        private void Resolve(DataVisioncastResult raw)
        {
            VisioncastResultsFilter.Resolve(raw, _previous, _previousIndex, _actorIndex, _output);
        }

        #endregion FIXTURE


        #region SINGLE TARGET

        [Test]
        public void Resolve_StandaloneCollider_ProducesOneEntryKeyedByItself()
        {
            BoxCollider col = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one);

            Resolve(Raw(VisionResultInjector.Observation.Partial(col, 3, 6, distance: 5f, angle: 12f)));

            Assert.AreEqual(1, _output.Count);
            DataVisionSeenObject entry = _output[0];
            Assert.AreSame(col, entry.Actor);
            Assert.AreSame(col, entry.ResultObject);
            Assert.IsTrue(entry.IsVisible);
            Assert.AreEqual(3, entry.VisiblePointCount);
            Assert.AreEqual(6, entry.SampleCount);
            Assert.AreEqual(0.5f, entry.Visibility, 1e-4f);
            Assert.AreEqual(5f, entry.Distance, 1e-4f);
            Assert.AreEqual(12f, entry.Angle, 1e-4f);
        }

        [Test]
        public void Resolve_NoVisiblePoints_IsReportedButNotVisible()
        {
            BoxCollider col = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one);

            Resolve(Raw(VisionResultInjector.Observation.Occluded(col)));

            Assert.AreEqual(1, _output.Count, "An occluded but in-range target is still reported");
            Assert.IsFalse(_output[0].IsVisible);
            Assert.AreEqual(0f, _output[0].Visibility, 1e-4f);
            Assert.IsFalse(_output[0].JustBecameVisible);
        }

        [Test]
        public void Resolve_ClampsVisibilityToOne()
        {
            // The managed straight-ahead ray can add a confirmed point on top of the sample count
            BoxCollider col = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one);

            Resolve(Raw(VisionResultInjector.Observation.Partial(col, 8, 6)));

            Assert.AreEqual(1f, _output[0].Visibility, 1e-4f);
        }

        [Test]
        public void Resolve_ZeroSamples_YieldsZeroVisibility()
        {
            BoxCollider col = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one);

            Resolve(Raw(VisionResultInjector.Observation.Partial(col, 0, 0)));

            Assert.AreEqual(0f, _output[0].Visibility, 1e-4f, "No division by a zero denominator");
        }

        [Test]
        public void Resolve_CarriesTheConeAndLitCountsThrough()
        {
            BoxCollider col = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one);

            Resolve(Raw(VisionResultInjector.Observation.Partial(col, 5, 8, inCone: 6, lit: 4)));

            Assert.AreEqual(6, _output[0].InConeCount);
            Assert.AreEqual(4, _output[0].LitCount);
        }

        #endregion SINGLE TARGET


        #region MULTI-COLLIDER ACTORS

        [Test]
        public void Resolve_CollidersOfOneActor_MergeIntoASingleEntry()
        {
            GameObject actorGo = _scene.Empty("actor");
            TestVisibleObject actor = actorGo.AddComponent<TestVisibleObject>();
            BoxCollider head = _scene.Target("head", new Vector3(0f, 2f, 5f), Vector3.one, actor: actor);
            BoxCollider torso = _scene.Target("torso", new Vector3(0f, 0f, 6f), Vector3.one, actor: actor);

            Resolve(Raw(
                VisionResultInjector.Observation.Partial(head, 2, 6, distance: 5.4f, angle: 20f),
                VisionResultInjector.Observation.Partial(torso, 4, 6, distance: 6.1f, angle: 8f)));

            Assert.AreEqual(1, _output.Count, "One entry per actor, not per collider");
            DataVisionSeenObject entry = _output[0];
            Assert.AreSame(actor, entry.Actor);
            Assert.AreSame(torso, entry.ResultObject, "Representative is the most-directly-observed collider");
            Assert.AreEqual(6, entry.VisiblePointCount, "Counts sum across the actor's colliders");
            Assert.AreEqual(12, entry.SampleCount);
            Assert.AreEqual(0.5f, entry.Visibility, 1e-4f);
            Assert.AreEqual(5.4f, entry.Distance, 1e-4f, "Distance is the closest contributing observation");
            Assert.AreEqual(8f, entry.Angle, 1e-4f);
        }

        [Test]
        public void Resolve_ActorIsVisibleIfAnyColliderIs()
        {
            GameObject actorGo = _scene.Empty("actor");
            TestVisibleObject actor = actorGo.AddComponent<TestVisibleObject>();
            BoxCollider head = _scene.Target("head", new Vector3(0f, 2f, 5f), Vector3.one, actor: actor);
            BoxCollider torso = _scene.Target("torso", new Vector3(0f, 0f, 6f), Vector3.one, actor: actor);

            Resolve(Raw(
                VisionResultInjector.Observation.Occluded(head, angle: 4f),
                VisionResultInjector.Observation.Partial(torso, 1, 6, angle: 30f)));

            Assert.AreEqual(1, _output.Count);
            Assert.IsTrue(_output[0].IsVisible);
            Assert.AreSame(head, _output[0].ResultObject, "Representative tracks angle, not visibility");
        }

        [Test]
        public void Resolve_SeparateActors_StaySeparateEntries()
        {
            GameObject aGo = _scene.Empty("actorA");
            GameObject bGo = _scene.Empty("actorB");
            TestVisibleObject a = aGo.AddComponent<TestVisibleObject>();
            TestVisibleObject b = bGo.AddComponent<TestVisibleObject>();
            BoxCollider colA = _scene.Target("a", new Vector3(-2f, 0f, 5f), Vector3.one, actor: a);
            BoxCollider colB = _scene.Target("b", new Vector3(2f, 0f, 5f), Vector3.one, actor: b);

            Resolve(Raw(
                VisionResultInjector.Observation.Visible(colA),
                VisionResultInjector.Observation.Visible(colB)));

            Assert.AreEqual(2, _output.Count);
        }

        #endregion MULTI-COLLIDER ACTORS


        #region TRANSITIONS

        [Test]
        public void Resolve_VisibleAfterNotBeingVisible_FlagsJustBecameVisible()
        {
            BoxCollider col = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one);

            Resolve(Raw(VisionResultInjector.Observation.Visible(col)));
            Assert.IsTrue(_output[0].JustBecameVisible, "First sighting is a transition");

            // Feed the result back as the previous update
            _previous = new List<DataVisionSeenObject>(_output);
            Resolve(Raw(VisionResultInjector.Observation.Visible(col)));

            Assert.IsTrue(_output[0].IsVisible);
            Assert.IsFalse(_output[0].JustBecameVisible, "Still visible is not a new transition");
        }

        [Test]
        public void Resolve_PreviouslyOccluded_CountsAsANewSighting()
        {
            BoxCollider col = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one);

            Resolve(Raw(VisionResultInjector.Observation.Occluded(col)));
            _previous = new List<DataVisionSeenObject>(_output);

            Resolve(Raw(VisionResultInjector.Observation.Visible(col)));

            Assert.IsTrue(_output[0].JustBecameVisible);
        }

        [Test]
        public void Resolve_TransitionKeysOffTheActor_NotTheRepresentativeCollider()
        {
            GameObject actorGo = _scene.Empty("actor");
            TestVisibleObject actor = actorGo.AddComponent<TestVisibleObject>();
            BoxCollider head = _scene.Target("head", new Vector3(0f, 2f, 5f), Vector3.one, actor: actor);
            BoxCollider torso = _scene.Target("torso", new Vector3(0f, 0f, 6f), Vector3.one, actor: actor);

            Resolve(Raw(VisionResultInjector.Observation.Visible(head, angle: 5f)));
            _previous = new List<DataVisionSeenObject>(_output);

            // Next update the torso is the most direct collider - same actor, so no re-sighting
            Resolve(Raw(VisionResultInjector.Observation.Visible(torso, angle: 3f)));

            Assert.AreSame(torso, _output[0].ResultObject);
            Assert.IsFalse(_output[0].JustBecameVisible, "A changed representative collider is not a new target");
        }

        #endregion TRANSITIONS


        #region ROBUSTNESS

        [Test]
        public void Resolve_SkipsNullAndDestroyedColliders()
        {
            BoxCollider live = _scene.Target("live", new Vector3(0f, 0f, 5f), Vector3.one);
            GameObject doomedGo = _scene.Box("doomed", new Vector3(1f, 0f, 5f), Vector3.one);
            BoxCollider doomed = doomedGo.GetComponent<BoxCollider>();

            DataVisioncastResult raw = Raw(
                VisionResultInjector.Observation.Visible(doomed),
                VisionResultInjector.Observation.Visible(null),
                VisionResultInjector.Observation.Visible(live));

            VisionTestScene.DestroyObject(doomedGo);

            Assert.DoesNotThrow(() => Resolve(raw));
            Assert.AreEqual(1, _output.Count, "Only the live collider resolves to an identity");
            Assert.AreSame(live, _output[0].Actor);
        }

        [Test]
        public void Resolve_ClearsCallerBuffersBeforeRefilling()
        {
            BoxCollider col = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one);
            _output.Add(new DataVisionSeenObject { Actor = col, IsVisible = true });
            _actorIndex[col] = 99;

            Resolve(Raw(VisionResultInjector.Observation.Visible(col)));

            Assert.AreEqual(1, _output.Count, "Stale output entries must be dropped");
            Assert.AreEqual(0, _actorIndex[col], "The actor index is rebuilt for this update");
        }

        [Test]
        public void Resolve_EmptyResults_ProducesEmptyOutput()
        {
            _output.Add(new DataVisionSeenObject());

            Resolve(Raw());

            Assert.AreEqual(0, _output.Count);
        }

        [Test]
        public void DataSeenContainsObject_MatchesOnTheRepresentativeCollider()
        {
            BoxCollider col = _scene.Target("target", new Vector3(0f, 0f, 5f), Vector3.one);
            BoxCollider other = _scene.Target("other", new Vector3(4f, 0f, 5f), Vector3.one);
            Resolve(Raw(VisionResultInjector.Observation.Visible(col)));

            Assert.IsTrue(VisioncastResultsFilter.DataSeenContainsObject(_output, col));
            Assert.IsFalse(VisioncastResultsFilter.DataSeenContainsObject(_output, other));
        }

        #endregion ROBUSTNESS
    }
}
