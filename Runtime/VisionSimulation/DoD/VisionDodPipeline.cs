using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
// See VisionTargetGather: 6.5 replaces InstanceID (int) with EntityId for object identity.
#if UNITY_6000_5_OR_NEWER
using ColliderId = UnityEngine.EntityId;
#else
using ColliderId = System.Int32;
#endif

namespace GalaxyGourd.Visioncast
{
    /// <summary>
    /// DoD narrowphase (step 3): the whole per-tick vision computation as a Burst pipeline, replacing the
    /// managed broadphase-narrow + request build + distribute. Gather (targets via VisionTargetGather,
    /// sources into flat arrays) -> spatial-grid broadphase + OBB closest-point/angle filter (one job) ->
    /// Burst sample-gen building the RaycastCommand batch -> RaycastCommand.ScheduleBatch -> per-pair
    /// hit-reduce (by collider instance id) -> scatter blittable results into each source's
    /// ResultBackBuffer. No collider crosses into a job; VisiblePoints is not built (debug-only now -
    /// consumers read VisiblePointCounts).
    ///
    /// Synchronous (schedules and completes within Execute); the schedule/complete split for the physics
    /// window is a later step. Owned by the Visioncaster when DoD mode is enabled.
    /// </summary>
    internal sealed class VisionDodPipeline : IDisposable
    {
        #region VARIABLES

        private const int CONST_MaxCandidatesPerSource = 64;
        private const int CONST_MinRaysPerJob = 32;

        private readonly VisionTargetGather _gather;
        private readonly float _cellSize;

        // Source-side gather (main-thread read of MB handles), grow-on-demand, indexed by scheduled slot.
        private NativeArray<float3> _sPos;
        private NativeArray<float3> _sHeading;
        private NativeArray<float> _sFovCos;
        private NativeArray<float> _sRange;
        private NativeArray<int> _sBroadMask;
        private NativeArray<int> _sRayMask;
        private NativeArray<int> _sMode;
        private NativeArray<int> _sRes;
        private int _sourceCapacity;

        private NativeParallelMultiHashMap<int, int> _grid;
        private NativeList<PairResult> _survivors;
        private NativeArray<int> _rayOffset;    // prefix sum, length survivors+1
        private NativeArray<int> _visibleCount; // per survivor: rays that reached the target (occlusion, cone-agnostic)
        private NativeArray<int> _inConeCount;  // per survivor: sample points inside the source's cone + range
        private NativeArray<int> _litCount;     // per survivor: sample points both in-cone AND reached
        private int _pairCapacity;
        private NativeArray<RaycastCommand> _rayCommands;
        private NativeArray<RaycastHit> _rayHits;
        private NativeArray<byte> _rayInCone;   // per ray: 1 if its sample point is in the source's cone + range
        private int _rayCapacity;

        // Debug-only: the world-space sample point each ray was aimed at, so a source with a visualizer
        // attached can rebuild DataVisioncastResult.VisiblePoints. Off the hot path - only filled when some
        // scheduled source has a debug component, otherwise a length-1 dummy the job never writes.
        private NativeArray<float3> _raySamplePoints;
        private bool _captureDebugPoints;
        private readonly Stack<List<Vector3>> _pointListPool = new();

        // Deferred (ST1) state, held between ScheduleBatch and CompleteAndScatter. While a batch is pending
        // the native buffers above must not be reused, which the Visioncaster gate guarantees (no second
        // ScheduleBatch before CompleteAndScatter). _batchSources snapshots the scheduled source references
        // since the caller's scheduled list is rebuilt next tick.
        private readonly List<VisioncastSource> _batchSources = new();
        private int _batchCount;
        private int _batchN;
        private float _batchVisionTime;
        private JobHandle _raycastHandle;
        private bool _batchPending;

        /// <summary>True between ScheduleBatch and CompleteAndScatter (a raycast batch is in flight).</summary>
        internal bool BatchPending => _batchPending;

        #endregion VARIABLES


        #region LIFECYCLE

        internal VisionDodPipeline(float cellSize = 20f)
        {
            _cellSize = Mathf.Max(0.01f, cellSize);
            _gather = new VisionTargetGather();
        }

