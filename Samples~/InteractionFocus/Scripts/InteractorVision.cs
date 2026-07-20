using UnityEngine;
using GalaxyGourd.Visioncast;

namespace VisioncastSamples.InteractionFocus
{
    /// <summary>
    /// A player/interactor's vision, built on <see cref="VisioncastSourceFiltered"/>. The base filter already
    /// computes <see cref="VisioncastSourceFiltered.TargetedObject"/> - the VISIBLE target whose closest sample
    /// point sits nearest the centre of the view - which is exactly the "what am I looking at right now?" signal
    /// an interaction system wants. This subclass just surfaces it as <see cref="Focused"/> and reports when it
    /// changes, so the sample can highlight the focused object and fire focus enter/exit.
    ///
    /// Note the SampleMode / SampleResolution overrides: VisioncastSourceFiltered reads those from a serialized
    /// _dataInteraction config, which a code-built source never assigns - leaving them would dereference null and
    /// abort every vision tick. A code-authored source must override them.
    /// </summary>
    public class InteractorVision : VisioncastSourceFiltered
    {
        private LayerMask _broad;
        private LayerMask _raycast;
        private float _range;
        private float _fov;

        public override LayerMask BroadphaseLayers => _broad;
        public override LayerMask RaycastLayers => _raycast;
        public override float Range => _range;
        public override float FieldOfView => _fov;
        public override VisionSampleMode SampleMode => VisionSampleMode.BoundsFaceGrid;
        public override int SampleResolution => 1;

        /// <summary>The object most directly in the centre of the view, or null when nothing visible is centred.</summary>
        public Collider Focused => TargetedObject;
        /// <summary>True on the tick <see cref="Focused"/> became a different object (for focus enter/exit).</summary>
        public bool FocusChanged { get; private set; }

        private Collider _lastFocused;

        public void Configure(LayerMask broad, LayerMask raycast, float range, float fov)
        {
            _broad = broad;
            _raycast = raycast;
            _range = range;
            _fov = fov;
        }

        protected override void PostVisionFilter()
        {
            FocusChanged = TargetedObject != _lastFocused;
            _lastFocused = TargetedObject;
        }
    }
}
