using System.Collections.Generic;
using UnityEngine;

namespace GalaxyGourd.Visioncast
{
    /// <summary>
    /// Provides debug visuals for a visioncast source
    /// </summary>
    [RequireComponent(typeof(VisioncastSource))]
    public class VisioncastSourceDebug : MonoBehaviour
    {
        #region VARIABLES

        [Header("References")]
        [SerializeField] private LineOfSightRay _prefabLineDebug;
        [SerializeField] private GameObject _prefabVisionCone;
        
        [Header("Draw Options")]
        [SerializeField] private bool _gizmos = true;
        [SerializeField] private bool _lineRenderers;
        [SerializeField] private bool _pauseDrawing;
        [SerializeField] private bool _drawCone;
        
        private VisionCone _visionCone;
        private VisioncastSource _source;
        private readonly List<LineOfSightRay> _debugLines = new();
        // Pool cursor: lines are consumed sequentially each tick and all reset in ClearLines, so we hand out
        // _debugLines[_lineCursor++] instead of scanning for a free one (which was O(n^2) in lines/tick).
        private int _lineCursor;
        
        #endregion VARIABLES


        #region INITIALIZATION

        private void Awake()
        {
            if (_drawCone)
                _visionCone = Instantiate(_prefabVisionCone, transform).GetComponent<VisionCone>();
            _source = GetComponent<VisioncastSource>();
            _source.AttachDebug(this);
        }

        private void OnDestroy()
        {
            _source.DetachDebug(this);
        }

        #endregion INITIALIZATION


        #region TICK

        internal void Tick(float delta)
        {
            if (_pauseDrawing)
                return;
            
            ClearLines();

            // Vision cone
            if (_visionCone)
            {
                _visionCone.CalculateCone(_source);
            }
            
            // LIne of sight rays
            DrawLineOfSightRays();
        }

        #endregion TICK


        #region LINE OF SIGHT

        /// <summary>
        /// Draws a red line to each resolved-but-occluded object and a green line to every visible sample
        /// point. Visibility comes from <see cref="DataVisioncastResult.VisiblePointCounts"/> (authoritative
        /// in both the managed and DoD pipelines); the individual point vectors are only present when this
        /// visualizer is attached, so fall back to one line at the object when they are unavailable.
        /// </summary>
        private void DrawLineOfSightRays()
        {
            DataVisioncastResult results = _source.LastResults;
            if (results.Objects == null || results.VisiblePointCounts == null)
                return;

            Vector3 sourcePosition = _source.Position;
            for (int i = 0; i < results.Objects.Count; i++)
            {
                Collider obj = results.Objects[i];
                if (obj == null)
                    continue;

                if (results.VisiblePointCounts[i] == 0)
                {
                    DrawLine(sourcePosition, obj.transform.position, Color.red);
                    continue;
                }

                List<Vector3> points = i < results.VisiblePoints.Count ? results.VisiblePoints[i] : null;
                if (points == null || points.Count == 0)
                {
                    // Visible, but the point vectors weren't captured this update (e.g. the visualizer was
                    // attached mid-tick) - still show the sighting.
                    DrawLine(sourcePosition, obj.transform.position, Color.green);
                    continue;
                }

                for (int e = 0; e < points.Count; e++)
                {
                    DrawLine(sourcePosition, points[e], Color.green);
                }
            }
        }
        
        private void DrawLine(Vector3 start, Vector3? end, Color color, float thickness = 1)
        {
            if (end is not { } v)
                return;
            
            if (_gizmos)
            {
                Debug.DrawLine(start, v, color);
            }

            if (_lineRenderers)
            {
                LineOfSightRay line = GetNextAvailableLine();
                if (line)
                {
                    line.SetParameters(start, v, color, thickness);
                    line.gameObject.SetActive(true);
                }
            }
        }

        #endregion LINE OF SIGHT


        #region UTILITY

        public void Toggle(bool on)
        {
            _gizmos = on;
            _lineRenderers = on;
        }
        
        private LineOfSightRay GetNextAvailableLine()
        {
            // Reuse the next pooled line, or grow the pool by one. O(1) - no scan for a free line.
            LineOfSightRay next;
            if (_lineCursor < _debugLines.Count)
            {
                next = _debugLines[_lineCursor];
            }
            else
            {
                if (!_prefabLineDebug)
                    return null;

                next = Instantiate(_prefabLineDebug.gameObject, transform).GetComponent<LineOfSightRay>();
                _debugLines.Add(next);
            }

            _lineCursor++;
            return next;
        }

        private void ClearLines()
        {
            // Only lines [0, _lineCursor) were handed out (and activated) last tick; deactivate just those
            // and rewind the cursor so this tick reuses them from the start.
            for (int i = 0; i < _lineCursor && i < _debugLines.Count; i++)
                _debugLines[i].gameObject.SetActive(false);

            _lineCursor = 0;
        }

        #endregion UTILITY
    }
}