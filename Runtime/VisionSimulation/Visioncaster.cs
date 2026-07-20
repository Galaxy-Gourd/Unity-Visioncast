using System;
using System.Collections.Generic;
using GalaxyGourd.Raycast;
using Unity.Profiling;
using UnityEngine;

namespace GalaxyGourd.Visioncast
{
    /// <summary>
    /// Class responsible for executing visioncast requests
    /// </summary>
    internal class Visioncaster : IScheduledRaycastListener, IDisposable
    {
        #region VARIABLES

        internal List<VisioncastSource> Components => _components;
        public List<RaycastHit> RaycasterResults { get; set; }
        public List<DataScheduledRaycastRequest> RaycasterRequests { get; set; }

        // Aligned intermediate buffers - every list is indexed 1:1 with _components, so a source's
        // slot is ALWAYS present even when the source is skipped (see CalculateVisionBroadphase).
        // Losing that alignment shifts every downstream index in narrowphase/distribution.
        // These are single-buffered: written in the broadphase, consumed in the following frame's
        // distribution, then reset by PrepareBuffers (which runs after that distribution).
        private readonly List<List<Collider>> _visibleObjects = new();
        // item1 = index of _visibleObjects object to which points belong
        private readonly List<List<List<Vector3>>> _visibleObjectPoints = new();
        private readonly List<List<float>> _visibleObjectDistances = new();
        private readonly List<List<float>> _visibleObjectAngles = new();
        // We use this to map our raycast requests back to the correct source/target/point
        private readonly List<RaycastRequestMap> _raycastRequestMap = new();

        // Results are owned PER-SOURCE (see VisioncastSource._resultBuffers), double-buffered there so
        // a source's LastResults stays valid until that source's own next distribution - independent of
        // any other source's update cadence. The Visioncaster writes into each source's back buffer and
        // returns that buffer's pooled point-lists here on reset.

        // Reused Vector3 lists to keep the narrowphase off the per-frame allocation path
        private readonly Stack<List<Vector3>> _pointListPool = new();

        // Per-source reusable collider -> result-object-index maps, so distribution avoids a linear
        // List.IndexOf per sample point. Grown to component count and cleared each distribution.
        private readonly List<Dictionary<Collider, int>> _resultObjectIndex = new();

        // Broadphase (spatial query) behind a swappable seam; candidates[i] holds the in-range colliders
        // for scheduled source i, reused each tick. The Visioncaster owns and disposes the broadphase.
        private readonly IBroadphase _broadphase;
        private readonly List<List<Collider>> _broadphaseCandidates = new();
        private readonly SystemScheduledRaycaster _raycaster;
        // When set (VisionPipeline.UseDod), Tick delegates the whole per-tick computation to this native
        // Burst pipeline instead of the managed broadphase/narrowphase/distribute path below.
        private readonly VisionDodPipeline _dodPipeline;
        private bool _waitingForRaycasts;
        private bool _destroyed;
        private readonly List<VisioncastSource> _components = new();
        private readonly List<VisioncastSource> _queuedAdd = new();
        private readonly List<VisioncastSource> _queuedRemove = new();

        // The subset of sources casting THIS tick, chosen by relevance-driven cadence (BuildSchedule).
        // All per-tick buffers/indices are aligned 1:1 with this list, not _components. It holds source
        // references (not indices), so it stays valid across registration changes between schedule and
        // the following tick's distribution.
        private readonly List<VisioncastSource> _scheduled = new();
        private long _scheduleTick;

        // Accumulated vision-time (sum of tick deltas), used to stamp each source's LastUpdatedTime so
        // consumers can gauge staleness under LOD/time-slicing. Driver-relative (not Unity Time.time),
        // to keep the system decoupled from the update loop.
        private float _visionTime;
        internal float VisionTime => _visionTime;

        private const int CONST_PointListCapacity = 7;

