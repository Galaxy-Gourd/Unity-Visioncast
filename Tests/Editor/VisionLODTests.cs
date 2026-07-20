using NUnit.Framework;
using UnityEngine;

namespace GalaxyGourd.Visioncast.Tests
{
    /// <summary>
    /// The LOD policy that decides how often a source casts. Tier selection, the hysteresis that stops
    /// a source thrashing on a tier boundary, and the dormant / no-cap fallbacks.
    /// </summary>
    [TestFixture]
    public class VisionLODTests
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

        /// <summary>near (<=10, every tick), mid (<=30, every 2nd, capped resolution 1), far (<=60, every 4th).</summary>
        private static void InstallThreeTiers()
        {
            VisionLOD.Tiers = new[]
            {
                new VisionLODTier { MaxDistance = 10f, Cadence = 1 },
                new VisionLODTier { MaxDistance = 30f, Cadence = 2, SampleResolution = 1 },
                new VisionLODTier { MaxDistance = 60f, Cadence = 4, SampleResolution = 1 }
            };
            VisionLOD.TierHysteresis = 1f;
        }

        #endregion FIXTURE


        #region DEFAULTS

        [Test]
        public void Default_IsASingleEveryTickTier()
        {
            Assert.AreEqual(1, VisionLOD.Tiers.Length);
            Assert.AreEqual(float.PositiveInfinity, VisionLOD.Tiers[0].MaxDistance);
            Assert.AreEqual(1, VisionLOD.Tiers[0].Cadence);

            Assert.AreEqual(0, VisionLOD.ResolveTier(0f, -1));
            Assert.AreEqual(0, VisionLOD.ResolveTier(10_000f, -1), "The default tier covers every distance");
            Assert.AreEqual(1, VisionLOD.CadenceForTier(0));
            Assert.AreEqual(0, VisionLOD.SampleResolutionForTier(0), "0 means 'no cap - use the source resolution'");
        }

        #endregion DEFAULTS


        #region TIER RESOLUTION

        [Test]
        public void ResolveTier_PicksTheFirstTierCoveringTheDistance()
        {
            InstallThreeTiers();

            Assert.AreEqual(0, VisionLOD.ResolveTier(0f, -1));
            Assert.AreEqual(0, VisionLOD.ResolveTier(9.9f, -1));
            Assert.AreEqual(1, VisionLOD.ResolveTier(10.1f, -1));
            Assert.AreEqual(1, VisionLOD.ResolveTier(30f, -1));
            Assert.AreEqual(2, VisionLOD.ResolveTier(45f, -1));
        }

        [Test]
        public void ResolveTier_BoundaryIsInclusive()
        {
            InstallThreeTiers();

            Assert.AreEqual(0, VisionLOD.ResolveTier(10f, -1), "MaxDistance is the inclusive upper bound");
        }

        [Test]
        public void ResolveTier_BeyondTheLastTier_IsDormant()
        {
            InstallThreeTiers();

            int tier = VisionLOD.ResolveTier(500f, -1);
            Assert.AreEqual(VisionLOD.Tiers.Length, tier, "Dormant is signalled by an index past the last tier");
            Assert.AreEqual(0, VisionLOD.CadenceForTier(tier), "A dormant tier never casts");
        }

        [Test]
        public void ResolveTier_StaleOrUnsetCurrentTier_AdoptsImmediately()
        {
            InstallThreeTiers();

            Assert.AreEqual(1, VisionLOD.ResolveTier(20f, -1), "No prior tier");
            Assert.AreEqual(1, VisionLOD.ResolveTier(20f, 99), "Current tier index no longer valid");
        }

        #endregion TIER RESOLUTION


        #region HYSTERESIS

        [Test]
        public void ResolveTier_MovingCloser_AdoptsTheNearerTierImmediately()
        {
            InstallThreeTiers();

            Assert.AreEqual(0, VisionLOD.ResolveTier(5f, 2), "Nearer tiers are adopted with no margin");
            Assert.AreEqual(1, VisionLOD.ResolveTier(25f, 2));
        }

