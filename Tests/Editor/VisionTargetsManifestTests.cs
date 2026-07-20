using NUnit.Framework;
using UnityEngine;

namespace GalaxyGourd.Visioncast.Tests
{
    /// <summary>
    /// The target registry: membership, the collider -> actor identity mapping consumers key off, and the
    /// dense id registry the DoD path indexes its native buffers by (including the swap-remove that keeps
    /// ids contiguous). Ids are asserted relative to the count on entry, so the fixture never depends on
    /// a globally empty manifest.
    /// </summary>
    [TestFixture]
    public class VisionTargetsManifestTests
    {
        #region FIXTURE

        private VisionTestScene _scene;
        private int _baseId;
        private int _baseVersion;

        [SetUp]
        public void SetUp()
        {
            _scene = new VisionTestScene();
            _baseId = VisionTargetsManifest.TargetCount;
            _baseVersion = VisionTargetsManifest.Version;
        }

        [TearDown]
        public void TearDown()
        {
            _scene.Dispose();
            Assert.AreEqual(_baseId, VisionTargetsManifest.TargetCount, "Fixture leaked a registered target");
        }

        /// <summary>A collider that is NOT auto-registered, so registration itself can be tested.</summary>
        private BoxCollider LooseCollider(string name, Vector3 position = default)
        {
            return _scene.Box(name, position, Vector3.one).GetComponent<BoxCollider>();
        }

        #endregion FIXTURE


        #region MEMBERSHIP

        [Test]
        public void Register_AddsColliderToTheManifest()
        {
            BoxCollider col = LooseCollider("target");
            Assert.IsFalse(VisionTargetsManifest.Manifest.Contains(col));

            _scene.Register(col);

            Assert.IsTrue(VisionTargetsManifest.Manifest.Contains(col));
            Assert.AreEqual(_baseId + 1, VisionTargetsManifest.TargetCount);
            Assert.AreNotEqual(_baseVersion, VisionTargetsManifest.Version, "Version must bump so native mirrors rebuild");
        }

        [Test]
        public void Register_Null_IsIgnored()
        {
            Assert.DoesNotThrow(() => VisionTargetsManifest.Register(null));
            Assert.AreEqual(_baseId, VisionTargetsManifest.TargetCount);
            Assert.AreEqual(_baseVersion, VisionTargetsManifest.Version);
        }

        [Test]
        public void Register_Twice_IsIdempotent()
        {
            BoxCollider col = LooseCollider("target");
            _scene.Register(col);
            int versionAfterFirst = VisionTargetsManifest.Version;

            _scene.Register(col);

            Assert.AreEqual(_baseId + 1, VisionTargetsManifest.TargetCount, "No duplicate id slot");
            Assert.AreEqual(versionAfterFirst, VisionTargetsManifest.Version, "A no-op re-register must not bump the version");
        }

        [Test]
        public void Unregister_RemovesMembershipAndId()
        {
            BoxCollider col = LooseCollider("target");
            _scene.Register(col);

            _scene.Unregister(col);

            Assert.IsFalse(VisionTargetsManifest.Manifest.Contains(col));
            Assert.AreEqual(-1, VisionTargetsManifest.GetId(col));
            Assert.AreEqual(_baseId, VisionTargetsManifest.TargetCount);
        }

        [Test]
        public void Unregister_UnknownCollider_IsIgnored()
        {
            BoxCollider col = LooseCollider("never-registered");

            Assert.DoesNotThrow(() => VisionTargetsManifest.Unregister(col));
            Assert.AreEqual(_baseVersion, VisionTargetsManifest.Version);
        }

        #endregion MEMBERSHIP


        #region ID REGISTRY

        [Test]
        public void Register_AssignsDenseIdsInRegistrationOrder()
        {
            BoxCollider a = LooseCollider("a");
            BoxCollider b = LooseCollider("b");
            BoxCollider c = LooseCollider("c");

            _scene.Register(a);
            _scene.Register(b);
            _scene.Register(c);

            Assert.AreEqual(_baseId + 0, VisionTargetsManifest.GetId(a));
            Assert.AreEqual(_baseId + 1, VisionTargetsManifest.GetId(b));
            Assert.AreEqual(_baseId + 2, VisionTargetsManifest.GetId(c));
            Assert.AreSame(a, VisionTargetsManifest.GetCollider(_baseId + 0));
            Assert.AreSame(c, VisionTargetsManifest.GetCollider(_baseId + 2));
            Assert.AreEqual(_baseId + 3, VisionTargetsManifest.TargetsById.Count);
        }

