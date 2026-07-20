using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GalaxyGourd.Visioncast
{
    /// <summary>
    /// DoD broadphase (step 2): a Burst spatial hash grid over the manifest's target centers, replacing the
    /// per-source Physics.OverlapSphere. It considers only real registered targets, touches no PhysicsScene
    /// (so no physics-window constraint), and runs the query across worker threads. Behind the same
    /// <see cref="IBroadphase"/> seam as the physics broadphase; opt in via
    /// <see cref="VisionBroadphase.UseGrid"/>.
    ///
    /// The grid works in native target ids and only materializes colliders at its boundary to satisfy the
    /// current managed narrowphase - a temporary shim the step-3 native pipeline removes. Candidate sets
    /// match OverlapSphere (range + broadphase-layer filter), so it is a drop-in with parity.
    /// </summary>
    internal sealed class VisionGridBroadphase : IBroadphase
    {
        #region VARIABLES

        private const int CONST_MaxCandidatesPerSource = 64;

        private readonly VisionTargetGather _gather;
        private readonly float _cellSize;

        // Source-side gather (main-thread read of MB handles), grow-on-demand.
        private NativeArray<float3> _sourcePos;
        private NativeArray<float> _sourceRange;
        private NativeArray<int> _sourceMask;
        private int _sourceCapacity;

        // cellHash -> targetId, and sourceSlot -> targetId. Rebuilt each Query.
        private NativeParallelMultiHashMap<int, int> _grid;
        private NativeParallelMultiHashMap<int, int> _candidates;

        #endregion VARIABLES


        #region LIFECYCLE

        internal VisionGridBroadphase(float cellSize = 20f)
        {
            _cellSize = Mathf.Max(0.01f, cellSize);
            _gather = new VisionTargetGather();
        }

        public void Dispose()
        {
            _gather.Dispose();
            if (_sourcePos.IsCreated) _sourcePos.Dispose();
            if (_sourceRange.IsCreated) _sourceRange.Dispose();
            if (_sourceMask.IsCreated) _sourceMask.Dispose();
            if (_grid.IsCreated) _grid.Dispose();
            if (_candidates.IsCreated) _candidates.Dispose();
        }

        #endregion LIFECYCLE


        #region QUERY

        public void Query(List<VisioncastSource> sources, int count, List<List<Collider>> candidates)
        {
            if (count == 0)
                return;

            _gather.SyncIfDirty();
            int targetCount = _gather.Count;

            EnsureSourceCapacity(count);
            for (int i = 0; i < count; i++)
            {
                VisioncastSource source = sources[i];
                _sourcePos[i] = source.Position;
                _sourceRange[i] = source.Range;
                _sourceMask[i] = source.BroadphaseLayers.value;
            }

            EnsureGridCapacity(targetCount, count);
            _grid.Clear();
            _candidates.Clear();

            if (targetCount == 0)
                return; // no targets: every candidate list stays empty

            JobHandle gatherHandle = _gather.Schedule();
            NativeArray<float3> centers = _gather.WorldCenters;
            NativeArray<int> layers = _gather.Layers;

            var build = new BuildGridJob
            {
                Centers = centers,
                CellSize = _cellSize,
                Grid = _grid.AsParallelWriter()
            }.Schedule(targetCount, 64, gatherHandle);

            var query = new QueryJob
            {
                Grid = _grid,
                Centers = centers,
                Layers = layers,
                SourcePos = _sourcePos,
                SourceRange = _sourceRange,
                SourceMask = _sourceMask,
                CellSize = _cellSize,
                Candidates = _candidates.AsParallelWriter()
            }.Schedule(count, 16, build);

            query.Complete();
            _gather.Complete();

            // Boundary shim: translate native (source, targetId) pairs back to colliders for the current
            // managed narrowphase. Step 3 removes this - the native pipeline consumes ids directly.
            for (int s = 0; s < count; s++)
            {
                List<Collider> list = candidates[s];
                if (!_candidates.TryGetFirstValue(s, out int targetId, out var it))
                    continue;

                do
                {
                    Collider col = VisionTargetsManifest.GetCollider(targetId);
                    if (col != null)
                        list.Add(col);
                }
                while (_candidates.TryGetNextValue(out targetId, ref it));
            }
        }

        #endregion QUERY


        #region CAPACITY

        private void EnsureSourceCapacity(int count)
        {
            if (_sourcePos.IsCreated && _sourceCapacity >= count)
                return;

            if (_sourcePos.IsCreated) _sourcePos.Dispose();
            if (_sourceRange.IsCreated) _sourceRange.Dispose();
            if (_sourceMask.IsCreated) _sourceMask.Dispose();

            _sourceCapacity = Mathf.Max(1, Mathf.NextPowerOfTwo(count));
            _sourcePos = new NativeArray<float3>(_sourceCapacity, Allocator.Persistent);
            _sourceRange = new NativeArray<float>(_sourceCapacity, Allocator.Persistent);
            _sourceMask = new NativeArray<int>(_sourceCapacity, Allocator.Persistent);
        }

        private void EnsureGridCapacity(int targetCount, int sourceCount)
        {
            int gridNeeded = Mathf.Max(1, targetCount);
            if (!_grid.IsCreated || _grid.Capacity < gridNeeded)
            {
                if (_grid.IsCreated) _grid.Dispose();
                _grid = new NativeParallelMultiHashMap<int, int>(Mathf.NextPowerOfTwo(gridNeeded), Allocator.Persistent);
            }

            int candNeeded = Mathf.Max(1, sourceCount * CONST_MaxCandidatesPerSource);
            if (!_candidates.IsCreated || _candidates.Capacity < candNeeded)
            {
                if (_candidates.IsCreated) _candidates.Dispose();
                _candidates = new NativeParallelMultiHashMap<int, int>(Mathf.NextPowerOfTwo(candNeeded), Allocator.Persistent);
            }
        }

        #endregion CAPACITY


        #region JOBS

        private static int3 CellOf(float3 p, float cellSize)
        {
            return (int3)math.floor(p / cellSize);
        }

        private static int HashCell(int3 c)
        {
            return (int)math.hash(c);
        }

        [BurstCompile]
        private struct BuildGridJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> Centers;
            public float CellSize;
            public NativeParallelMultiHashMap<int, int>.ParallelWriter Grid;

            public void Execute(int index)
            {
                Grid.Add(HashCell(CellOf(Centers[index], CellSize)), index);
            }
        }

        [BurstCompile]
        private struct QueryJob : IJobParallelFor
        {
            [ReadOnly] public NativeParallelMultiHashMap<int, int> Grid;
            [ReadOnly] public NativeArray<float3> Centers;
            [ReadOnly] public NativeArray<int> Layers;
            [ReadOnly] public NativeArray<float3> SourcePos;
            [ReadOnly] public NativeArray<float> SourceRange;
            [ReadOnly] public NativeArray<int> SourceMask;
            public float CellSize;
            public NativeParallelMultiHashMap<int, int>.ParallelWriter Candidates;

            public void Execute(int s)
            {
                float3 p = SourcePos[s];
                float r = SourceRange[s];
                int mask = SourceMask[s];
                float r2 = r * r;

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
                        // Hash collisions can bucket foreign cells together; accept a target only for its
                        // own cell (also guarantees each target is emitted at most once).
                        if (!math.all(CellOf(Centers[t], CellSize) == cell))
                            continue;
                        if ((mask & (1 << Layers[t])) == 0)
                            continue;
                        if (math.distancesq(p, Centers[t]) > r2)
                            continue;

                        Candidates.Add(s, t);
                    }
                    while (Grid.TryGetNextValue(out t, ref it));
                }
            }
        }

        #endregion JOBS
    }
}