        // Per-stage profiler markers. Baseline instrumentation for the DoD rewrite: each stage is the
        // A/B measurement point its Burst replacement is judged against. Zero cost when not profiling.
        private static readonly ProfilerMarker s_MarkerBuildSchedule = new("Visioncast.BuildSchedule");
        private static readonly ProfilerMarker s_MarkerBroadphase = new("Visioncast.Broadphase");
        private static readonly ProfilerMarker s_MarkerNarrowphaseBuild = new("Visioncast.NarrowphaseBuild");
        private static readonly ProfilerMarker s_MarkerDistribute = new("Visioncast.Distribute");
        private static readonly ProfilerMarker s_MarkerDod = new("Visioncast.DodPipeline");

        #endregion VARIABLES


        #region INITIALIZATION

        internal Visioncaster(SystemScheduledRaycaster raycaster, IBroadphase broadphase)
        {
            _raycaster = raycaster;
            _broadphase = broadphase;
            RaycasterRequests = new List<DataScheduledRaycastRequest>();

            if (VisionPipeline.UseDodNarrowphase)
                _dodPipeline = new VisionDodPipeline(VisionPipeline.DodCellSize);
        }

        #endregion INITIALIZATION


        #region REGISTRATION

        public void RegisterComponent(VisioncastSource component)
        {
            // Uniformly queued - registration never mutates _components directly, so the component
            // set stays stable between a schedule and its matching result distribution
            _queuedAdd.Add(component);
        }

        public void RemoveComponent(VisioncastSource component)
        {
            _queuedRemove.Add(component);
        }

        /// <summary>
        /// Syncs the addition and removal of components until after raycasting has been completed
        /// </summary>
        private void ResolveComponentQueues()
        {
            if (_destroyed)
                return;

            foreach (VisioncastSource source in _queuedAdd)
            {
                if (_components.Contains(source))
                    continue;

                _components.Add(source);
                // Stable per-source phase offset spreads same-cadence sources across ticks. Uses
                // GetHashCode (a stable arbitrary int; collisions only mean a shared phase, harmless)
                // rather than GetInstanceID, which Unity 6.5 removed (EntityId replaces InstanceID).
                source.SchedulePhase = source.GetHashCode() & int.MaxValue;
            }

            foreach (VisioncastSource source in _queuedRemove)
            {
                int index = _components.IndexOf(source);
                if (index < 0)
                    continue;

                _components.RemoveAt(index);
            }

            bool modified = _queuedAdd.Count > 0 || _queuedRemove.Count > 0;
            _queuedAdd.Clear();
            _queuedRemove.Clear();

            if (modified)
                VisioncastManager.VisionSourceComponentsModified(_components);
        }

        #endregion REGISTRATION


        #region TICK

        internal void Tick(float delta)
        {
            // Advance vision-time every cycle (even skipped ones) so staleness reflects real elapsed time
            _visionTime += delta;

            if (_destroyed || _waitingForRaycasts)
                return;

            using (s_MarkerBuildSchedule.Auto()) BuildSchedule();

            // DoD path: the native pipeline gathers, casts, and scatters synchronously in one call, then we
            // resolve registration queues (no deferred raycast round-trip, so no _waitingForRaycasts gate).
            if (_dodPipeline != null)
            {
                for (int i = 0; i < _scheduled.Count; i++)
                    _scheduled[i].OnBeforeVisioncast();

                using (s_MarkerDod.Auto()) _dodPipeline.Execute(_scheduled, _scheduled.Count, _visionTime);
                ResolveComponentQueues();
                return;
            }

            using (s_MarkerBroadphase.Auto()) CalculateVisionBroadphase();
            using (s_MarkerNarrowphaseBuild.Auto()) ExecuteVisionNarrowphase();

            // Guard against scheduling again before the previous batch has been received
            _waitingForRaycasts = true;
            _raycaster.Schedule(this, RaycasterRequests);
        }

        /// <summary>
        /// Deferred DoD phase 1 (call at end of the fixed tick, after movement): build this cycle's batch and
        /// schedule the raycast on workers without completing it. No-op if not in DoD mode. Must be paired with
        /// <see cref="TickDodComplete"/> at the start of the next fixed tick, before any Physics.Simulate.
        /// </summary>
        internal void TickDodSchedule(float delta)
        {
            _visionTime += delta;

            if (_destroyed || _dodPipeline == null || _dodPipeline.BatchPending)
                return; // no pipeline, or the previous batch was never completed (ordering error) - skip

            using (s_MarkerBuildSchedule.Auto()) BuildSchedule();

            for (int i = 0; i < _scheduled.Count; i++)
                _scheduled[i].OnBeforeVisioncast();

            using (s_MarkerDod.Auto()) _dodPipeline.ScheduleBatch(_scheduled, _scheduled.Count, _visionTime);
        }

