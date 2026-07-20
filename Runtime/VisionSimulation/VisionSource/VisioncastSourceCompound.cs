using System.Collections.Generic;
using UnityEngine;

namespace GalaxyGourd.Visioncast
{
    /// <summary>
    /// Combines the refined vision of several child sources into a single de-duplicated output. Each
    /// child <see cref="VisioncastSourceFiltered"/> casts independently and already resolves its results to
    /// one entry per target identity (<see cref="VisionTargetsManifest.ResolveTarget"/> - the owning actor,
    /// or the collider itself when standalone); the compound merges those across children so a target seen
    /// by ANY child is reported once, with both its <see cref="DataVisionSeenTarget.Visibility"/> (occlusion)
    /// and <see cref="DataVisionSeenTarget.Coverage"/> (in-beam illumination) aggregated across children per
    /// <see cref="VisibilityAggregation"/>.
    ///
    /// Typical use is stealth exposure from multiple lights: parent several light-mounted sources under one
    /// compound and query <see cref="TryGetCoverage"/> for the combined "how lit across all lights" value
    /// (or <see cref="TryGetVisibility"/> for cone-agnostic occlusion). The compound does NOT cast itself -
    /// it only aggregates its children.
    /// </summary>
    public class VisioncastSourceCompound : MonoBehaviour
    {
        #region VARIABLES

        [Header("Sources")]
        [SerializeField] private List<VisioncastSourceFiltered> _sources = new();

        [Header("Aggregation")]
        [Tooltip("How a target's visibility is combined across the child sources that resolve it.")]
        [SerializeField] private VisibilityAggregation _aggregation = VisibilityAggregation.Max;
        [Tooltip("Combine automatically each LateUpdate. Disable to drive Combine() from your own tick.")]
        [SerializeField] private bool _autoCombine = true;

        /// <summary>
        /// How a target's visibility is combined across the child sources that resolve it
        /// (see <see cref="VisibilityAggregation"/>). Takes effect on the next <see cref="Combine"/>.
        /// </summary>
        public VisibilityAggregation Aggregation
        {
            get => _aggregation;
            set => _aggregation = value;
        }

        /// <summary>
        /// When true, <see cref="Combine"/> runs automatically each LateUpdate. Set false to drive it
        /// yourself (e.g. right after the vision tick, for a deterministic same-frame read).
        /// </summary>
        public bool AutoCombine
        {
            get => _autoCombine;
            set => _autoCombine = value;
        }

        /// <summary>Combined, de-duplicated vision targets across all active child sources.</summary>
        public IReadOnlyList<DataVisionSeenTarget> VisionTargets => _combined;
        /// <summary>Representative colliders of targets that became visible since the previous combine.</summary>
        public IReadOnlyList<Collider> NewlySeenObjects => _newlySeen;
        /// <summary>Representative colliders of targets that stopped being visible since the previous combine.</summary>
        public IReadOnlyList<Collider> NewlyLostObjects => _newlyLost;

        private readonly List<DataVisionSeenTarget> _combined = new();
        private readonly List<DataVisionSeenTarget> _previous = new();
        private readonly HashSet<Component> _previousVisible = new();
        // aggregation key (actor when grouped, else the collider) -> index into _combined
        private readonly Dictionary<Component, int> _index = new();
        // Per-combined-target aggregation scratch (aligned with _combined)
        private readonly List<float> _maxVis = new();
        private readonly List<float> _sumVis = new();
        private readonly List<float> _maxCov = new();
        private readonly List<float> _sumCov = new();
        private readonly List<int> _visibleCount = new();
        private readonly List<Collider> _newlySeen = new();
        private readonly List<Collider> _newlyLost = new();

        #endregion VARIABLES


        #region SOURCES

        public void AddSource(VisioncastSourceFiltered source)
        {
            if (source && !_sources.Contains(source))
                _sources.Add(source);
        }

        public void RemoveSource(VisioncastSourceFiltered source)
        {
            _sources.Remove(source);
        }

        #endregion SOURCES


        #region TICK