        public void Dispose()
        {
            if (_batchPending)
            {
                _raycastHandle.Complete(); // a raycast job in flight references the buffers we are about to free
                _batchPending = false;
            }

            _gather.Dispose();
            DisposeIf(ref _sPos); DisposeIf(ref _sHeading); DisposeIf(ref _sFovCos); DisposeIf(ref _sRange);
            DisposeIf(ref _sBroadMask); DisposeIf(ref _sRayMask); DisposeIf(ref _sMode); DisposeIf(ref _sRes);
            if (_grid.IsCreated) _grid.Dispose();
            if (_survivors.IsCreated) _survivors.Dispose();
            DisposeIf(ref _rayOffset); DisposeIf(ref _visibleCount);
            DisposeIf(ref _inConeCount); DisposeIf(ref _litCount);
            DisposeIf(ref _rayCommands); DisposeIf(ref _rayHits); DisposeIf(ref _rayInCone);
            DisposeIf(ref _raySamplePoints);
        }

        private static void DisposeIf<T>(ref NativeArray<T> a) where T : struct
        {
            if (a.IsCreated) a.Dispose();
        }

        #endregion LIFECYCLE


        #region EXECUTE

        /// <summary>Synchronous path (schedule + complete in one call). Used by the standalone driver.</summary>
        internal void Execute(List<VisioncastSource> scheduled, int count, float visionTime)
        {
            ScheduleBatch(scheduled, count, visionTime);
            CompleteAndScatter();
        }

        /// <summary>
        /// Deferred phase 1: gather + grid + filter + sample-gen, then schedule the RaycastCommand batch on
        /// worker threads WITHOUT completing it. The batch runs across the gap until <see cref="CompleteAndScatter"/>.
        /// The caller must not schedule again while <see cref="BatchPending"/>, and must ensure no Physics.Simulate
        /// or collider mutation occurs before the completing call (the RaycastCommand job reads the PhysicsScene).
        /// </summary>
        internal void ScheduleBatch(List<VisioncastSource> scheduled, int count, float visionTime)
        {
            _batchPending = false;

            // Snapshot the scheduled source references for the complete phase (the caller rebuilds its list).
            // Capture visible-point vectors only if something is going to draw them (see HasDebug).
            _batchSources.Clear();
            _captureDebugPoints = false;
            for (int i = 0; i < count; i++)
            {
                VisioncastSource src = scheduled[i];
                _batchSources.Add(src);
                _captureDebugPoints |= src.HasDebug;
            }
            _batchCount = count;
            _batchVisionTime = visionTime;

            if (count == 0)
                return;

            _gather.SyncIfDirty();
            int targetCount = _gather.Count;

            GatherSources(scheduled, count);
            EnsureGrid(targetCount, count);
            _grid.Clear();
            _survivors.Clear();

            if (targetCount == 0)
            {
                DeliverEmpty(scheduled, count, visionTime); // nothing to raycast; deliver empty now
                return;
            }

            NativeArray<TargetObb> obbs = _gather.WorldObbs;

            JobHandle gh = _gather.Schedule();
            var build = new BuildGridJob { Obbs = obbs, CellSize = _cellSize, Grid = _grid.AsParallelWriter() }
                .Schedule(targetCount, 64, gh);
            var query = new QueryFilterJob
            {
                Grid = _grid, Obbs = obbs, Layers = _gather.Layers,
                SPos = _sPos, SHeading = _sHeading, SFovCos = _sFovCos, SRange = _sRange, SBroadMask = _sBroadMask,
                CellSize = _cellSize, Survivors = _survivors.AsParallelWriter()
            }.Schedule(count, 16, build);
            query.Complete();  // sync point: survivor count needed for the ray prefix sum
            _gather.Complete();

            int n = _survivors.Length;
            _batchN = n;
            if (n == 0)
            {
                ResetAndDeliver(scheduled, count, visionTime);
                return;
            }

            // Prefix sum of per-survivor ray counts (closest point + sample grid), main thread.
            EnsurePairBuffers(n);
            int totalRays = 0;
            for (int p = 0; p < n; p++)
            {
                _rayOffset[p] = totalRays;
                int s = _survivors[p].Source;
                totalRays += RayCount(_sMode[s], _sRes[s]);
            }
            _rayOffset[n] = totalRays;

            EnsureRayBuffers(totalRays);
            var sampleGen = new SampleGenJob
            {
                Survivors = _survivors.AsArray(), Obbs = obbs, RayOffset = _rayOffset,
                SPos = _sPos, SHeading = _sHeading, SFovCos = _sFovCos, SRange = _sRange,
                SRayMask = _sRayMask, SMode = _sMode, SRes = _sRes,
                RayCommands = _rayCommands, RayInCone = _rayInCone,
                RaySamplePoints = _raySamplePoints, Capture = _captureDebugPoints
            }.Schedule(n, 16);

            // Kick the raycast onto workers (depends on sample-gen); do NOT complete it here.
            _raycastHandle = RaycastCommand.ScheduleBatch(
                _rayCommands.GetSubArray(0, totalRays), _rayHits.GetSubArray(0, totalRays), CONST_MinRaysPerJob, sampleGen);
            JobHandle.ScheduleBatchedJobs();
            _batchPending = true;
        }

