using System.Collections.Generic;
using UnityEngine;

namespace GalaxyGourd.Visioncast.Tests
{
    /// <summary>
    /// Minimal concrete <see cref="VisioncastSource"/> with public, runtime-settable config - the shipped
    /// sources keep theirs in serialized/private fields, which a code-built test scene cannot author.
    /// Records the delivery callbacks so tests can assert cadence and result content.
    /// </summary>
    public class TestVisionSource : VisioncastSource
    {
        public LayerMask BroadphaseMask = ~0;
        public LayerMask RaycastMask = ~0;
        public float VisionRange = 20f;
        /// <summary>Cone HALF-angle in degrees (matches <see cref="VisioncastSource.FieldOfView"/>).</summary>
        public float Fov = 60f;
        public VisionSampleMode Mode = VisionSampleMode.BoundsFaceGrid;
        public int Resolution = 1;

        public override LayerMask BroadphaseLayers => BroadphaseMask;
        public override LayerMask RaycastLayers => RaycastMask;
        public override float Range => VisionRange;
        public override float FieldOfView => Fov;
        public override VisionSampleMode SampleMode => Mode;
        public override int SampleResolution => Resolution;

        /// <summary>Number of result deliveries received (i.e. ticks on which this source actually cast).</summary>
        public int ReceiveCount { get; private set; }
        public int BeforeVisioncastCount { get; private set; }
        /// <summary>Colliders reported by the most recent delivery, in result order.</summary>
        public readonly List<Collider> LastObjects = new();
        /// <summary>Visible sample-point count per <see cref="LastObjects"/> entry.</summary>
        public readonly List<int> LastVisibleCounts = new();
        /// <summary>Total sample points tested per <see cref="LastObjects"/> entry.</summary>
        public readonly List<int> LastSampleCounts = new();

        public override void OnBeforeVisioncast()
        {
            BeforeVisioncastCount++;
        }

        protected override void OnReceiveResults(DataVisioncastResult data)
        {
            ReceiveCount++;
            LastObjects.Clear();
            LastVisibleCounts.Clear();
            LastSampleCounts.Clear();
            if (data.Objects == null)
                return;

            for (int i = 0; i < data.Objects.Count; i++)
            {
                LastObjects.Add(data.Objects[i]);
                LastVisibleCounts.Add(data.VisiblePointCounts[i]);
                LastSampleCounts.Add(data.SampleCounts[i]);
            }
        }

        /// <summary>Index of <paramref name="col"/> in the last delivery, or -1.</summary>
        public int IndexOf(Collider col)
        {
            return LastObjects.IndexOf(col);
        }

        /// <summary>Visible sample points reported for <paramref name="col"/> last delivery (0 if absent).</summary>
        public int VisibleCountFor(Collider col)
        {
            int i = IndexOf(col);
            return i >= 0 ? LastVisibleCounts[i] : 0;
        }

        /// <summary>Sample points tested against <paramref name="col"/> last delivery (0 if absent).</summary>
        public int SampleCountFor(Collider col)
        {
            int i = IndexOf(col);
            return i >= 0 ? LastSampleCounts[i] : 0;
        }

        public bool Sees(Collider col)
        {
            return VisibleCountFor(col) > 0;
        }
    }

    /// <summary>
    /// <see cref="VisioncastSourceFiltered"/> wired to a runtime-created config asset, so the real
    /// config-driven property path is exercised rather than overridden away.
    /// </summary>
    public class TestFilteredVisionSource : VisioncastSourceFiltered
    {
        public int PostFilterCount { get; private set; }

        /// <summary>Creates and installs the source config. Call before the source is first ticked.</summary>
        public DataConfigVisioncastSource Configure(
            LayerMask broadphase,
            LayerMask raycast,
            float range,
            float fov,
            VisionSampleMode mode = VisionSampleMode.BoundsFaceGrid,
            int resolution = 1)
        {
            DataConfigVisioncastSource config = ScriptableObject.CreateInstance<DataConfigVisioncastSource>();
            config.hideFlags = HideFlags.DontSave;
            config.BroadphaseLayermask = broadphase;
            config.RaycastLayermask = raycast;
            config.VisionRange = range;
            config.FieldOfView = fov;
            config.SampleMode = mode;
            config.SampleResolution = resolution;

            _dataInteraction = config;
            return config;
        }

        protected override void PostVisionFilter()
        {
            PostFilterCount++;
        }

        /// <summary>
        /// Drives the filter step directly, so the defensive "no result buffer at all" path
        /// (<see cref="DataVisioncastResult.Objects"/> == null) can be exercised - the injector always
        /// hands over initialized lists.
        /// </summary>
        public void FilterDirect(DataVisioncastResult data)
        {
            FilterVisionTargets(data);
        }

        /// <summary>The resolved entry for a target identity, if this source currently resolves it.</summary>
        public bool TryGetTarget(Component actor, out DataVisionSeenObject target)
        {
            for (int i = 0; i < VisionTargets.Count; i++)
            {
                if (VisionTargets[i].Actor == actor)
                {
                    target = VisionTargets[i];
                    return true;
                }
            }

            target = default;
            return false;
        }
    }

    /// <summary>Counts <see cref="IVisibleObject.Seen"/> notifications for the simple-source tests.</summary>
    public class TestVisibleObject : MonoBehaviour, IVisibleObject
    {
        public int SeenCount { get; private set; }
        public VisioncastSource LastSeenBy { get; private set; }

        public void Seen(VisioncastSource source)
        {
            SeenCount++;
            LastSeenBy = source;
        }

        public void ResetCounters()
        {
            SeenCount = 0;
            LastSeenBy = null;
        }
    }
}
