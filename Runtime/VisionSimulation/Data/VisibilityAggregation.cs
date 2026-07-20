namespace GalaxyGourd.Visioncast
{
    /// <summary>
    /// How <see cref="VisioncastSourceCompound"/> combines an object's per-source visibility into a
    /// single value when more than one child source resolves the same object.
    /// </summary>
    public enum VisibilityAggregation
    {
        /// <summary>
        /// Highest visibility among the child sources (e.g. exposure to the brightest light).
        /// </summary>
        Max = 0,

        /// <summary>
        /// Sum of child visibilities, clamped to [0, 1] (cumulative exposure).
        /// </summary>
        Sum = 1,

        /// <summary>
        /// Mean visibility across the child sources that currently see the object.
        /// </summary>
        Average = 2
    }
}