        /// <summary>
        /// Deferred phase 2: complete the batch scheduled by <see cref="ScheduleBatch"/> (returns immediately if
        /// it already finished on workers during the gap), reduce hits, and scatter results to the sources.
        /// No-op if no batch is pending. In the synchronous path this runs right after ScheduleBatch.
        /// </summary>
        internal void CompleteAndScatter()
        {
            if (!_batchPending)
                return;

            _batchPending = false;
            _raycastHandle.Complete();

            new ReduceJob
            {
                Survivors = _survivors.AsArray(), RayOffset = _rayOffset, RayHits = _rayHits,
                RayInCone = _rayInCone, InstanceIds = _gather.InstanceIds,
                VisibleCount = _visibleCount, InConeCount = _inConeCount, LitCount = _litCount
            }.Schedule(_batchN, 16).Complete();

            Scatter(_batchSources, _batchCount, _batchN, _batchVisionTime);
        }

        private void GatherSources(List<VisioncastSource> scheduled, int count)
        {
            EnsureSourceCapacity(count);
            for (int i = 0; i < count; i++)
            {
                VisioncastSource src = scheduled[i];
                _sPos[i] = src.Position;
                _sHeading[i] = ((float3)src.Heading) / math.max(math.length(src.Heading), 1e-6f);
                _sFovCos[i] = math.cos(math.radians(src.FieldOfView));
                _sRange[i] = src.Range;
                _sBroadMask[i] = src.BroadphaseLayers.value;
                _sRayMask[i] = src.RaycastLayers.value;
                _sMode[i] = (int)src.SampleMode;

                int tierRes = VisionLOD.SampleResolutionForTier(src.ScheduleTier);
                _sRes[i] = tierRes > 0 ? Mathf.Min(src.SampleResolution, tierRes) : src.SampleResolution;
            }
        }

        private void Scatter(List<VisioncastSource> scheduled, int count, int n, float visionTime)
        {
            // A source can be destroyed between ScheduleBatch and CompleteAndScatter (deferred path); its
            // members throw once destroyed, so guard every access with the Unity-object null check.
            for (int i = 0; i < count; i++)
                if (scheduled[i])
                    ResetBuffer(scheduled[i].ResultBackBuffer);

            for (int p = 0; p < n; p++)
            {
                PairResult pr = _survivors[p];
                Collider col = VisionTargetsManifest.GetCollider(pr.TargetId);
                if (col == null)
                    continue;

                VisioncastSource src = scheduled[pr.Source];
                if (!src)
                    continue;
                DataVisioncastResult buf = src.ResultBackBuffer;
                buf.Objects.Add(col);
                buf.SampleCounts.Add(_rayOffset[p + 1] - _rayOffset[p]);
                buf.Distances.Add(pr.Distance);
                buf.Angles.Add(pr.Angle);
                buf.VisiblePointCounts.Add(_visibleCount[p]);
                buf.InConeCounts.Add(_inConeCount[p]);
                buf.LitCounts.Add(_litCount[p]);

                // Only a source being visualized pays for the point vectors; kept parallel to its Objects.
                if (_captureDebugPoints && src.HasDebug)
                    buf.VisiblePoints.Add(CollectVisiblePoints(p));
            }

            for (int i = 0; i < count; i++)
                if (scheduled[i] && scheduled[i].transform != null)
                    scheduled[i].DeliverResults(visionTime);
        }