        /// <summary>
        /// Deferred DoD phase 2 (call at start of the fixed tick, before Physics.Simulate): complete the batch
        /// scheduled last tick, reduce, and scatter results. No-op if not in DoD mode or nothing is pending.
        /// </summary>
        internal void TickDodComplete()
        {
            if (_destroyed || _dodPipeline == null)
                return;

            using (s_MarkerDod.Auto()) _dodPipeline.CompleteAndScatter();
            ResolveComponentQueues();
        }

        /// <summary>
        /// Chooses the subset of sources to cast this tick from their relevance-driven cadence. Sources
        /// with null transforms and dormant (out-of-range) sources are excluded; the rest cast on the
        /// ticks matching their tier cadence, phase-spread so same-cadence sources don't all fire together.
        /// </summary>
        private void BuildSchedule()
        {
            _scheduled.Clear();
            _scheduleTick++;

            for (int i = 0; i < _components.Count; i++)
            {
                VisioncastSource source = _components[i];
                // A source whose GameObject was destroyed this frame stays in _components until the queued
                // removal is resolved at the END of this tick (ResolveComponentQueues). Check the Unity-object
                // null FIRST - accessing source.transform on a destroyed object throws MissingReferenceException.
                if (source == null || source.transform == null)
                    continue;

                float distance = VisionRelevance.GetDistance(source);
                int tier = VisionLOD.ResolveTier(distance, source.ScheduleTier);
                source.ScheduleTier = tier;

                int cadence = VisionLOD.CadenceForTier(tier);
                if (cadence <= 0)
                    continue; // dormant

                if (cadence == 1 || (_scheduleTick + source.SchedulePhase) % cadence == 0)
                    _scheduled.Add(source);
            }
        }

        #endregion TICK


        #region LOGIC

        /// <summary>
        /// Gathers targets to be raycasted against, eliminating objects that are too far or not in front of the source
        /// </summary>
        private void CalculateVisionBroadphase()
        {
            // Reset the intermediate buffers for the new calculation; slots are reused, not reallocated.
            // Buffers are aligned 1:1 with the scheduled subset, not the full component list.
            PrepareBuffers(_scheduled.Count);
            PrepareBroadphaseCandidates(_scheduled.Count);

            // Let sources update their vision params before the (possibly batched) spatial query reads them
            for (int i = 0; i < _scheduled.Count; i++)
                _scheduled[i].OnBeforeVisioncast();

            // Spatial query stage (swappable): fill candidates[i] with the in-range colliders per source
            _broadphase.Query(_scheduled, _scheduled.Count, _broadphaseCandidates);

            // Narrow the candidates by manifest membership + field-of-view angle, and generate samples
            for (int i = 0; i < _scheduled.Count; i++)
            {
                List<Collider> candidates = _broadphaseCandidates[i];
                if (candidates.Count == 0)
                    continue;

                VisioncastSource source = _scheduled[i];
                Vector3 position = source.Position;
                Vector3 heading = source.Heading;
                float fov = source.FieldOfView;
                VisionSampleMode sampleMode = source.SampleMode;
                // Far tiers may cap sampling coarser than the source requests (never finer)
                int tierResolution = VisionLOD.SampleResolutionForTier(source.ScheduleTier);
                int sampleResolution = tierResolution > 0
                    ? Mathf.Min(source.SampleResolution, tierResolution)
                    : source.SampleResolution;

                for (int h = 0; h < candidates.Count; h++)
                {
                    Collider hit = candidates[h];
                    if (!hit)
                        continue;

                    if (!VisionTargetsManifest.Manifest.Contains(hit)) // manifest must contain collider
                        continue;

                    // We know the object is in range, but we also need to filter by angle
                    Vector3 closestPoint = GetClosestPoint(hit, position);
                    Vector3 dirToTarget = closestPoint - position;
                    float angle = Vector3.Angle(heading, dirToTarget);
                    if (angle > fov)
                        continue;

                    float objDistance = Vector3.Distance(closestPoint, position);

                    _visibleObjects[i].Add(hit);

                    List<Vector3> objectPoints = RentPointList();
                    objectPoints.Add(closestPoint);
                    VisiblePointsProcessor.Process(hit, sampleMode, sampleResolution, objectPoints);
                    _visibleObjectPoints[i].Add(objectPoints);

                    _visibleObjectDistances[i].Add(objDistance);
                    _visibleObjectAngles[i].Add(angle);
                }
            }
        }

