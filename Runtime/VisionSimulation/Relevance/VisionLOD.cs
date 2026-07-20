using UnityEngine;

namespace GalaxyGourd.Visioncast
{
    /// <summary>
    /// System-wide vision level-of-detail policy: maps a source's relevance distance
    /// (<see cref="VisionRelevance"/>) to an update cadence. The scheduler casts a source once every
    /// 'cadence' ticks — nearer sources every tick, farther sources rarely, out-of-range sources not
    /// at all.
    ///
    /// Optional: the default is a single infinite tier at cadence 1 (every source every tick), which
    /// reproduces non-LOD behavior. Combined with a null relevance provider (distance 0), the default
    /// yields "everything every tick". A game assigns <see cref="Tiers"/> with real thresholds.
    /// </summary>
    public static class VisionLOD
    {
        #region VARIABLES

        /// <summary>
        /// Tiers ordered ASCENDING by <see cref="VisionLODTier.MaxDistance"/>. A source uses the first
        /// tier whose MaxDistance covers its relevance distance; beyond the last tier it is dormant.
        /// </summary>
        public static VisionLODTier[] Tiers = _defaultTiers;

        /// <summary>Distance margin (world units) required to drop to a FARTHER tier, preventing thrash.</summary>
        public static float TierHysteresis = 1f;

        private static readonly VisionLODTier[] _defaultTiers =
        {
            new VisionLODTier { MaxDistance = float.PositiveInfinity, Cadence = 1 }
        };

        #endregion VARIABLES


        #region API

        /// <summary>
        /// Resolves the tier index for a relevance distance, applying hysteresis against the source's
        /// current tier: nearer tiers are adopted immediately (responsive), farther tiers only once the
        /// distance clears the current boundary by <see cref="TierHysteresis"/>. Returns Tiers.Length
        /// (dormant) when beyond the last tier.
        /// </summary>
        internal static int ResolveTier(float distance, int currentTier)
        {
            int target = Tiers.Length; // dormant unless a tier covers the distance
            for (int i = 0; i < Tiers.Length; i++)
            {
                if (distance <= Tiers[i].MaxDistance)
                {
                    target = i;
                    break;
                }
            }

            // No prior tier, current tier stale, or nearer than before -> adopt immediately
            if (currentTier < 0 || currentTier >= Tiers.Length || target <= currentTier)
                return target;

            // Farther than before -> only switch once past the current boundary plus the margin
            float boundary = Tiers[currentTier].MaxDistance;
            return distance > boundary + TierHysteresis ? target : currentTier;
        }

        /// <summary>Cast cadence for a tier index; 0 (dormant) for an out-of-range / beyond-last index.</summary>
        internal static int CadenceForTier(int tier)
        {
            return tier >= 0 && tier < Tiers.Length ? Tiers[tier].Cadence : 0;
        }

        /// <summary>Sample-resolution cap for a tier index; 0 = no cap (use the source's own).</summary>
        internal static int SampleResolutionForTier(int tier)
        {
            return tier >= 0 && tier < Tiers.Length ? Tiers[tier].SampleResolution : 0;
        }

        #endregion API


        #region RESET

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            // Statics survive across play sessions when domain reload is disabled
            Tiers = _defaultTiers;
            TierHysteresis = 1f;
        }

        #endregion RESET
    }
}
