using System;
using UnityEngine;

namespace GalaxyGourd.Visioncast
{
    /// <summary>
    /// Optional relevance seam for vision sources, read by the LOD scheduler to decide a source's update
    /// cadence / sample resolution. A future prioritization system (player distance, always-important
    /// entities, etc.) assigns <see cref="Provider"/>; until then it is null and every source is treated
    /// as fully relevant, so vision runs with no LOD weighting. Never a required dependency.
    ///
    /// Relevance is expressed as a DISTANCE in world units from the point of interest:
    /// smaller = more relevant (higher priority). An always-important entity returns 0. When no provider
    /// is set, every source resolves to 0 (maximally relevant).
    /// </summary>
    public static class VisionRelevance
    {
        #region VARIABLES

        /// <summary>
        /// Supplies a source's relevance distance (world units, smaller = more relevant). Null =
        /// unweighted. Set by the game's prioritization system, or the interim
        /// <see cref="VisionRelevanceFeeder"/>; read by the LOD scheduler.
        /// </summary>
        public static Func<VisioncastSource, float> Provider;

        #endregion VARIABLES


        #region API

        /// <summary>
        /// Relevance distance for a source. Returns 0 when no provider is set, so an unweighted system
        /// behaves as "everything maximally relevant".
        /// </summary>
        public static float GetDistance(VisioncastSource source)
        {
            return Provider?.Invoke(source) ?? 0f;
        }

        #endregion API


        #region RESET

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            // Statics survive across play sessions when domain reload is disabled
            Provider = null;
        }

        #endregion RESET
    }
}