        private void LateUpdate()
        {
            if (_autoCombine)
                Combine();
        }

        #endregion TICK


        #region COMBINE

        /// <summary>
        /// Merges the current results of all active child sources into the combined output and
        /// recomputes the newly-seen / newly-lost sets relative to the previous combine.
        /// </summary>
        public void Combine()
        {
            // Snapshot the previous result for transition diffing
            _previous.Clear();
            _previous.AddRange(_combined);
            _previousVisible.Clear();
            for (int i = 0; i < _previous.Count; i++)
            {
                if (_previous[i].IsVisible)
                    _previousVisible.Add(KeyOf(_previous[i]));
            }

            // Reset the working buffers
            _combined.Clear();
            _index.Clear();
            _maxVis.Clear();
            _sumVis.Clear();
            _maxCov.Clear();
            _sumCov.Clear();
            _visibleCount.Clear();

            // Fold every active child's targets into the combined set
            for (int s = 0; s < _sources.Count; s++)
            {
                VisioncastSourceFiltered source = _sources[s];

                // A disabled source is no longer casting; its data is stale, so skip it
                if (!source || !source.isActiveAndEnabled)
                    continue;

                float updateTime = source.LastUpdatedTime;
                IReadOnlyList<DataVisionSeenObject> targets = source.VisionTargets;
                for (int t = 0; t < targets.Count; t++)
                {
                    MergeTarget(targets[t], updateTime);
                }
            }

            // Resolve aggregated visibility now that all contributions are folded
            for (int i = 0; i < _combined.Count; i++)
            {
                DataVisionSeenTarget entry = _combined[i];
                entry.Visibility = Aggregate(_maxVis[i], _sumVis[i], _visibleCount[i]);
                entry.Coverage = Aggregate(_maxCov[i], _sumCov[i], _visibleCount[i]);
                _combined[i] = entry;
            }

            ResolveTransitions();
        }

        private void MergeTarget(DataVisionSeenObject incoming, float updateTime)
        {
            // Children already resolved each observation to its single target identity, so merging across
            // children is just keying on that identity - no actor-vs-collider branch here.
            Component key = incoming.Actor;
            Collider col = incoming.ResultObject;
            if (!key)
                return;

            float cov = incoming.SampleCount > 0 ? incoming.LitCount / (float)incoming.SampleCount : 0f;

            if (_index.TryGetValue(key, out int i))
            {
                DataVisionSeenTarget entry = _combined[i];
                entry.IsVisible |= incoming.IsVisible;
                entry.Distance = Mathf.Min(entry.Distance, incoming.Distance);
                entry.Angle = Mathf.Min(entry.Angle, incoming.Angle);
                entry.VisiblePointCount += incoming.VisiblePointCount;
                entry.SampleCount += incoming.SampleCount;
                entry.InConeCount += incoming.InConeCount;
                entry.LitCount += incoming.LitCount;
                entry.LastUpdatedTime = Mathf.Max(entry.LastUpdatedTime, updateTime);

                // The representative collider is the most-visible contribution
                if (incoming.Visibility > _maxVis[i])
                    entry.Collider = col;

                _combined[i] = entry;

                _maxVis[i] = Mathf.Max(_maxVis[i], incoming.Visibility);
                _sumVis[i] += incoming.Visibility;
                _maxCov[i] = Mathf.Max(_maxCov[i], cov);
                _sumCov[i] += cov;
                if (incoming.IsVisible)
                    _visibleCount[i]++;
            }
            else
            {
                _index[key] = _combined.Count;
                _combined.Add(new DataVisionSeenTarget
                {
                    Actor = key,
                    Collider = col,
                    IsVisible = incoming.IsVisible,
                    JustBecameVisible = false, // resolved in ResolveTransitions
                    Distance = incoming.Distance,
                    Angle = incoming.Angle,
                    VisiblePointCount = incoming.VisiblePointCount,
                    SampleCount = incoming.SampleCount,
                    InConeCount = incoming.InConeCount,
                    LitCount = incoming.LitCount,
                    Visibility = 0f, // resolved in the finalize pass
                    LastUpdatedTime = updateTime
                });
                _maxVis.Add(incoming.Visibility);
                _sumVis.Add(incoming.Visibility);
                _maxCov.Add(cov);
                _sumCov.Add(cov);
                _visibleCount.Add(incoming.IsVisible ? 1 : 0);
            }
        }

