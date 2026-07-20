using System.Collections.Generic;
using UnityEngine;

namespace GalaxyGourd.Visioncast
{
    /// <summary>
    /// Source of truth for all active interactable objects. Maps colliders to key objects
    /// </summary>
    public static class VisionTargetsManifest
    {
        #region VARIABLES

        internal static HashSet<Collider> Manifest { get; private set; } = new();
        // Optional collider -> owning actor mapping, used to group a multi-collider actor's results
        // into a single target (see VisioncastSourceCompound). Colliders with no entry stand alone.
        private static readonly Dictionary<Collider, Component> _actors = new();

        // DoD target identity registry (see PERFORMANCE-STRUCTURAL DoD hand-off): a dense, contiguous
        // int id per registered collider. The Burst spatial grid keys on these ids and the scatter maps
        // them back to colliders, so no collider ever crosses into a job. Kept dense via swap-remove; an
        // id can change only when ANOTHER target unregisters, so consumers must snapshot ids at a tick
        // boundary (Version bumps on every add/remove) rather than hold one across registration changes.
        private static readonly Dictionary<Collider, int> _idOf = new();
        private static readonly List<Collider> _byId = new();
        private static int _version;

        /// <summary>Bumps on every add/remove; native mirrors (gather/grid) rebuild when it changes.</summary>
        internal static int Version => _version;
        /// <summary>Number of registered targets; ids are the dense range [0, TargetCount).</summary>
        internal static int TargetCount => _byId.Count;
        /// <summary>Registered colliders indexed by id; the gather enumerates this to build native buffers.</summary>
        internal static IReadOnlyList<Collider> TargetsById => _byId;

        /// <summary>Dense id of a registered collider, or -1 if not registered.</summary>
        internal static int GetId(Collider col)
        {
            return col != null && _idOf.TryGetValue(col, out int id) ? id : -1;
        }

        /// <summary>Collider for a dense id, or null if out of range (e.g. an id freed by swap-remove).</summary>
        internal static Collider GetCollider(int id)
        {
            return id >= 0 && id < _byId.Count ? _byId[id] : null;
        }

        #endregion VARIABLES


        #region INITIALIZATION

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            Manifest.Clear();
            _actors.Clear();
            _idOf.Clear();
            _byId.Clear();
            _version++;
        }

        #endregion INITIALIZATION


        #region REGISTRATION

        public static void Register(Collider col)
        {
            Register(col, null);
        }

        /// <summary>
        /// Registers a collider as a vision target, optionally owned by <paramref name="actor"/>.
        /// Register every collider of a multi-collider actor against the same actor so line-of-sight
        /// results collapse to one target per actor.
        /// </summary>
        public static void Register(Collider col, Component actor)
        {
            if (col == null)
                return;

            Manifest.Add(col);

            // Assign a dense id on first registration (idempotent; re-registering only updates the actor)
            if (!_idOf.ContainsKey(col))
            {
                _idOf[col] = _byId.Count;
                _byId.Add(col);
                _version++;
            }

            if (actor)
                _actors[col] = actor;
            else
                _actors.Remove(col);
        }

        public static void Unregister(Collider col)
        {
            Manifest.Remove(col);
            _actors.Remove(col);

            // Swap-remove from the dense id registry: move the last target into the freed slot so ids
            // stay contiguous. The moved target's id changes - safe because registration resolves at
            // tick boundaries and Version bumps so native mirrors rebuild.
            if (col != null && _idOf.TryGetValue(col, out int id))
            {
                int last = _byId.Count - 1;
                if (id != last)
                {
                    Collider moved = _byId[last];
                    _byId[id] = moved;
                    _idOf[moved] = id;
                }

                _byId.RemoveAt(last);
                _idOf.Remove(col);
                _version++;
            }
        }

        /// <summary>
        /// The single identity a collider resolves to: its registered actor, or the collider itself when it
        /// stands alone (a Collider is a Component, so both share the type). Never null for a live collider -
        /// every target has exactly one owner, so consumers never branch on "actor or collider?". Costs
        /// nothing extra: standalone colliders store no entry, the collider *is* the fallback.
        /// </summary>
        public static Component ResolveTarget(Collider col)
        {
            if (col == null)
                return null;

            return _actors.TryGetValue(col, out Component actor) && actor ? actor : col;
        }

        /// <summary>
        /// Whether a collider has an EXPLICIT registered actor (i.e. is one of several colliders owned by an
        /// actor). Prefer <see cref="ResolveTarget"/> for identity - this is only for callers that genuinely
        /// need to distinguish an explicitly-grouped collider from a standalone one.
        /// </summary>
        public static bool TryGetActor(Collider col, out Component actor)
        {
            if (col != null && _actors.TryGetValue(col, out actor) && actor)
                return true;

            actor = null;
            return false;
        }

        #endregion REGISTRATION
    }
}
