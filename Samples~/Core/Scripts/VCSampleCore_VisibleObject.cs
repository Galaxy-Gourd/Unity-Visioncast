using System;
using GalaxyGourd.Visioncast;
using UnityEngine;

namespace VisioncastSamples.Core
{
    [RequireComponent(typeof(Collider))]
    public class VCSampleCore_VisibleObject : MonoBehaviour, IVisibleObject
    {
        #region REFERENCES

        [Header("Debug")]
        [SerializeField] private Color _colorSeen;
        [SerializeField] private float _colorSeenIncrement;
        [SerializeField] private float _colorFadeRate;
        
        public Transform Transform => transform;

        private Renderer _debugRenderer;
        private Color _defaultColor;
        private float _normColorValue;
        private Color _targetColor;
        private Collider _collider;
        
        #endregion REFERENCES


        #region INITIALIZATION

        private void Awake()
        {
            _collider = GetComponent<Collider>();
            _debugRenderer = GetComponent<Renderer>();
            _defaultColor = _debugRenderer.material.color;
        }

        private void OnEnable()
        {
            VisionTargetsManifest.Register(_collider);
        }

        private void OnDisable()
        {
            VisionTargetsManifest.Unregister(_collider);
        }

        #endregion INITIALIZATION
    
    
        #region VISION

        void IVisibleObject.Seen(VisioncastSource source)
        {
            _normColorValue += _colorSeenIncrement;
        }

        #endregion VISION


        #region TICK

        private void Update()
        {
            _targetColor = Color.Lerp(_defaultColor, _colorSeen, _normColorValue);
            _debugRenderer.material.color = _targetColor;
            
            _normColorValue -= Time.deltaTime * _colorFadeRate;
            _normColorValue = Mathf.Clamp01(_normColorValue);
        }

        #endregion TICK
    }
}