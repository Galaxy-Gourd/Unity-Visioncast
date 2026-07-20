using System.Collections.Generic;
using UnityEngine;

namespace GalaxyGourd.Visioncast
{
    /// <summary>
    /// Receives raw (per-collider) visioncast results and resolves them into refined per-TARGET data.
    /// Colliders are collapsed by their identity (<see cref="VisionTargetsManifest.ResolveTarget"/>), so a
    /// multi-collider actor yields ONE <see cref="DataVisionSeenObject"/> with its observations merged, and
    /// a standalone collider yields one entry keyed by itself.
    /// </summary>
    public static class VisioncastResultsFilter
    {
        #region RESOLVE

        /// <summary>
        /// Resolves raw results into <paramref name="output"/> (cleared and refilled - no allocation), one
        /// entry per target identity. <paramref name="previousIndex"/> and <paramref name="actorIndex"/> are
        /// caller-owned reusable maps (rebuilt here) keyed by target identity: the former indexes
        /// <paramref name="previous"/> for O(1) "was this visible last update", the latter groups this
        /// update's colliders onto their target.
        /// </summary>
        public static void Resolve(
            DataVisioncastResult results,
            List<DataVisionSeenObject> previous,
            Dictionary<Component, int> previousIndex,
            Dictionary<Component, int> actorIndex,
            List<DataVisionSeenObject> output)
        {
            output.Clear();
            actorIndex.Clear();

            // Index the previous update's targets by identity (avoids per-target linear scans)
            previousIndex.Clear();
            for (int i = 0; i < previous.Count; i++)
            {
                Component actor = previous[i].Actor;
                if (actor)
                    previousIndex[actor] = i;
            }

            // Merge every observed collider onto its target identity
            for (int i = 0; i < results.Objects.Count; i++)
            {
                Collider col = results.Objects[i];
                if (ReferenceEquals(col, null))
                    continue;

                Component actor = VisionTargetsManifest.ResolveTarget(col);
                if (!actor)
                    continue;

                int visiblePoints = results.VisiblePointCounts[i];
                int samples = results.SampleCounts[i];
                int inCone = results.InConeCounts[i];
                int lit = results.LitCounts[i];
                float angle = results.Angles[i];
                float distance = results.Distances[i];

                if (actorIndex.TryGetValue(actor, out int index))
                {
                    DataVisionSeenObject entry = output[index];

                    // The most-directly-observed collider represents the target
                    if (angle < entry.Angle)
                    {
                        entry.Angle = angle;
                        entry.ResultObject = col;
                    }

                    if (distance < entry.Distance)
                        entry.Distance = distance;

                    entry.VisiblePointCount += visiblePoints;
                    entry.SampleCount += samples;
                    entry.InConeCount += inCone;
                    entry.LitCount += lit;
                    entry.IsVisible |= visiblePoints > 0;
                    output[index] = entry;
                }
                else
                {
                    actorIndex[actor] = output.Count;
                    output.Add(new DataVisionSeenObject
                    {
                        Actor = actor,
                        ResultObject = col,
                        IsVisible = visiblePoints > 0,
                        Angle = angle,
                        Distance = distance,
                        VisiblePointCount = visiblePoints,
                        SampleCount = samples,
                        InConeCount = inCone,
                        LitCount = lit
                    });
                }
            }

            // Finalize once merged: visibility fraction, and the visible transition against the previous update
            for (int i = 0; i < output.Count; i++)
            {
                DataVisionSeenObject entry = output[i];
                entry.Visibility = entry.SampleCount > 0
                    ? Mathf.Clamp01(entry.VisiblePointCount / (float)entry.SampleCount)
                    : 0f;

                bool wasVisible = previousIndex.TryGetValue(entry.Actor, out int prevIdx) && previous[prevIdx].IsVisible;
                entry.JustBecameVisible = entry.IsVisible && !wasVisible;
                output[i] = entry;
            }
        }

        /// <summary>
        /// True if any resolved target's representative collider is <paramref name="visibleObject"/>. Prefer
        /// comparing <see cref="DataVisionSeenObject.Actor"/> (via VisionTargetsManifest.ResolveTarget) for
        /// identity - a multi-collider actor's representative collider can change between updates.
        /// </summary>
        public static bool DataSeenContainsObject(List<DataVisionSeenObject> data, Collider visibleObject)
        {
            foreach (DataVisionSeenObject item in data)
            {
                if (item.ResultObject == visibleObject)
                    return true;
            }

            return false;
        }

        #endregion RESOLVE
    }
}