        [Test]
        public void Unregister_SwapRemovesToKeepIdsContiguous()
        {
            BoxCollider a = LooseCollider("a");
            BoxCollider b = LooseCollider("b");
            BoxCollider c = LooseCollider("c");
            _scene.Register(a);
            _scene.Register(b);
            _scene.Register(c);

            _scene.Unregister(a); // the LAST target takes the freed slot

            Assert.AreEqual(_baseId + 2, VisionTargetsManifest.TargetCount);
            Assert.AreEqual(-1, VisionTargetsManifest.GetId(a));
            Assert.AreEqual(_baseId + 0, VisionTargetsManifest.GetId(c), "c was swapped into a's freed id");
            Assert.AreEqual(_baseId + 1, VisionTargetsManifest.GetId(b), "b's id is untouched");
            Assert.AreSame(c, VisionTargetsManifest.GetCollider(_baseId + 0));
        }

        [Test]
        public void Unregister_LastTarget_NeedsNoSwap()
        {
            BoxCollider a = LooseCollider("a");
            BoxCollider b = LooseCollider("b");
            _scene.Register(a);
            _scene.Register(b);

            _scene.Unregister(b);

            Assert.AreEqual(_baseId + 0, VisionTargetsManifest.GetId(a));
            Assert.AreEqual(_baseId + 1, VisionTargetsManifest.TargetCount);
        }

        [Test]
        public void GetId_UnknownOrNull_IsMinusOne()
        {
            Assert.AreEqual(-1, VisionTargetsManifest.GetId(null));
            Assert.AreEqual(-1, VisionTargetsManifest.GetId(LooseCollider("loose")));
        }

        [Test]
        public void GetCollider_OutOfRange_IsNull()
        {
            Assert.IsNull(VisionTargetsManifest.GetCollider(-1));
            Assert.IsNull(VisionTargetsManifest.GetCollider(VisionTargetsManifest.TargetCount));
            Assert.IsNull(VisionTargetsManifest.GetCollider(int.MaxValue));
        }

        #endregion ID REGISTRY


        #region IDENTITY

        [Test]
        public void ResolveTarget_StandaloneCollider_IsItsOwnIdentity()
        {
            BoxCollider col = LooseCollider("standalone");
            _scene.Register(col);

            Assert.AreSame(col, VisionTargetsManifest.ResolveTarget(col));
            Assert.IsFalse(VisionTargetsManifest.TryGetActor(col, out Component actor));
            Assert.IsNull(actor);
        }

        [Test]
        public void ResolveTarget_UnregisteredCollider_StillResolvesToItself()
        {
            // Identity resolution is total for a live collider - consumers never branch on membership
            BoxCollider col = LooseCollider("loose");

            Assert.AreSame(col, VisionTargetsManifest.ResolveTarget(col));
        }

        [Test]
        public void ResolveTarget_Null_IsNull()
        {
            Assert.IsNull(VisionTargetsManifest.ResolveTarget(null));
        }

        [Test]
        public void ResolveTarget_GroupedColliders_ShareTheOwningActor()
        {
            GameObject actorGo = _scene.Empty("actor");
            TestVisibleObject actor = actorGo.AddComponent<TestVisibleObject>();

            BoxCollider head = LooseCollider("head", new Vector3(0f, 2f, 0f));
            BoxCollider torso = LooseCollider("torso");
            _scene.Register(head, actor);
            _scene.Register(torso, actor);

            Assert.AreSame(actor, VisionTargetsManifest.ResolveTarget(head));
            Assert.AreSame(actor, VisionTargetsManifest.ResolveTarget(torso));
            Assert.IsTrue(VisionTargetsManifest.TryGetActor(head, out Component resolved));
            Assert.AreSame(actor, resolved);
        }

        [Test]
        public void Register_WithoutActor_ClearsAPreviousActorMapping()
        {
            GameObject actorGo = _scene.Empty("actor");
            TestVisibleObject actor = actorGo.AddComponent<TestVisibleObject>();
            BoxCollider col = LooseCollider("part");

            _scene.Register(col, actor);
            Assert.AreSame(actor, VisionTargetsManifest.ResolveTarget(col));

            _scene.Register(col, null); // re-registering standalone must detach the actor

            Assert.AreSame(col, VisionTargetsManifest.ResolveTarget(col));
            Assert.IsFalse(VisionTargetsManifest.TryGetActor(col, out _));
        }

        [Test]
        public void ResolveTarget_DestroyedActor_FallsBackToTheCollider()
        {
            GameObject actorGo = _scene.Empty("actor");
            TestVisibleObject actor = actorGo.AddComponent<TestVisibleObject>();
            BoxCollider col = LooseCollider("part");
            _scene.Register(col, actor);

            VisionTestScene.DestroyObject(actorGo);

            Assert.AreSame(col, VisionTargetsManifest.ResolveTarget(col), "A dead actor must not yield a null identity");
            Assert.IsFalse(VisionTargetsManifest.TryGetActor(col, out _));
        }

        #endregion IDENTITY
    }
}
