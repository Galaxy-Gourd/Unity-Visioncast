using System;
using System.Collections.Generic;
using UnityEngine;

namespace GalaxyGourd.Visioncast.Tests
{
    /// <summary>
    /// Builds and tears down a code-authored vision scene. Everything it creates is tracked, so
    /// <see cref="Dispose"/> both unregisters targets from the (static) manifest and destroys the objects -
    /// leaving a destroyed collider in the manifest would break the next fixture's DoD gather.
    /// </summary>
    public sealed class VisionTestScene : IDisposable
    {
        #region LAYERS

        /// <summary>Default layer - used for vision targets.</summary>
        public const int LayerTarget = 0;
        /// <summary>TransparentFX - used for occluders, and for "not in the mask" negative cases.</summary>
        public const int LayerOccluder = 1;
        /// <summary>Water - a third layer for mask-filtering cases.</summary>
        public const int LayerOther = 4;

        public static LayerMask MaskOf(params int[] layers)
        {
            int mask = 0;
            foreach (int layer in layers)
                mask |= 1 << layer;
            return mask;
        }

        #endregion LAYERS


        #region VARIABLES

        private readonly List<GameObject> _objects = new();
        private readonly List<Collider> _registered = new();
        private readonly List<UnityEngine.Object> _assets = new();

        #endregion VARIABLES


        #region BUILD

        /// <summary>An empty tracked GameObject (destroyed on dispose, never saved into the open scene).</summary>
        public GameObject Empty(string name, Vector3 position = default, Quaternion rotation = default)
        {
            GameObject go = new GameObject(name)
            {
                hideFlags = HideFlags.DontSave
            };
            go.transform.SetPositionAndRotation(
                position,
                rotation == default ? Quaternion.identity : rotation);
            _objects.Add(go);
            return go;
        }

        /// <summary>A box collider, NOT registered as a vision target (use for occluders).</summary>
        public GameObject Box(string name, Vector3 position, Vector3 size, int layer = LayerTarget)
        {
            GameObject go = Empty(name, position);
            go.layer = layer;
            BoxCollider col = go.AddComponent<BoxCollider>();
            col.size = size;
            Physics.SyncTransforms();
            return go;
        }

        /// <summary>A box collider registered as a vision target, optionally owned by an actor.</summary>
        public BoxCollider Target(
            string name,
            Vector3 position,
            Vector3 size,
            int layer = LayerTarget,
            Component actor = null)
        {
            BoxCollider col = Box(name, position, size, layer).GetComponent<BoxCollider>();
            Register(col, actor);
            return col;
        }

        /// <summary>Registers an existing collider as a vision target and tracks it for cleanup.</summary>
        public void Register(Collider col, Component actor = null)
        {
            VisionTargetsManifest.Register(col, actor);
            if (!_registered.Contains(col))
                _registered.Add(col);
        }

        public void Unregister(Collider col)
        {
            VisionTargetsManifest.Unregister(col);
            _registered.Remove(col);
        }

        /// <summary>A configurable vision source at <paramref name="position"/> looking down +Z by default.</summary>
        public TestVisionSource Source(
            string name,
            Vector3 position,
            Quaternion rotation = default,
            float range = 20f,
            float fov = 60f)
        {
            GameObject go = Empty(name, position, rotation);
            TestVisionSource source = go.AddComponent<TestVisionSource>();
            source.VisionRange = range;
            source.Fov = fov;
            source.BroadphaseMask = MaskOf(LayerTarget);
            // The line-of-sight ray must be able to hit the TARGET's layer, not only occluders
            source.RaycastMask = MaskOf(LayerTarget, LayerOccluder);
            return source;
        }

        /// <summary>A filtered source backed by a runtime-created config asset.</summary>
        public TestFilteredVisionSource FilteredSource(
            string name,
            Vector3 position,
            Quaternion rotation = default,
            float range = 20f,
            float fov = 60f,
            VisionSampleMode mode = VisionSampleMode.BoundsFaceGrid,
            int resolution = 1)
        {
            GameObject go = Empty(name, position, rotation);
            TestFilteredVisionSource source = go.AddComponent<TestFilteredVisionSource>();
            _assets.Add(source.Configure(
                MaskOf(LayerTarget),
                MaskOf(LayerTarget, LayerOccluder),
                range,
                fov,
                mode,
                resolution));
            return source;
        }

        /// <summary>Tracks a runtime-created asset (ScriptableObject, Mesh, ...) for disposal.</summary>
        public T Track<T>(T asset) where T : UnityEngine.Object
        {
            _assets.Add(asset);
            return asset;
        }

        #endregion BUILD


        #region CLEANUP

        public void Dispose()
        {
            // Unregister BEFORE destroying: the manifest holds collider references, and a destroyed
            // collider left behind would fault the next gather/rebuild.
            for (int i = 0; i < _registered.Count; i++)
                VisionTargetsManifest.Unregister(_registered[i]);
            _registered.Clear();

            for (int i = 0; i < _objects.Count; i++)
                DestroyObject(_objects[i]);
            _objects.Clear();

            for (int i = 0; i < _assets.Count; i++)
                DestroyObject(_assets[i]);
            _assets.Clear();

            Physics.SyncTransforms();
        }

        /// <summary>Immediate destruction in both edit and play mode, so fixtures stay synchronous.</summary>
        public static void DestroyObject(UnityEngine.Object obj)
        {
            if (obj)
                UnityEngine.Object.DestroyImmediate(obj);
        }

        #endregion CLEANUP
    }
}
