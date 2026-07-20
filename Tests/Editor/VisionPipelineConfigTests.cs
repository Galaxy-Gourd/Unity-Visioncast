using NUnit.Framework;

namespace GalaxyGourd.Visioncast.Tests
{
    /// <summary>
    /// The static selection seams a game configures before <see cref="VisioncastManager.Setup"/>: which
    /// per-tick computation runs, which broadphase implementation is built, and the ray budget the DoD
    /// sample generator derives from a source's sampling config.
    /// </summary>
    [TestFixture]
    public class VisionPipelineConfigTests
    {
        #region FIXTURE

        [SetUp]
        public void SetUp()
        {
            VisionTestStatics.ResetAll();
        }

        [TearDown]
        public void TearDown()
        {
            VisionTestStatics.ResetAll();
        }

        #endregion FIXTURE


        #region PIPELINE SELECTION

        [Test]
        public void Pipeline_DefaultsToTheManagedPath()
        {
            Assert.IsFalse(VisionPipeline.UseDodNarrowphase);
            Assert.AreEqual(20f, VisionPipeline.DodCellSize, 1e-4f);
        }

        [Test]
        public void UseDod_EnablesTheNativePathAndSetsTheCellSize()
        {
            VisionPipeline.UseDod(7.5f);

            Assert.IsTrue(VisionPipeline.UseDodNarrowphase);
            Assert.AreEqual(7.5f, VisionPipeline.DodCellSize, 1e-4f);
        }

        [Test]
        public void UseDod_ClampsADegenerateCellSize()
        {
            VisionPipeline.UseDod(0f);
            Assert.AreEqual(0.01f, VisionPipeline.DodCellSize, 1e-5f, "A zero cell size would divide the grid by zero");

            VisionPipeline.UseDod(-100f);
            Assert.AreEqual(0.01f, VisionPipeline.DodCellSize, 1e-5f);
        }

        [Test]
        public void UseManaged_RevertsToTheManagedPath()
        {
            VisionPipeline.UseDod();
            VisionPipeline.UseManaged();

            Assert.IsFalse(VisionPipeline.UseDodNarrowphase);
        }

        #endregion PIPELINE SELECTION


        #region BROADPHASE SELECTION

        [Test]
        public void Broadphase_DefaultsToThePhysicsOverlapImplementation()
        {
            using IBroadphase broadphase = VisionBroadphase.Factory();
            Assert.IsInstanceOf<PhysicsOverlapBroadphase>(broadphase);
        }

        [Test]
        public void UseBatchedOverlap_BuildsTheCommandBroadphase()
        {
            VisionBroadphase.UseBatchedOverlap();

            using IBroadphase broadphase = VisionBroadphase.Factory();
            Assert.IsInstanceOf<OverlapCommandBroadphase>(broadphase);
        }

        [Test]
        public void UseGrid_BuildsTheSpatialGridBroadphase()
        {
            VisionBroadphase.UseGrid(12f);

            using IBroadphase broadphase = VisionBroadphase.Factory();
            Assert.IsInstanceOf<VisionGridBroadphase>(broadphase);
        }

        [Test]
        public void UsePhysicsOverlap_RevertsTheSelection()
        {
            VisionBroadphase.UseGrid();
            VisionBroadphase.UsePhysicsOverlap();

            using IBroadphase broadphase = VisionBroadphase.Factory();
            Assert.IsInstanceOf<PhysicsOverlapBroadphase>(broadphase);
        }

        [Test]
        public void Factory_ProducesAnInstancePerCall()
        {
            using IBroadphase first = VisionBroadphase.Factory();
            using IBroadphase second = VisionBroadphase.Factory();

            Assert.AreNotSame(first, second, "Each Visioncaster owns and disposes its own broadphase");
        }

        #endregion BROADPHASE SELECTION


        #region RAY BUDGET

        [Test]
        public void RayCount_FaceGrid_IsClosestPointPlusSixFaces()
        {
            int mode = (int)VisionSampleMode.BoundsFaceGrid;

            Assert.AreEqual(1 + 6, VisionDodPipeline.RayCount(mode, 1));
            Assert.AreEqual(1 + 24, VisionDodPipeline.RayCount(mode, 2));
            Assert.AreEqual(1 + 54, VisionDodPipeline.RayCount(mode, 3));
        }

        [Test]
        public void RayCount_VolumeGrid_IsClosestPointPlusResolutionCubed()
        {
            int mode = (int)VisionSampleMode.BoundsVolumeGrid;

            Assert.AreEqual(1 + 1, VisionDodPipeline.RayCount(mode, 1));
            Assert.AreEqual(1 + 8, VisionDodPipeline.RayCount(mode, 2));
            Assert.AreEqual(1 + 27, VisionDodPipeline.RayCount(mode, 3));
        }

        [Test]
        public void RayCount_ClampsNonPositiveResolution()
        {
            Assert.AreEqual(7, VisionDodPipeline.RayCount((int)VisionSampleMode.BoundsFaceGrid, 0));
            Assert.AreEqual(7, VisionDodPipeline.RayCount((int)VisionSampleMode.BoundsFaceGrid, -5));
            Assert.AreEqual(2, VisionDodPipeline.RayCount((int)VisionSampleMode.BoundsVolumeGrid, 0));
        }

        [Test]
        public void RayCount_MatchesTheManagedSampleCount()
        {
            // The DoD budget must agree with what the managed processor generates, plus the extra
            // closest-point ray both paths cast first - otherwise visibility fractions diverge.
            using VisionTestScene scene = new VisionTestScene();
            UnityEngine.BoxCollider box = scene.Target("target", UnityEngine.Vector3.zero, UnityEngine.Vector3.one);

            foreach (VisionSampleMode mode in new[] { VisionSampleMode.BoundsFaceGrid, VisionSampleMode.BoundsVolumeGrid })
            {
                for (int resolution = 1; resolution <= 3; resolution++)
                {
                    System.Collections.Generic.List<UnityEngine.Vector3> points = new();
                    VisiblePointsProcessor.Process(box, mode, resolution, points);

                    Assert.AreEqual(
                        points.Count + 1,
                        VisionDodPipeline.RayCount((int)mode, resolution),
                        $"{mode} at resolution {resolution}");
                }
            }
        }

        #endregion RAY BUDGET
    }
}