        /// <summary>
        /// Takes resulting broadphase objects and schedules raycasts against them
        /// </summary>
        private void ExecuteVisionNarrowphase()
        {
            RaycasterRequests.Clear();
            _raycastRequestMap.Clear();

            // One straight-ahead cast per scheduled source, kept 1:1 with _scheduled
            for (int i = 0; i < _scheduled.Count; i++)
            {
                VisioncastSource source = _scheduled[i];
                RaycasterRequests.Add(new DataScheduledRaycastRequest
                {
                    SourcePosition = source.Position,
                    Direction = source.Heading.normalized,
                    MaxDistance = source.Range,
                    LayerMask = source.RaycastLayers
                });
            }

            // One cast per visible sample point, mapped back to its source/object/point
            for (int i = 0; i < _scheduled.Count; i++)
            {
                VisioncastSource source = _scheduled[i];
                Vector3 position = source.Position;
                float range = source.Range;
                int layerMask = source.RaycastLayers;

                for (int e = 0; e < _visibleObjects[i].Count; e++)
                {
                    List<Vector3> points = _visibleObjectPoints[i][e];
                    for (int y = 0; y < points.Count; y++)
                    {
                        Vector3 objectPoint = points[y];
                        RaycasterRequests.Add(new DataScheduledRaycastRequest
                        {
                            SourcePosition = position,
                            Direction = objectPoint - position,
                            MaxDistance = range,
                            LayerMask = layerMask
                        });

                        // Add matching key map
                        _raycastRequestMap.Add(new RaycastRequestMap
                        {
                            Source = i,
                            Object = e,
                            Point = y
                        });
                    }
                }
            }
        }

        void IScheduledRaycastListener.ReceiveScheduledRaycasterResults()
        {
            // Requests are 1:1 with (straight-ahead per scheduled source) + (mapped sample points).
            // _scheduled still holds the batch's source set (rebuilt only in the next tick's
            // BuildSchedule, which runs after this distribution), so this is a defensive guard.
            if (RaycasterResults.Count == _raycastRequestMap.Count + _scheduled.Count)
            {
                using (s_MarkerDistribute.Auto()) DistributeRaycasterResults();
            }
            else
            {
                Debug.LogWarning("Visioncast raycast count mismatch; skipping distribution this tick.");
            }

            // Now that we've received raycasts, we can restart visioncast
            _waitingForRaycasts = false;
            ResolveComponentQueues();
        }

        private void DistributeRaycasterResults()
        {
            // Reset each scheduled source's back result buffer (recycles its pooled point-lists) before
            // writing. Per-source ownership: the buffer reset here is the one NOT behind LastResults, so
            // nothing a consumer is still holding gets recycled - and unscheduled sources keep theirs.
            for (int i = 0; i < _scheduled.Count; i++)
                ResetSourceResultBuffer(_scheduled[i]);
            PrepareResultObjectIndex(_scheduled.Count);

            // Distribute straight-ahead raycasts
            DistributeStraightAheadCasts();

            // Distribute object-specific raycast hits
            DistributeMappedCasts();

            // Distribute results
            SendResultsToComponents();
        }

        void IScheduledRaycastListener.OnEmptyRaycastRequestResult()
        {
            _waitingForRaycasts = false;
            ResolveComponentQueues();
        }

        #endregion LOGIC


        #region DISTRIBUTION

