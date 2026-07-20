using System;
using System.Collections.Generic;
using UnityEngine;

namespace GalaxyGourd.Visioncast
{
    /// <summary>
    /// Spatial-query stage of vision: for each scheduled source, find the candidate colliders within its
    /// range. Angle filtering and sample generation stay in the Visioncaster. This is the swap point the
    /// DoD branch replaces physics overlap with a Burst spatial grid behind the same contract.
    /// </summary>
    internal interface IBroadphase : IDisposable
    {
        /// <summary>
        /// Appends candidate colliders within range of each source to candidates[i], for the first
        /// 'count' sources. The candidate lists are caller-owned and pre-cleared; sources[i] aligns
        /// with candidates[i].
        /// </summary>
        void Query(List<VisioncastSource> sources, int count, List<List<Collider>> candidates);
    }
}
