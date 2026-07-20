using System.Collections.Generic;
using UnityEngine;

namespace GalaxyGourd.Visioncast
{
    /// <summary>
    /// Default broadphase: per-source Physics.OverlapSphereNonAlloc on the main thread. No external
    /// dependency or Unity-version requirement. A single shared hit buffer serves every source because
    /// they are queried sequentially.
    /// </summary>
    internal sealed class PhysicsOverlapBroadphase : IBroadphase
    {
        #region VARIABLES

        private const int CONST_MaxHitsPerSource = 64;
        private readonly Collider[] _hits = new Collider[CONST_MaxHitsPerSource];

        #endregion VARIABLES


        #region QUERY

        public void Query(List<VisioncastSource> sources, int count, List<List<Collider>> candidates)
        {
            for (int i = 0; i < count; i++)
            {
                VisioncastSource source = sources[i];
                int hitCount = Physics.OverlapSphereNonAlloc(
                    source.Position,
                    source.Range,
                    _hits,
                    source.BroadphaseLayers,
                    QueryTriggerInteraction.UseGlobal);

                // A saturated buffer silently drops targets - surface it rather than hide it
                if (hitCount >= _hits.Length)
                {
                    Debug.LogWarning($"Visioncast broadphase buffer ({CONST_MaxHitsPerSource}) saturated " +
                                     $"for '{source.name}'; some targets may be ignored this tick.");
                }

                List<Collider> list = candidates[i];
                for (int h = 0; h < hitCount; h++)
                {
                    if (_hits[h])
                        list.Add(_hits[h]);
                }
            }
        }

        public void Dispose()
        {
        }

        #endregion QUERY
    }
}
