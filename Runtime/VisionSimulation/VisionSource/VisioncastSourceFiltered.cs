using System.Collections.Generic;
using UnityEngine;

namespace GalaxyGourd.Visioncast
{
    /// <summary>
    /// Source that filters visioncast results into more detailed data. 
    /// </summary>
    public class VisioncastSourceFiltered : VisioncastSource
    {
        #region VARIABLES

        [Header("Data")]
        [SerializeField] protected DataConfigVisioncastSource _dataInteraction;

        public override LayerMask BroadphaseLayers => _dataInteraction.BroadphaseLayermask;
        public override LayerMask RaycastLayers => _dataInteraction.RaycastLayermask;
        public override float Range => _dataInteraction.VisionRange;
        public override float FieldOfView => _dataInteraction.FieldOfView;
        public override VisionSampleMode SampleMode => _dataInteraction.SampleMode;
        public override int SampleResolution => _dataInteraction.SampleResolution;

        /// <summary>
        /// All objects currently resolved by this source (visible and in-range-but-occluded)
        /// </summary>
        public IReadOnlyList<DataVisionSeenObject> VisionTargets => _filteredVisionTargets;
        /// <summary>
        /// Objects that became visible since the most recent visioncast update
        /// </summary>
        public IReadOnlyList<Collider> NewlySeenObjects => _newlySeenObjects;
        /// <summary>
        /// Objects that stopped being visible since the most recent visioncast update
        /// </summary>
        public IReadOnlyList<Collider> NewlyLostObjects => _newlyLostObjects;
        /// <summary>
        /// The object most directly in the center of this source's field of view, if any
        /// </summary>
        public Collider TargetedObject => _targetedObject;

        /// <summary>
        /// All objects currently seen by this source
        /// </summary>
        protected List<DataVisionSeenObject> _filteredVisionTargets = new ();
        /// <summary>
        /// The object most directly in the center of the source's field of view
        /// </summary>
        protected Collider _targetedObject;
        /// <summary>
        /// A list of objects that were newly seen since the most recent visioncast update 
        /// </summary>
        protected readonly List<Collider> _newlySeenObjects = new();
        /// <summary>
        /// A list of objects that were newly un-seen since the most recent visioncast update
        /// </summary>
        protected readonly List<Collider> _newlyLostObjects = new();

        private readonly List<DataVisionSeenObject> _objCache = new();

        // Reused buffers so filtering allocates nothing per update: a second results list to ping-pong with
        // _filteredVisionTargets, and identity->index maps / a set for O(1) diffs. Keyed by TARGET IDENTITY
        // (DataVisionSeenObject.Actor), not collider - a multi-collider actor's representative collider can
        // change between updates, which would otherwise read as a spurious lost+seen pair.
        private List<DataVisionSeenObject> _resolveBuffer = new();
        private readonly Dictionary<Component, int> _previousIndex = new();
        private readonly Dictionary<Component, int> _currentIndex = new();
        private readonly Dictionary<Component, int> _actorIndex = new();
        private readonly HashSet<Component> _newlyLostSet = new();

        #endregion VARIABLES
        

        #region VISION

        protected override void OnReceiveResults(DataVisioncastResult data)
        {
            FilterVisionTargets(data);
            PostVisionFilter();
        }

        protected virtual void FilterVisionTargets(DataVisioncastResult data)
        {
            // If there are no objects visible we can get out
            if (data.Objects == null)
            {
                // Copy previously seen objects
                _objCache.Clear();
                _objCache.AddRange(_filteredVisionTargets);
                
                // Clear out vision data since there's nothing visible
                ClearVisionData();

                // If there WERE visible items last update, they now count as newly lost
                foreach (DataVisionSeenObject lastSeen in _objCache)
                {
                    _newlyLostObjects.Add(lastSeen.ResultObject);
                }
                
                return;
            }
            
            // Resolve into the reused buffer, collapsed per target identity (previous = current targets)
            VisioncastResultsFilter.Resolve(data, _filteredVisionTargets, _previousIndex, _actorIndex, _resolveBuffer);
            List<DataVisionSeenObject> newResults = _resolveBuffer;
            _newlySeenObjects.Clear();
            _newlyLostObjects.Clear();
            _newlyLostSet.Clear();

            // Index the new results by target identity for O(1) presence checks
            _currentIndex.Clear();
            for (int i = 0; i < newResults.Count; i++)
            {
                Component actor = newResults[i].Actor;
                if (actor)
                    _currentIndex[actor] = i;
            }

            // Targets that were visible before but are no longer present at all are newly lost
            for (int i = 0; i < _filteredVisionTargets.Count; i++)
            {
                DataVisionSeenObject prev = _filteredVisionTargets[i];
                if (!prev.IsVisible || !prev.Actor)
                    continue;

                if (!_currentIndex.ContainsKey(prev.Actor) && _newlyLostSet.Add(prev.Actor))
                    _newlyLostObjects.Add(prev.ResultObject);
            }

            // From the new results: collect newly seen, and present-but-not-visible as newly lost
            for (int i = 0; i < newResults.Count; i++)
            {
                DataVisionSeenObject visionObject = newResults[i];
                if (visionObject.JustBecameVisible)
                {
                    _newlySeenObjects.Add(visionObject.ResultObject);
                }
                else if (!visionObject.IsVisible &&
                         visionObject.Actor &&
                         _newlyLostSet.Add(visionObject.Actor))
                {
                    _newlyLostObjects.Add(visionObject.ResultObject);
                }
            }

            // Swap: new results become current; the old current list is reused as next resolve buffer
            _resolveBuffer = _filteredVisionTargets;
            _filteredVisionTargets = newResults;

            // The object closest to the view center is our key object
            float closestAngle = float.MaxValue;
            _targetedObject = null;
            for (int i = 0; i < _filteredVisionTargets.Count; i++)
            {
                DataVisionSeenObject obj = _filteredVisionTargets[i];
                if (obj.IsVisible && obj.Angle < closestAngle)
                {
                    closestAngle = obj.Angle;
                    _targetedObject = obj.ResultObject;
                }
            }
        }
        
        protected virtual void PostVisionFilter() { }

        protected virtual void ClearVisionData()
        {
            _filteredVisionTargets.Clear();
            _newlySeenObjects.Clear();
            _newlyLostObjects.Clear();
            _targetedObject = null;
        }

        #endregion VISION
    }
}