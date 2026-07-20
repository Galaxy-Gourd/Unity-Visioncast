using UnityEngine;
using GalaxyGourd.Visioncast;

namespace VisioncastSamples.CrowdLOD
{
    /// <summary>
    /// A filtered vision source configured entirely from code, so the Crowd LOD bootstrap can spawn and
    /// set up hundreds of them at runtime without a <see cref="DataConfigVisioncastSource"/> asset each.
    /// It re-overrides the config properties the base normally reads from that asset.
    /// </summary>
    public class CrowdAgentVision : VisioncastSourceFiltered
    {
        private LayerMask _broadphase;
        private LayerMask _raycast;
        private float _range;
        private float _fov;
        private int _sampleRes = 1;

        public override LayerMask BroadphaseLayers => _broadphase;
        public override LayerMask RaycastLayers => _raycast;
        public override float Range => _range;
        public override float FieldOfView => _fov;
        public override VisionSampleMode SampleMode => VisionSampleMode.BoundsFaceGrid;
        public override int SampleResolution => _sampleRes;

        /// <summary>Degrees/second the agent spins, so its visible set churns tick to tick (no free caching).</summary>
        public float SpinSpeed;
        /// <summary>Cached renderer, tinted by how recently this source last cast (see the bootstrap).</summary>
        public Renderer Renderer;

        public void Configure(LayerMask broadphase, LayerMask raycast, float range, float fov, int sampleRes)
        {
            _broadphase = broadphase;
            _raycast = raycast;
            _range = range;
            _fov = fov;
            _sampleRes = sampleRes;
        }
    }
}
