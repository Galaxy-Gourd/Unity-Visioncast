using System.Runtime.CompilerServices;

// The DoD benchmark drives internal DoD scaffolding (target identity registry, native gather) directly
// to validate correctness against the managed path, without exposing it as public package API.
[assembly: InternalsVisibleTo("GG.Visioncast.Benchmark")]

// The test assemblies assert on the same internal scaffolding (result back buffers, LOD tier
// resolution, the DoD gather / manifest id registry) plus the internal tick entry points, so the
// package can be verified without widening its public surface.
[assembly: InternalsVisibleTo("GG.Visioncast.Tests")]
[assembly: InternalsVisibleTo("GG.Visioncast.EditorTests")]
[assembly: InternalsVisibleTo("GG.Visioncast.TestSupport")]