        /// <summary>
        /// Rebuilds one object's visible world-space points for the debug visualizer: the sample points of
        /// the rays in this pair's range that actually reached the target (same identity test the reduce job
        /// uses). Debug-only, so the main-thread re-test costs nothing in a normal build.
        /// </summary>
        private List<Vector3> CollectVisiblePoints(int p)
        {
            List<Vector3> points = _pointListPool.Count > 0 ? _pointListPool.Pop() : new List<Vector3>();
            points.Clear();

            ColliderId targetId = _gather.InstanceIds[_survivors[p].TargetId];
            for (int r = _rayOffset[p]; r < _rayOffset[p + 1]; r++)
            {
#if UNITY_6000_5_OR_NEWER
                if (_rayHits[r].colliderEntityId == targetId)
#else
                if (_rayHits[r].colliderInstanceID == targetId)
#endif
                    points.Add(_raySamplePoints[r]);
            }

            return points;
        }

        private void ResetAndDeliver(List<VisioncastSource> scheduled, int count, float visionTime)
        {
            for (int i = 0; i < count; i++)
                if (scheduled[i])
                    ResetBuffer(scheduled[i].ResultBackBuffer);
            for (int i = 0; i < count; i++)
                if (scheduled[i] && scheduled[i].transform != null)
                    scheduled[i].DeliverResults(visionTime);
        }

        private void DeliverEmpty(List<VisioncastSource> scheduled, int count, float visionTime)
        {
            ResetAndDeliver(scheduled, count, visionTime);
        }

        private void ResetBuffer(DataVisioncastResult buf)
        {
            // Recycle any debug point-lists from the previous update (empty and free in a normal build)
            for (int i = 0; i < buf.VisiblePoints.Count; i++)
                _pointListPool.Push(buf.VisiblePoints[i]);
            buf.VisiblePoints.Clear();

            buf.Objects.Clear();
            buf.SampleCounts.Clear();
            buf.Distances.Clear();
            buf.Angles.Clear();
            buf.VisiblePointCounts.Clear();
            buf.InConeCounts.Clear();
            buf.LitCounts.Clear();
        }

        internal static int RayCount(int mode, int res)
        {
            int n = math.max(1, res);
            return 1 + (mode == (int)VisionSampleMode.BoundsVolumeGrid ? n * n * n : 6 * n * n);
        }

        #endregion EXECUTE


        #region CAPACITY

        private void EnsureSourceCapacity(int count)
        {
            if (_sPos.IsCreated && _sourceCapacity >= count)
                return;

            DisposeIf(ref _sPos); DisposeIf(ref _sHeading); DisposeIf(ref _sFovCos); DisposeIf(ref _sRange);
            DisposeIf(ref _sBroadMask); DisposeIf(ref _sRayMask); DisposeIf(ref _sMode); DisposeIf(ref _sRes);

            _sourceCapacity = Mathf.Max(1, Mathf.NextPowerOfTwo(count));
            _sPos = new NativeArray<float3>(_sourceCapacity, Allocator.Persistent);
            _sHeading = new NativeArray<float3>(_sourceCapacity, Allocator.Persistent);
            _sFovCos = new NativeArray<float>(_sourceCapacity, Allocator.Persistent);
            _sRange = new NativeArray<float>(_sourceCapacity, Allocator.Persistent);
            _sBroadMask = new NativeArray<int>(_sourceCapacity, Allocator.Persistent);
            _sRayMask = new NativeArray<int>(_sourceCapacity, Allocator.Persistent);
            _sMode = new NativeArray<int>(_sourceCapacity, Allocator.Persistent);
            _sRes = new NativeArray<int>(_sourceCapacity, Allocator.Persistent);
        }