        [Test]
        public void ResolveTier_MovingAway_HoldsTheCurrentTierWithinTheMargin()
        {
            InstallThreeTiers();

            // Boundary 10 + hysteresis 1 -> must exceed 11 before dropping to the coarser tier
            Assert.AreEqual(0, VisionLOD.ResolveTier(10.5f, 0), "Inside the margin - hold");
            Assert.AreEqual(0, VisionLOD.ResolveTier(11f, 0), "Exactly at the margin - still hold");
            Assert.AreEqual(1, VisionLOD.ResolveTier(11.1f, 0), "Past the margin - drop a tier");
        }

        [Test]
        public void ResolveTier_LargeHysteresis_SuppressesTheDrop()
        {
            InstallThreeTiers();
            VisionLOD.TierHysteresis = 25f;

            Assert.AreEqual(0, VisionLOD.ResolveTier(30f, 0), "30 <= boundary(10) + 25");
            Assert.AreEqual(2, VisionLOD.ResolveTier(40f, 0), "Past the margin, resolved against the real distance");
        }

        [Test]
        public void ResolveTier_MovingAwayPastTheLastTier_GoesDormantOnlyBeyondTheMargin()
        {
            InstallThreeTiers();

            Assert.AreEqual(2, VisionLOD.ResolveTier(60.5f, 2), "Inside the margin of the last tier");
            Assert.AreEqual(3, VisionLOD.ResolveTier(61.5f, 2), "Beyond it - dormant");
        }

        #endregion HYSTERESIS


        #region TIER LOOKUPS

        [Test]
        public void CadenceForTier_OutOfRangeIndex_IsDormant()
        {
            InstallThreeTiers();

            Assert.AreEqual(1, VisionLOD.CadenceForTier(0));
            Assert.AreEqual(2, VisionLOD.CadenceForTier(1));
            Assert.AreEqual(4, VisionLOD.CadenceForTier(2));
            Assert.AreEqual(0, VisionLOD.CadenceForTier(3));
            Assert.AreEqual(0, VisionLOD.CadenceForTier(-1), "An unscheduled source must not be treated as tier 0");
        }

        [Test]
        public void SampleResolutionForTier_ReportsTheCapOrZeroForNoCap()
        {
            InstallThreeTiers();

            Assert.AreEqual(0, VisionLOD.SampleResolutionForTier(0), "Nearest tier sets no cap");
            Assert.AreEqual(1, VisionLOD.SampleResolutionForTier(1));
            Assert.AreEqual(0, VisionLOD.SampleResolutionForTier(3), "Out of range");
            Assert.AreEqual(0, VisionLOD.SampleResolutionForTier(-1));
        }

        #endregion TIER LOOKUPS


        #region RELEVANCE

        [Test]
        public void Relevance_WithNoProvider_TreatsEverySourceAsMaximallyRelevant()
        {
            using VisionTestScene scene = new VisionTestScene();
            TestVisionSource source = scene.Source("source", new Vector3(0f, 0f, 100f));

            Assert.IsNull(VisionRelevance.Provider);
            Assert.AreEqual(0f, VisionRelevance.GetDistance(source), 1e-4f);
        }

        [Test]
        public void Relevance_UsesTheInstalledProvider()
        {
            using VisionTestScene scene = new VisionTestScene();
            TestVisionSource source = scene.Source("source", new Vector3(0f, 0f, 42f));

            VisioncastSource seen = null;
            VisionRelevance.Provider = s =>
            {
                seen = s;
                return s.Position.z;
            };

            Assert.AreEqual(42f, VisionRelevance.GetDistance(source), 1e-4f);
            Assert.AreSame(source, seen, "The provider is handed the source it is scoring");
        }

        #endregion RELEVANCE
    }
}
