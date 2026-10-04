using Xunit;

namespace NetCat.Tests;

// Native process launch fixtures use OS scheduling and ephemeral ports. Keep
// them apart from the baseline's short deterministic fake-clock deadlines.
[CollectionDefinition("Candidate18 native resources", DisableParallelization = true)]
public sealed class Candidate18NativeCollection;