        private void EnsureGrid(int targetCount, int sourceCount)
        {
            int gridNeeded = Mathf.Max(1, targetCount);
            if (!_grid.IsCreated || _grid.Capacity < gridNeeded)
            {
                if (_grid.IsCreated) _grid.Dispose();
                _grid = new NativeParallelMultiHashMap<int, int>(Mathf.NextPowerOfTwo(gridNeeded), Allocator.Persistent);
            }

            int pairNeeded = Mathf.Max(1, sourceCount * CONST_MaxCandidatesPerSource);
            if (!_survivors.IsCreated)
                _survivors = new NativeList<PairResult>(pairNeeded, Allocator.Persistent);
            else if (_survivors.Capacity < pairNeeded)
                _survivors.Capacity = pairNeeded;
        }

        private void EnsurePairBuffers(int n)
        {
            if (_rayOffset.IsCreated && _pairCapacity >= n)
                return;

            DisposeIf(ref _rayOffset); DisposeIf(ref _visibleCount);
            DisposeIf(ref _inConeCount); DisposeIf(ref _litCount);
            _pairCapacity = Mathf.Max(1, Mathf.NextPowerOfTwo(n));
            _rayOffset = new NativeArray<int>(_pairCapacity + 1, Allocator.Persistent);
            _visibleCount = new NativeArray<int>(_pairCapacity, Allocator.Persistent);
            _inConeCount = new NativeArray<int>(_pairCapacity, Allocator.Persistent);
            _litCount = new NativeArray<int>(_pairCapacity, Allocator.Persistent);
        }

        private void EnsureRayBuffers(int totalRays)
        {
            if (!_rayCommands.IsCreated || _rayCapacity < totalRays)
            {
                DisposeIf(ref _rayCommands); DisposeIf(ref _rayHits); DisposeIf(ref _rayInCone);
                _rayCapacity = Mathf.Max(1, Mathf.NextPowerOfTwo(totalRays));
                _rayCommands = new NativeArray<RaycastCommand>(_rayCapacity, Allocator.Persistent);
                _rayHits = new NativeArray<RaycastHit>(_rayCapacity, Allocator.Persistent);
                _rayInCone = new NativeArray<byte>(_rayCapacity, Allocator.Persistent);
                DisposeIf(ref _raySamplePoints); // must match the ray capacity when capturing
            }

            // Debug capture buffer: full size while a visualizer is attached, otherwise a dummy the job
            // never writes (a job field can't be an uncreated array).
            int needed = _captureDebugPoints ? _rayCapacity : 1;
            if (!_raySamplePoints.IsCreated || _raySamplePoints.Length != needed)
            {
                DisposeIf(ref _raySamplePoints);
                _raySamplePoints = new NativeArray<float3>(needed, Allocator.Persistent);
            }
        }

        #endregion CAPACITY


        #region MATH

        private static int3 CellOf(float3 p, float cellSize) => (int3)math.floor(p / cellSize);
        private static int HashCell(int3 c) => (int)math.hash(c);

        private static float3 ClosestPointOnObb(float3 p, in TargetObb o)
        {
            float3 d = p - o.Center;
            return o.Center
                + math.clamp(math.dot(d, o.AxisX), -o.Extents.x, o.Extents.x) * o.AxisX
                + math.clamp(math.dot(d, o.AxisY), -o.Extents.y, o.Extents.y) * o.AxisY
                + math.clamp(math.dot(d, o.AxisZ), -o.Extents.z, o.Extents.z) * o.AxisZ;
        }

        private static float CellCoord(int index, int n) => math.lerp(-1f, 1f, (index + 0.5f) / n);

        #endregion MATH


        #region JOBS

        internal struct PairResult
        {
            public int Source;
            public int TargetId;
            public float3 ClosestPoint;
            public float Distance;
            public float Angle;
        }

        [BurstCompile]
        private struct BuildGridJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<TargetObb> Obbs;
            public float CellSize;
            public NativeParallelMultiHashMap<int, int>.ParallelWriter Grid;

