using GalaxyGourd.Visioncast;
using UnityEngine;

namespace VisioncastSamples.Core
{
    /// <summary>
    /// Drives the Core sample's vision through the data-oriented (DoD) Burst pipeline.
    ///
    /// Uses the SYNCHRONOUS DoD path: the whole vision tick, including the raycast, completes in this one
    /// call. The deferred (off-main-thread) split is intentionally NOT used here - this sample moves its
    /// objects in Update (rotators / track movers), and the deferred path forbids moving a vision-queried
    /// collider while its raycast batch is in flight across the frame. See the "DoD Deferred Driver" sample
    /// for the deferred path (which takes manual ownership of the physics step to keep that window safe).
    /// </summary>
    [DefaultExecutionOrder(-500)]
    public class VCSampleCore_Updater : MonoBehaviour
    {
        [Tooltip("Spatial-grid cell size for the DoD broadphase - set roughly to a source's typical Range.")]
        [SerializeField] private float _gridCellSize = 20f;

        #region INITIALIZATION

        private void Awake()
        {
            // Route the whole tick through the native Burst pipeline. Must be set BEFORE Setup, and the
            // pipeline statics reset each play session, so it lives in the same Awake that calls Setup.
            VisionPipeline.UseDod(_gridCellSize);
            VisioncastManager.Setup();
        }

        private void OnDestroy()
        {
            VisioncastManager.Dispose();
        }

        #endregion INITIALIZATION


        #region TICK

        private void FixedUpdate()
        {
            // DoD owns its own raycast batch - RaycastManager is not used in this mode.
            VisioncastManager.TickVisioncasts(Time.fixedDeltaTime);
        }

        private void LateUpdate()
        {
            VisioncastManager.TickVisioncastSourceDebug(Time.deltaTime);
        }

        #endregion
    }
}
