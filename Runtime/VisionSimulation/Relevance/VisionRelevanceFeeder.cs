using UnityEngine;

namespace GalaxyGourd.Visioncast
{
    /// <summary>
    /// STUB / interim utility. Feeds distance-based relevance into the vision system by measuring a
    /// source's distance to a focus transform (defaults to the main camera). Drop it into a scene to
    /// stand up relevance before the game's real prioritization system exists.
    ///
    /// It is NOT required for vision to run - without it, <see cref="VisionRelevance.Provider"/> stays
    /// null and every source is treated as maximally relevant. It is expected to be subsumed by the
    /// game's prioritization system (player distance + explicit always-important entities), at which
    /// point this component is simply removed.
    ///
    /// Pull-based: it installs a distance function as the relevance provider; nothing is computed until
    /// a consumer (the LOD scheduler) asks, so it costs nothing per frame on its own. Only one relevance
    /// provider is active at a time - last writer wins.
    /// </summary>
    public class VisionRelevanceFeeder : MonoBehaviour
    {
        #region VARIABLES

        [Tooltip("Point of interest to measure distance from. Falls back to Camera.main when unset.")]
        [SerializeField] private Transform _focus;

        #endregion VARIABLES


        #region INITIALIZATION

        private void OnEnable()
        {
            VisionRelevance.Provider = GetDistance;
        }

        private void OnDisable()
        {
            // Only clear if we are still the active provider, so we never clobber a later real system
            if (VisionRelevance.Provider == GetDistance)
                VisionRelevance.Provider = null;
        }

        #endregion INITIALIZATION


        #region RELEVANCE

        private float GetDistance(VisioncastSource source)
        {
            Transform focus = _focus ? _focus : (Camera.main ? Camera.main.transform : null);
            if (!focus || source == null || source.transform == null)
                return 0f; // no focus / no source -> treat as maximally relevant

            return Vector3.Distance(focus.position, source.Position);
        }

        #endregion RELEVANCE
    }
}
