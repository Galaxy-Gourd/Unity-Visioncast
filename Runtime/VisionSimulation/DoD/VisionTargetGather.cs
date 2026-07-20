using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;
// Object identity type: Unity 6.5 replaces the 32-bit InstanceID (int) with the 64-bit EntityId struct
// (GetInstanceID / RaycastHit.colliderInstanceID are removed). Alias keeps the buffers version-agnostic.
#if UNITY_6000_5_OR_NEWER
using ColliderId = UnityEngine.EntityId;
#else
using ColliderId = System.Int32;
#endif

namespace GalaxyGourd.Visioncast
{
    /// <summary>
    /// DoD gather (step 1): reads every registered target's world-space center off the main thread via a
    /// Burst <see cref="IJobParallelForTransform"/> over a <see cref="TransformAccessArray"/>, indexed by
    /// the target's dense id (<see cref="VisionTargetsManifest"/>). The center is captured as a local-space
    /// offset once per registration change (rebuild) and re-projected each tick through the transform, which
    /// is EXACT for any rigidly-attached collider - no OBB approximation here (that arrives with extents).
    ///
    /// Not yet consumed by the vision pipeline; the Burst spatial-grid broadphase (step 2) reads
    /// <see cref="WorldCenters"/>. Owned and disposed by whoever creates it (native containers are Persistent).
    /// </summary>
    internal sealed class VisionTargetGather : IDisposable
    {
        #region VARIABLES

        private TransformAccessArray _transforms;
        // Per target (indexed by id): local-space collider-center offset + local half-extents (inputs),
        // and the gathered world center + full world OBB (outputs).
        private NativeList<float3> _localCenter;
        private NativeList<float3> _localExtents;
        private NativeList<float3> _worldCenter;
        private NativeList<TargetObb> _worldObb;
        // Per target static attributes captured on rebuild: collider layer (broadphase mask filter) and
        // collider instance id (the narrowphase reduce tests ray hits by identity, off the main thread).
        private NativeList<int> _layer;
        private NativeList<ColliderId> _instanceId;

        private int _syncedVersion = int.MinValue;
        private JobHandle _handle;
        private bool _scheduled;

        /// <summary>Gathered world-space centers, valid after <see cref="Complete"/>; indexed by target id.</summary>
        internal NativeArray<float3> WorldCenters => _worldCenter.AsArray();
        /// <summary>Gathered world OBBs (center + axes + extents), valid after <see cref="Complete"/>; by id.</summary>
        internal NativeArray<TargetObb> WorldObbs => _worldObb.AsArray();
        /// <summary>Per-target collider layer, indexed by target id (captured on rebuild).</summary>
        internal NativeArray<int> Layers => _layer.AsArray();
        /// <summary>Per-target collider identity (EntityId on 6.5+, InstanceID int otherwise), by target id;
        /// compared against RaycastHit collider identity in the reduce job (job-safe, no managed access).</summary>
        internal NativeArray<ColliderId> InstanceIds => _instanceId.AsArray();
        internal int Count => _localCenter.IsCreated ? _localCenter.Length : 0;

        #endregion VARIABLES


        #region LIFECYCLE

        internal VisionTargetGather(int initialCapacity = 64)
        {
            int cap = Mathf.Max(1, initialCapacity);
            _transforms = new TransformAccessArray(cap);
            _localCenter = new NativeList<float3>(cap, Allocator.Persistent);
            _localExtents = new NativeList<float3>(cap, Allocator.Persistent);
            _worldCenter = new NativeList<float3>(cap, Allocator.Persistent);
            _worldObb = new NativeList<TargetObb>(cap, Allocator.Persistent);
            _layer = new NativeList<int>(cap, Allocator.Persistent);
            _instanceId = new NativeList<ColliderId>(cap, Allocator.Persistent);
        }

        public void Dispose()
        {
            if (_scheduled)
            {
                _handle.Complete();
                _scheduled = false;
            }

            if (_transforms.isCreated) _transforms.Dispose();
            if (_localCenter.IsCreated) _localCenter.Dispose();
            if (_localExtents.IsCreated) _localExtents.Dispose();
            if (_worldCenter.IsCreated) _worldCenter.Dispose();
            if (_worldObb.IsCreated) _worldObb.Dispose();
            if (_layer.IsCreated) _layer.Dispose();
            if (_instanceId.IsCreated) _instanceId.Dispose();
        }

        #endregion LIFECYCLE


        #region GATHER

        /// <summary>Rebuilds the transform array + local-center buffer from the manifest if it changed.</summary>
        internal void SyncIfDirty()
        {
            int version = VisionTargetsManifest.Version;
            if (version == _syncedVersion)
                return;

            Rebuild();
            _syncedVersion = version;
        }

