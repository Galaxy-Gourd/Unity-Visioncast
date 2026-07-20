using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace GalaxyGourd.Visioncast
{
    /// <summary>
    /// Batched broadphase using OverlapSphereCommand (Unity 2022.2+): every source's sphere query runs
    /// across worker threads in one job, completed within Query so it never spans a Physics.Simulate.
    /// Opt in via <see cref="VisionBroadphase.UseBatchedOverlap"/>. Persistent, grow-on-demand native
    /// buffers keep it off the per-tick allocation path.
    ///
    /// VALIDATE IN-EDITOR: the exact OverlapSphereCommand / ColliderHit signature can vary slightly by
    /// Unity version. It mirrors the RaycastCommand batch already used by the raycaster.
    /// </summary>
    internal sealed class OverlapCommandBroadphase : IBroadphase
    {
        #region VARIABLES

        private const int CONST_MaxHitsPerSource = 64;
        private const int CONST_MinCommandsPerJob = 8;

        private NativeArray<OverlapSphereCommand> _commands;
        private NativeArray<ColliderHit> _results;
        private int _capacity; // in commands

        #endregion VARIABLES


        #region QUERY

        public void Query(List<VisioncastSource> sources, int count, List<List<Collider>> candidates)
        {
            if (count == 0)
                return;

            EnsureCapacity(count);

            for (int i = 0; i < count; i++)
            {
                VisioncastSource source = sources[i];
                _commands[i] = new OverlapSphereCommand(
                    source.Position,
                    source.Range,
                    new QueryParameters(layerMask: source.BroadphaseLayers));
            }

            // Schedule only the active range; the persistent buffers may be larger than count
            NativeArray<OverlapSphereCommand> commands = _commands.GetSubArray(0, count);
            NativeArray<ColliderHit> results = _results.GetSubArray(0, count * CONST_MaxHitsPerSource);
            JobHandle handle = OverlapSphereCommand.ScheduleBatch(
                commands, results, CONST_MinCommandsPerJob, CONST_MaxHitsPerSource);
            JobHandle.ScheduleBatchedJobs();
            handle.Complete();

            // Results are laid out [command * maxHits + hit]; an empty (null) slot ends a command's hits
            for (int i = 0; i < count; i++)
            {
                List<Collider> list = candidates[i];
                int baseIndex = i * CONST_MaxHitsPerSource;
                for (int h = 0; h < CONST_MaxHitsPerSource; h++)
                {
                    Collider col = results[baseIndex + h].collider;
                    if (col == null)
                        break;

                    list.Add(col);
                }
            }
        }

        #endregion QUERY


        #region CAPACITY

        private void EnsureCapacity(int count)
        {
            if (_commands.IsCreated && _capacity >= count)
                return;

            if (_commands.IsCreated)
                _commands.Dispose();
            if (_results.IsCreated)
                _results.Dispose();

            _capacity = Mathf.Max(1, Mathf.NextPowerOfTwo(count));
            _commands = new NativeArray<OverlapSphereCommand>(_capacity, Allocator.Persistent);
            _results = new NativeArray<ColliderHit>(_capacity * CONST_MaxHitsPerSource, Allocator.Persistent);
        }

        public void Dispose()
        {
            if (_commands.IsCreated)
                _commands.Dispose();
            if (_results.IsCreated)
                _results.Dispose();

            _capacity = 0;
        }

        #endregion CAPACITY
    }
}
