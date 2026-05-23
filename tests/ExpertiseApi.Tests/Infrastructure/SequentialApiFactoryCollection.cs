namespace ExpertiseApi.Tests.Infrastructure;

/// <summary>
/// xUnit collection for tests that instantiate <see cref="ApiFactory"/> WITHOUT
/// the Postgres testcontainer fixture (i.e., DI-only tests with stub connection
/// strings). Each ApiFactory instance triggers Program.cs to call
/// <c>Log.Logger = new LoggerConfiguration()…CreateBootstrapLogger()</c>,
/// which Serilog rejects with "The logger is already frozen" when multiple
/// instances initialise concurrently in the same test process.
///
/// xUnit runs distinct test classes in parallel by default; placing every
/// ApiFactory-instantiating non-Postgres test class in this collection
/// disables intra-collection parallelism (via DisableParallelization=true)
/// AND prevents concurrent execution with tests in any OTHER collection that
/// also has DisableParallelization set. Tests in unrelated, parallel-enabled
/// collections (incl. <c>Postgres</c>, which serialises only within itself
/// via ICollectionFixture coordination of the shared PostgresFixture) can
/// still run in parallel with this collection — the bootstrap-logger race
/// only matters between non-Postgres ApiFactory-instantiating tests, all of
/// which live here.
/// </summary>
[CollectionDefinition("SequentialApiFactory", DisableParallelization = true)]
public sealed class SequentialApiFactoryCollection { }