        private void Rebuild()
        {
            IReadOnlyList<Collider> targets = VisionTargetsManifest.TargetsById;
            int count = targets.Count;

            var transforms = new Transform[count];
            _localCenter.ResizeUninitialized(count);
            _localExtents.ResizeUninitialized(count);
            _worldCenter.ResizeUninitialized(count);
            _worldObb.ResizeUninitialized(count);
            _layer.ResizeUninitialized(count);
            _instanceId.ResizeUninitialized(count);

            for (int i = 0; i < count; i++)
            {
                Collider col = targets[i];
                transforms[i] = col.transform;
                LocalBounds(col, out float3 center, out float3 extents);
                _localCenter[i] = center;
                _localExtents[i] = extents;
                _layer[i] = col.gameObject.layer;
#if UNITY_6000_5_OR_NEWER
                _instanceId[i] = col.GetEntityId();
#else
                _instanceId[i] = col.GetInstanceID();
#endif
            }

            _transforms.SetTransforms(transforms);
        }

        /// <summary>
        /// The collider's bounds in its OWN local space: centre offset and pre-scale half-extents. The gather
        /// job projects these through the transform (centre by the matrix, extents by each column's length),
        /// producing a true oriented box that tracks rotation.
        ///
        /// Read from each collider's own definition rather than <c>Collider.bounds</c> - bounds is the WORLD
        /// axis-aligned box, which for a rotated collider is inflated well beyond the shape; treating that as
        /// local extents produces an OBB that is both too large and misaligned (sample points land in empty
        /// space, and the closest point corrupts the angle filter).
        /// </summary>
        private static void LocalBounds(Collider col, out float3 center, out float3 extents)
        {
            switch (col)
            {
                case BoxCollider box:
                    center = box.center;
                    extents = (float3)box.size * 0.5f;
                    return;

                case SphereCollider sphere:
                    center = sphere.center;
                    extents = new float3(sphere.radius);
                    return;

                case CapsuleCollider capsule:
                {
                    center = capsule.center;
                    float r = capsule.radius;
                    float half = Mathf.Max(capsule.height * 0.5f, r); // height includes the caps
                    extents = capsule.direction switch
                    {
                        0 => new float3(half, r, r), // X
                        1 => new float3(r, half, r), // Y
                        _ => new float3(r, r, half)  // Z
                    };
                    return;
                }

                case MeshCollider mesh when mesh.sharedMesh != null:
                    center = mesh.sharedMesh.bounds.center; // mesh bounds ARE local
                    extents = mesh.sharedMesh.bounds.extents;
                    return;

                default:
                {
                    // Unknown/!sharedMesh (e.g. terrain): fall back to de-scaling the world bounds. Correct
                    // while unrotated; an over-estimate otherwise.
                    Vector3 s = col.transform.lossyScale;
                    Vector3 e = col.bounds.extents;
                    center = col.transform.InverseTransformPoint(col.bounds.center);
                    extents = new float3(
                        e.x / Mathf.Max(Mathf.Abs(s.x), 1e-6f),
                        e.y / Mathf.Max(Mathf.Abs(s.y), 1e-6f),
                        e.z / Mathf.Max(Mathf.Abs(s.z), 1e-6f));
                    return;
                }
            }
        }

        /// <summary>Schedules the parallel transform gather (does not wait). Pair with <see cref="Complete"/>.</summary>
        internal JobHandle Schedule(JobHandle dependsOn = default)
        {
            var job = new GatherObbJob
            {
                LocalCenter = _localCenter.AsArray(),
                LocalExtents = _localExtents.AsArray(),
                WorldCenter = _worldCenter.AsArray(),
                WorldObb = _worldObb.AsArray()
            };
            _handle = job.ScheduleReadOnly(_transforms, 32, dependsOn);
            _scheduled = true;
            return _handle;
        }

        internal void Complete()
        {
            if (!_scheduled)
                return;

            _handle.Complete();
            _scheduled = false;
        }

        /// <summary>Convenience: sync + schedule + complete in one call (used for standalone validation).</summary>
        internal void Gather()
        {
            SyncIfDirty();
            Schedule().Complete();
            _scheduled = false;
        }

        #endregion GATHER


        #region JOB

        [BurstCompile]
        private struct GatherObbJob : IJobParallelForTransform
        {
            [ReadOnly] public NativeArray<float3> LocalCenter;
            [ReadOnly] public NativeArray<float3> LocalExtents;
            [WriteOnly] public NativeArray<float3> WorldCenter;
            [WriteOnly] public NativeArray<TargetObb> WorldObb;

            public void Execute(int index, TransformAccess transform)
            {
                float4x4 m = transform.localToWorldMatrix;
                float3 center = math.transform(m, LocalCenter[index]);
                WorldCenter[index] = center;

                // Transform columns carry axis direction * scale; split into unit axis + per-axis world extent.
                float3 c0 = m.c0.xyz, c1 = m.c1.xyz, c2 = m.c2.xyz;
                float lx = math.length(c0), ly = math.length(c1), lz = math.length(c2);
                float3 e = LocalExtents[index];

                WorldObb[index] = new TargetObb
                {
                    Center = center,
                    AxisX = c0 / math.max(lx, 1e-6f),
                    AxisY = c1 / math.max(ly, 1e-6f),
                    AxisZ = c2 / math.max(lz, 1e-6f),
                    Extents = new float3(e.x * lx, e.y * ly, e.z * lz)
                };
            }
        }

        #endregion JOB
    }
}