        private void DistributeStraightAheadCasts()
        {
            for (int i = 0; i < _scheduled.Count; i++)
            {
                RaycastHit hit = RaycasterResults[i];
                if (hit.collider != null && VisionTargetsManifest.Manifest.Contains(hit.collider))
                {
                    DataVisioncastResult result = _scheduled[i].ResultBackBuffer;

                    // Denominator is the object's dedicated sample count; the straight-ahead ray is
                    // an extra confirmed point on top of it (visibility is clamped on read)
                    int broadIndex = _visibleObjects[i].IndexOf(hit.collider);
                    int sampleCount = broadIndex >= 0 ? _visibleObjectPoints[i][broadIndex].Count : 1;

                    List<Vector3> points = RentPointList();
                    points.Add(hit.point);
                    result.VisiblePoints.Add(points);
                    result.VisiblePointCounts.Add(1);
                    result.Objects.Add(hit.collider);
                    result.SampleCounts.Add(sampleCount);
                    result.Angles.Add(0);
                    result.Distances.Add(hit.distance);

                    // Record so DistributeMappedCasts finds this object in O(1)
                    _resultObjectIndex[i][hit.collider] = result.Objects.Count - 1;
                }
            }
        }

        private void DistributeMappedCasts()
        {
            for (int i = _scheduled.Count; i < RaycasterResults.Count; i++)
            {
                RaycastHit hit = RaycasterResults[i];
                RaycastRequestMap map = _raycastRequestMap[i - _scheduled.Count];
                DataVisioncastResult result = _scheduled[map.Source].ResultBackBuffer;
                Dictionary<Collider, int> objectIndexMap = _resultObjectIndex[map.Source];
                Collider thisObject = _visibleObjects[map.Source][map.Object];
                Vector3 thisPoint = _visibleObjectPoints[map.Source][map.Object][map.Point];

                // Add object to source results if not present (O(1) via the per-source index map)
                if (!objectIndexMap.TryGetValue(thisObject, out int objectIndex))
                {
                    result.Objects.Add(thisObject);
                    result.SampleCounts.Add(_visibleObjectPoints[map.Source][map.Object].Count);
                    result.Angles.Add(_visibleObjectAngles[map.Source][map.Object]);
                    result.Distances.Add(_visibleObjectDistances[map.Source][map.Object]);
                    result.VisiblePoints.Add(RentPointList());
                    result.VisiblePointCounts.Add(0);
                    objectIndex = result.Objects.Count - 1;
                    objectIndexMap[thisObject] = objectIndex;
                }

                // Did we hit the object itself?
                if (hit.collider && hit.collider == thisObject)
                {
                    result.VisiblePoints[objectIndex].Add(thisPoint);
                    result.VisiblePointCounts[objectIndex]++;
                }
            }
        }

        private void SendResultsToComponents()
        {
            for (int i = 0; i < _scheduled.Count; i++)
            {
                VisioncastSource source = _scheduled[i];

                // The managed narrowphase gates the FOV cone per TARGET (not per sample point), so every
                // reported sample is "in cone" and a reached sample is therefore "lit". Fill the in-cone /
                // lit counts (aligned with Objects) so consumers read the same contract as the DoD path.
                DataVisioncastResult buf = source.ResultBackBuffer;
                buf.InConeCounts.Clear();
                buf.LitCounts.Clear();
                for (int o = 0; o < buf.Objects.Count; o++)
                {
                    buf.InConeCounts.Add(buf.SampleCounts[o]);
                    buf.LitCounts.Add(buf.VisiblePointCounts[o]);
                }

                // A source destroyed between schedule and distribution: its buffer writes were harmless
                // (managed access), but skip delivery so OnReceiveResults never touches a dead transform
                if (source.transform != null)
                    source.DeliverResults(_visionTime);
            }
        }

        #endregion DISTRIBUTION


        #region BUFFERS