            public void Execute(int index)
            {
                Grid.Add(HashCell(CellOf(Obbs[index].Center, CellSize)), index);
            }
        }

        [BurstCompile]
        private struct QueryFilterJob : IJobParallelFor
        {
            [ReadOnly] public NativeParallelMultiHashMap<int, int> Grid;
            [ReadOnly] public NativeArray<TargetObb> Obbs;
            [ReadOnly] public NativeArray<int> Layers;
            [ReadOnly] public NativeArray<float3> SPos;
            [ReadOnly] public NativeArray<float3> SHeading;
            [ReadOnly] public NativeArray<float> SFovCos;
            [ReadOnly] public NativeArray<float> SRange;
            [ReadOnly] public NativeArray<int> SBroadMask;
            public float CellSize;
            public NativeList<PairResult>.ParallelWriter Survivors;

            public void Execute(int s)
            {
                float3 p = SPos[s];
                float3 heading = SHeading[s];
                float fovCos = SFovCos[s];
                float r = SRange[s];
                float r2 = r * r;
                int mask = SBroadMask[s];

                int3 cmin = CellOf(p - r, CellSize);
                int3 cmax = CellOf(p + r, CellSize);

                for (int cz = cmin.z; cz <= cmax.z; cz++)
                for (int cy = cmin.y; cy <= cmax.y; cy++)
                for (int cx = cmin.x; cx <= cmax.x; cx++)
                {
                    int3 cell = new int3(cx, cy, cz);
                    if (!Grid.TryGetFirstValue(HashCell(cell), out int t, out var it))
                        continue;

                    do
                    {
                        TargetObb o = Obbs[t];
                        if (!math.all(CellOf(o.Center, CellSize) == cell))
                            continue;                                     // hash-collision / foreign-cell guard
                        if ((mask & (1 << Layers[t])) == 0)
                            continue;                                     // broadphase layer filter
                        if (math.distancesq(p, o.Center) > r2)
                            continue;                                     // range by center (grid semantic)

                        // Narrowphase angle filter against the OBB closest point.
                        float3 closest = ClosestPointOnObb(p, o);
                        float3 dir = closest - p;
                        float lenSq = math.lengthsq(dir);
                        float angle;
                        if (lenSq < 1e-10f)
                        {
                            angle = 0f;
                        }
                        else
                        {
                            float d = math.dot(dir * math.rsqrt(lenSq), heading);
                            if (d < fovCos)
                                continue;                                 // outside the cone
                            angle = math.degrees(math.acos(math.clamp(d, -1f, 1f)));
                        }

                        Survivors.AddNoResize(new PairResult
                        {
                            Source = s, TargetId = t, ClosestPoint = closest,
                            Distance = math.sqrt(lenSq), Angle = angle
                        });
                    }
                    while (Grid.TryGetNextValue(out t, ref it));
                }
            }
        }

        [BurstCompile]
        private struct SampleGenJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<PairResult> Survivors;
            [ReadOnly] public NativeArray<TargetObb> Obbs;
            [ReadOnly] public NativeArray<int> RayOffset;
            [ReadOnly] public NativeArray<float3> SPos;
            [ReadOnly] public NativeArray<float3> SHeading;
            [ReadOnly] public NativeArray<float> SFovCos;
            [ReadOnly] public NativeArray<float> SRange;
            [ReadOnly] public NativeArray<int> SRayMask;
            [ReadOnly] public NativeArray<int> SMode;
            [ReadOnly] public NativeArray<int> SRes;
            [NativeDisableParallelForRestriction] public NativeArray<RaycastCommand> RayCommands;
            [NativeDisableParallelForRestriction] public NativeArray<byte> RayInCone;
            [NativeDisableParallelForRestriction] public NativeArray<float3> RaySamplePoints;
            public bool Capture;

