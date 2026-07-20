using System.Collections.Generic;
using UnityEngine;
using GalaxyGourd.Visioncast;

namespace VisioncastSamples.GuardPatrol
{
    /// <summary>
    /// A guard's vision plus a Patrol -> Alert -> Search alert state machine, built on
    /// <see cref="VisioncastSourceFiltered"/>. It reacts to the FILTERED enter/exit events -
    /// <see cref="VisioncastSourceFiltered.NewlySeenObjects"/> and
    /// <see cref="VisioncastSourceFiltered.NewlyLostObjects"/> - rather than raw per-tick visibility:
    ///   - sees the intruder (NewlySeen)            -> Alert (lock on, track it)
    ///   - loses it behind cover / range (NewlyLost) -> Search (go to last-known, count down)
    ///   - re-sees it while searching                -> Alert
    ///   - search times out                          -> Patrol
    /// The intruder is a MULTI-COLLIDER actor (head + torso registered under one <see cref="GuardIntruder"/>),
    /// so the filter collapses it to a single target here - the guard tracks "the intruder", not two body parts.
    /// </summary>
    public class GuardVision : VisioncastSourceFiltered
    {
        public enum GuardState { Patrol, Alert, Search }

        private LayerMask _broad;
        private LayerMask _raycast;
        private float _range;
        private float _fov;
        private float _searchDuration = 3f;
        private Component _intruder; // the intruder's actor identity (see GuardIntruder)

        public override LayerMask BroadphaseLayers => _broad;
        public override LayerMask RaycastLayers => _raycast;
        public override float Range => _range;
        public override float FieldOfView => _fov;
        // VisioncastSourceFiltered reads SampleMode/SampleResolution from a serialized _dataInteraction config.
        // This guard is built in code with no such asset, so override them here (leaving them would dereference
        // a null config). Resolution 2 gives a few sample points per body part, so the wall occludes gradually.
        public override VisionSampleMode SampleMode => VisionSampleMode.BoundsFaceGrid;
        public override int SampleResolution => 2;

        public GuardState State { get; private set; } = GuardState.Patrol;
        public bool TargetVisible { get; private set; }
        public Vector3 LastKnownPosition { get; private set; }
        public float SearchTimer { get; private set; }

        public void Configure(LayerMask broad, LayerMask raycast, float range, float fov,
                              Component intruder, float searchDuration)
        {
            _broad = broad;
            _raycast = raycast;
            _range = range;
            _fov = fov;
            _intruder = intruder;
            _searchDuration = searchDuration;
        }

        protected override void PostVisionFilter()
        {
            // Current visibility of the intruder. VisionTargets is one entry per identity, so the intruder's
            // head + torso are already collapsed into a single actor entry.
            TargetVisible = false;
            for (int i = 0; i < VisionTargets.Count; i++)
            {
                DataVisionSeenObject t = VisionTargets[i];
                if (t.IsVisible && IsIntruder(t.Actor))
                {
                    TargetVisible = true;
                    if (t.ResultObject)
                        LastKnownPosition = t.ResultObject.transform.position;
                }
            }

            bool newlySeen = ListHasIntruder(NewlySeenObjects);
            bool newlyLost = ListHasIntruder(NewlyLostObjects);

            switch (State)
            {
                case GuardState.Patrol:
                    if (newlySeen)
                        State = GuardState.Alert;
                    break;

                case GuardState.Alert:
                    // Left view (stepped behind cover or out of range). Guard heads to the last-known spot.
                    if (newlyLost && !TargetVisible)
                    {
                        State = GuardState.Search;
                        SearchTimer = _searchDuration;
                    }
                    break;

                case GuardState.Search:
                    if (newlySeen)
                        State = GuardState.Alert;
                    break;
            }
        }

        /// <summary>Advance the search countdown; call once per fixed tick. Search -> Patrol on timeout.</summary>
        public void TickSearch(float dt)
        {
            if (State != GuardState.Search)
                return;

            SearchTimer -= dt;
            if (SearchTimer <= 0f)
                State = GuardState.Patrol;
        }

        private bool IsIntruder(Component actor) => _intruder == null || actor == _intruder;

        private bool ListHasIntruder(IReadOnlyList<Collider> list)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] && IsIntruder(VisionTargetsManifest.ResolveTarget(list[i])))
                    return true;
            return false;
        }
    }
}