        private float Aggregate(float max, float sum, int count)
        {
            switch (_aggregation)
            {
                case VisibilityAggregation.Sum:
                    return Mathf.Clamp01(sum);
                case VisibilityAggregation.Average:
                    return count > 0 ? sum / count : 0f;
                default: // Max
                    return max;
            }
        }

        private void ResolveTransitions()
        {
            _newlySeen.Clear();
            _newlyLost.Clear();

            // Newly seen: visible now, but not visible in the previous combine
            for (int i = 0; i < _combined.Count; i++)
            {
                DataVisionSeenTarget entry = _combined[i];
                if (entry.IsVisible && !_previousVisible.Contains(KeyOf(entry)))
                {
                    entry.JustBecameVisible = true;
                    _combined[i] = entry;
                    _newlySeen.Add(entry.Collider);
                }
            }

            // Newly lost: visible previously, but not visible now
            for (int i = 0; i < _previous.Count; i++)
            {
                DataVisionSeenTarget prev = _previous[i];
                if (prev.IsVisible && !IsVisibleNow(KeyOf(prev)))
                    _newlyLost.Add(prev.Collider);
            }
        }

        #endregion COMBINE


        #region QUERY

        /// <summary>
        /// Combined visibility in [0, 1] of the target owning <paramref name="col"/>, or 0 if no
        /// active child resolves it.
        /// </summary>
        public bool TryGetVisibility(Collider col, out float visibility)
        {
            if (col != null)
                return TryGetVisibility(VisionTargetsManifest.ResolveTarget(col), out visibility);

            visibility = 0f;
            return false;
        }

        /// <summary>
        /// Combined visibility in [0, 1] of an actor grouped in the combined output, or 0 if no active
        /// child resolves any of its colliders.
        /// </summary>
        public bool TryGetVisibility(Component actor, out float visibility)
        {
            if (actor && _index.TryGetValue(actor, out int i))
            {
                visibility = _combined[i].Visibility;
                return true;
            }

            visibility = 0f;
            return false;
        }

        /// <summary>
        /// Combined COVERAGE in [0, 1] (lit / sample - in-cone and unobstructed) of the target owning
        /// <paramref name="col"/>, aggregated across all child lights per <see cref="Aggregation"/>. This is
        /// the multi-light "how lit" signal for stealth; 0 if no active child resolves the target.
        /// </summary>
        public bool TryGetCoverage(Collider col, out float coverage)
        {
            if (col != null)
                return TryGetCoverage(VisionTargetsManifest.ResolveTarget(col), out coverage);

            coverage = 0f;
            return false;
        }

        /// <summary>Combined coverage in [0, 1] of an actor grouped in the combined output (see the collider overload).</summary>
        public bool TryGetCoverage(Component actor, out float coverage)
        {
            if (actor && _index.TryGetValue(actor, out int i))
            {
                coverage = _combined[i].Coverage;
                return true;
            }

            coverage = 0f;
            return false;
        }

        /// <summary>True if any active child source currently sees the target owning the collider.</summary>
        public bool IsVisible(Collider col)
        {
            return col != null && IsVisibleNow(VisionTargetsManifest.ResolveTarget(col));
        }

        /// <summary>True if any active child source currently sees the actor.</summary>
        public bool IsVisible(Component actor)
        {
            return actor && IsVisibleNow(actor);
        }

        #endregion QUERY


        #region UTILITY

        private static Component KeyOf(DataVisionSeenTarget target)
        {
            return target.Actor;
        }

        private bool IsVisibleNow(Component key)
        {
            return key && _index.TryGetValue(key, out int i) && _combined[i].IsVisible;
        }

        #endregion UTILITY
    }
}