            public void Execute(int p)
            {
                PairResult pr = Survivors[p];
                int s = pr.Source;
                float3 from = SPos[s];
                float3 heading = SHeading[s];
                float fovCos = SFovCos[s];
                float range = SRange[s];
                int mask = SRayMask[s];
                int w = RayOffset[p];

                Emit(from, pr.ClosestPoint, heading, fovCos, range, mask, ref w);

                TargetObb o = Obbs[pr.TargetId];
                int n = math.max(1, SRes[s]);
                float3 ex = o.AxisX * o.Extents.x;
                float3 ey = o.AxisY * o.Extents.y;
                float3 ez = o.AxisZ * o.Extents.z;

                if (SMode[s] == (int)VisionSampleMode.BoundsVolumeGrid)
                {
                    for (int x = 0; x < n; x++)
                    {
                        float fx = CellCoord(x, n);
                        for (int y = 0; y < n; y++)
                        {
                            float fy = CellCoord(y, n);
                            for (int z = 0; z < n; z++)
                                Emit(from, o.Center + ex * fx + ey * fy + ez * CellCoord(z, n), heading, fovCos, range, mask, ref w);
                        }
                    }
                }
                else
                {
                    Face(o.Center + ex, ey, ez, n, from, heading, fovCos, range, mask, ref w);
                    Face(o.Center - ex, ey, ez, n, from, heading, fovCos, range, mask, ref w);
                    Face(o.Center + ey, ex, ez, n, from, heading, fovCos, range, mask, ref w);
                    Face(o.Center - ey, ex, ez, n, from, heading, fovCos, range, mask, ref w);
                    Face(o.Center + ez, ex, ey, n, from, heading, fovCos, range, mask, ref w);
                    Face(o.Center - ez, ex, ey, n, from, heading, fovCos, range, mask, ref w);
                }
            }

            private void Face(float3 fc, float3 u, float3 v, int n, float3 from, float3 heading, float fovCos, float range, int mask, ref int w)
            {
                for (int a = 0; a < n; a++)
                {
                    float fu = CellCoord(a, n);
                    for (int b = 0; b < n; b++)
                        Emit(from, fc + u * fu + v * CellCoord(b, n), heading, fovCos, range, mask, ref w);
                }
            }

            /// <summary>
            /// Writes one ray, flags whether its sample point is in the beam (angular cone AND within range),
            /// and stores the point when a visualizer needs to draw it.
            /// </summary>
            private void Emit(float3 from, float3 to, float3 heading, float fovCos, float range, int mask, ref int w)
            {
                float3 d = to - from;
                float len = math.length(d);
                float3 dir = len > 1e-6f ? d / len : new float3(0f, 0f, 1f);
                RayCommands[w] = new RaycastCommand(from, dir, new QueryParameters(mask), range);
                RayInCone[w] = (byte)((len <= range && math.dot(dir, heading) >= fovCos) ? 1 : 0);
                if (Capture)
                    RaySamplePoints[w] = to;
                w++;
            }
        }

        [BurstCompile]
        private struct ReduceJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<PairResult> Survivors;
            [ReadOnly] public NativeArray<int> RayOffset;
            [ReadOnly] public NativeArray<RaycastHit> RayHits;
            [ReadOnly] public NativeArray<byte> RayInCone;
            [ReadOnly] public NativeArray<ColliderId> InstanceIds;
            [NativeDisableParallelForRestriction] public NativeArray<int> VisibleCount; // reached (occlusion, cone-agnostic)
            [NativeDisableParallelForRestriction] public NativeArray<int> InConeCount;  // in the beam
            [NativeDisableParallelForRestriction] public NativeArray<int> LitCount;     // in the beam AND reached

            public void Execute(int p)
            {
                ColliderId targetId = InstanceIds[Survivors[p].TargetId];
                int reached = 0, inCone = 0, lit = 0;
                for (int r = RayOffset[p]; r < RayOffset[p + 1]; r++)
                {
#if UNITY_6000_5_OR_NEWER
                    bool hit = RayHits[r].colliderEntityId == targetId;
#else
                    bool hit = RayHits[r].colliderInstanceID == targetId;
#endif
                    bool ic = RayInCone[r] != 0;
                    if (hit) reached++;
                    if (ic) inCone++;
                    if (ic & hit) lit++;
                }

                VisibleCount[p] = reached;
                InConeCount[p] = inCone;
                LitCount[p] = lit;
            }
        }

        #endregion JOBS
    }
}
