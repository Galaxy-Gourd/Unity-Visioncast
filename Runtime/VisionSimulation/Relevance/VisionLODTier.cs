namespace GalaxyGourd.Visioncast
{
    /// <summary>
    /// One vision level-of-detail tier: a source whose relevance distance falls within
    /// <see cref="MaxDistance"/> updates once every <see cref="Cadence"/> ticks. See <see cref="VisionLOD"/>.
    /// </summary>
    [System.Serializable]
    public struct VisionLODTier
    {
        /// <summary>Upper bound (inclusive) of the relevance distance this tier covers.</summary>
        public float MaxDistance;

        /// <summary>Cast once every this many ticks (1 = every tick). 0 or less = dormant (never).</summary>
        public int Cadence;

        /// <summary>
        /// Caps a source's sample resolution while in this tier (far tiers sample coarser). 0 = no cap,
        /// use the source's own resolution. When set, the effective resolution is min(source, this).
        /// </summary>
        public int SampleResolution;
    }
}
