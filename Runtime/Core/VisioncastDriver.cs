using GalaxyGourd.Raycast;
using UnityEngine;

namespace GalaxyGourd.Visioncast
{
    /// <summary>
    /// Default, self-contained driver for the vision system. Drop one into a scene and the package runs
    /// standalone with NO external dependency - no GG.Tick, no custom PlayerLoop. It simply calls the
    /// public tick entry points from Unity's FixedUpdate.
    ///
    /// This is the SIMPLE path: raycasts are scheduled and completed within the same tick, so their cost
    /// lands on the main thread. To hide that at scale, drive the same entry points from the game's own
    /// ordered manager (e.g. GG.Tick groups, or a custom PlayerLoop) that brackets Physics.Simulate and
    /// opts into deferred completion. Use EITHER this driver OR a custom one - never both (double-ticking).
    /// </summary>
    [DefaultExecutionOrder(-500)]
    public class VisioncastDriver : MonoBehaviour
    {
        #region INITIALIZATION

        private void Awake()
        {
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
            float delta = Time.fixedDeltaTime;

            // Complete the previous tick's batch (distributes results), then schedule this tick's
            RaycastManager.TickRaycasts(delta);
            VisioncastManager.TickVisioncasts(delta);
        }

        private void LateUpdate()
        {
            VisioncastManager.TickVisioncastSourceDebug(Time.deltaTime);
        }

        #endregion TICK
    }
}