        /// <summary>
        /// Resets the aligned intermediate buffers for a tick over <paramref name="count"/> sources,
        /// reusing existing container/point lists so the narrowphase does not allocate per frame.
        /// </summary>
        private void PrepareBuffers(int count)
        {
            // Return last tick's broadphase point-lists before the buffers are reused
            for (int i = 0; i < _visibleObjectPoints.Count; i++)
            {
                List<List<Vector3>> objectPoints = _visibleObjectPoints[i];
                for (int e = 0; e < objectPoints.Count; e++)
                    ReturnPointList(objectPoints[e]);
                objectPoints.Clear();
            }

            // Grow outer buffers to match the component count, reusing containers
            while (_visibleObjects.Count < count)
            {
                _visibleObjects.Add(new List<Collider>());
                _visibleObjectPoints.Add(new List<List<Vector3>>());
                _visibleObjectDistances.Add(new List<float>());
                _visibleObjectAngles.Add(new List<float>());
            }

            // Reset the active range; capacity is retained so no per-tick allocation occurs
            for (int i = 0; i < count; i++)
            {
                _visibleObjects[i].Clear();
                _visibleObjectDistances[i].Clear();
                _visibleObjectAngles[i].Clear();
            }
        }

        /// <summary>
        /// Grows and clears the per-source broadphase candidate lists for a query over
        /// <paramref name="count"/> scheduled sources. Reused each tick (capacity retained).
        /// </summary>
        private void PrepareBroadphaseCandidates(int count)
        {
            while (_broadphaseCandidates.Count < count)
                _broadphaseCandidates.Add(new List<Collider>());

            for (int i = 0; i < count; i++)
                _broadphaseCandidates[i].Clear();
        }

        /// <summary>
        /// Grows and clears the per-source result-object index maps for a distribution over
        /// <paramref name="count"/> sources. Reused each tick so distribution allocates no maps.
        /// </summary>
        private void PrepareResultObjectIndex(int count)
        {
            while (_resultObjectIndex.Count < count)
                _resultObjectIndex.Add(new Dictionary<Collider, int>());

            for (int i = 0; i < count; i++)
                _resultObjectIndex[i].Clear();
        }

        /// <summary>
        /// Recycles a source's back result buffer before it is rewritten this distribution. Returns its
        /// point-lists to the shared pool; the buffer is the one NOT behind LastResults, so nothing a
        /// consumer still holds is recycled.
        /// </summary>
        private void ResetSourceResultBuffer(VisioncastSource source)
        {
            DataVisioncastResult buffer = source.ResultBackBuffer;

            for (int e = 0; e < buffer.VisiblePoints.Count; e++)
                ReturnPointList(buffer.VisiblePoints[e]);
            buffer.VisiblePoints.Clear();

            buffer.Objects.Clear();
            buffer.VisiblePointCounts.Clear();
            buffer.SampleCounts.Clear();
            buffer.InConeCounts.Clear();
            buffer.LitCounts.Clear();
            buffer.Distances.Clear();
            buffer.Angles.Clear();
        }

        private List<Vector3> RentPointList()
        {
            List<Vector3> list = _pointListPool.Count > 0
                ? _pointListPool.Pop()
                : new List<Vector3>(CONST_PointListCapacity);
            list.Clear();
            return list;
        }

        private void ReturnPointList(List<Vector3> list)
        {
            if (list == null)
                return;

            _pointListPool.Push(list);
        }

        #endregion BUFFERS


        #region UTILITY

        /// <summary>
        /// Collider.ClosestPoint throws on non-convex mesh colliders; those fall back to the
        /// (looser) bounds approximation so a mesh target cannot bring down the whole tick.
        /// </summary>
        private static Vector3 GetClosestPoint(Collider col, Vector3 position)
        {
            if (col is MeshCollider { convex: false })
                return col.bounds.ClosestPoint(position);

            return col.ClosestPoint(position);
        }

        #endregion UTILITY


        #region RESET

        public void Dispose()
        {
            _destroyed = true;
            _broadphase?.Dispose();
            _dodPipeline?.Dispose();
            _components.Clear();
            _scheduled.Clear();
            _queuedAdd.Clear();
            _queuedRemove.Clear();
        }

        #endregion RESET


        #region DATA

        /// <summary>
        /// Maps a scheduled raycast back to the source/object/point it was cast for
        /// </summary>
        private struct RaycastRequestMap
        {
            public int Source;
            public int Object;
            public int Point;
        }

        #endregion DATA
    }
}
