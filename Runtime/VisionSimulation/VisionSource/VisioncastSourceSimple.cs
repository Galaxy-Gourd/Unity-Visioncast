using System.Collections.Generic;
using UnityEngine;

namespace GalaxyGourd.Visioncast
{
    /// <summary>
    /// Basic implementation of visioncast source without complex filtering.
    /// </summary>
    public class VisioncastSourceSimple : VisioncastSource
    {
        #region VARIABLES

        [Header("Vision Values")]
        [SerializeField] private LayerMask _visible;
        [SerializeField] private LayerMask _obstruction;
        [SerializeField] private float _range;
        [SerializeField] private float _fieldOfView;

        [Header("Sampling")]
        [SerializeField] private VisionSampleMode _sampleMode = VisionSampleMode.BoundsFaceGrid;
        [SerializeField, Min(1)] private int _sampleResolution = 1;

        public override LayerMask BroadphaseLayers => _visible;
        public override LayerMask RaycastLayers => _obstruction;
        public override float Range => _range;
        public override float FieldOfView => _fieldOfView;
        public override VisionSampleMode SampleMode => _sampleMode;
        public override int SampleResolution => _sampleResolution;

        // Tracks which targets have been notified this update so a multi-collider actor's
        // IVisibleObject is told it was Seen once, not once per visible collider
        private readonly HashSet<IVisibleObject> _notifiedThisUpdate = new();

        #endregion VARIABLES


        #region METHODS

        public void OverrideFieldOfView(float val)
        {
            _fieldOfView = val;
        }

        public void OverrideRange(float val)
        {
            _range = val;
        }

        protected override void OnReceiveResults(DataVisioncastResult data)
        {
            if (data.Objects == null)
                return;

            _notifiedThisUpdate.Clear();
            for (int i = 0; i < data.Objects.Count; i++)
            {
                Collider col = data.Objects[i];
                if (!col || data.VisiblePointCounts[i] == 0)
                    continue;

                if (!TryResolveVisibleObject(col, out IVisibleObject vo))
                    continue;

                // Colliders that resolve to the same actor share one IVisibleObject; notify it once
                if (_notifiedThisUpdate.Add(vo))
                    vo.Seen(this);
            }
        }

        /// <summary>
        /// Resolves the IVisibleObject to notify for a hit collider, via the collider's single target
        /// identity (<see cref="VisionTargetsManifest.ResolveTarget"/>) - the owning actor, or the collider
        /// itself when standalone. Colliders of the same actor resolve to the same object, so
        /// <see cref="_notifiedThisUpdate"/> notifies it once.
        /// </summary>
        private static bool TryResolveVisibleObject(Collider col, out IVisibleObject visibleObject)
        {
            Component target = VisionTargetsManifest.ResolveTarget(col);
            if (target)
                return target.TryGetComponent(out visibleObject);

            visibleObject = null;
            return false;
        }

        #endregion METHODS
    }
}