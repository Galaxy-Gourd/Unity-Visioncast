using UnityEngine;
using GalaxyGourd.Visioncast;

namespace VisioncastSamples.StealthExposure
{
    /// <summary>
    /// A filtered vision source that stands in for a light: its cone (<see cref="Range"/> /
    /// <see cref="FieldOfView"/> around <see cref="VisioncastSource.Heading"/>) is what "sees" the player,
    /// and the per-target <see cref="DataVisionSeenObject.Visibility"/> it produces is how lit the player is
    /// by this one light. Configured from code so the sample can build several at runtime, with a mutable
    /// sampling density so the Stealth Exposure sample can show binary vs smooth exposure.
    /// </summary>
    public class LightVision : VisioncastSourceFiltered
    {
        private LayerMask _targets;
        private LayerMask _occluders;
        private float _range;
        private float _fov;
        private VisionSampleMode _mode = VisionSampleMode.BoundsVolumeGrid;
        private int _res = 3;

        public override LayerMask BroadphaseLayers => _targets;
        public override LayerMask RaycastLayers => _occluders;
        public override float Range => _range;
        public override float FieldOfView => _fov;
        public override VisionSampleMode SampleMode => _mode;
        public override int SampleResolution => _res;

        public void Configure(LayerMask targets, LayerMask occluders, float range, float fov)
        {
            _targets = targets;
            _occluders = occluders;
            _range = range;
            _fov = fov;
        }

        public void SetSampling(VisionSampleMode mode, int res)
        {
            _mode = mode;
            _res = res;
        }
    }
}
