using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Protostar.Registry.Acceptance.Support;

/// <summary>The Docker containers and token certificates one test run shares: Postgres 18 and RustFS 1.0.5.</summary>
/// <remarks>
/// <para>
/// Started once per run (see <see cref="TestRunHooks"/>) because a container start costs seconds and a database or a
/// bucket costs milliseconds: every scenario gets its own database and bucket on these (see
/// <see cref="RegistryScenario"/>), so sharing the servers does not share state. The images are the ones the AppHost
/// and the production compose file run.
/// </para>
/// <para>
/// Docker may be shared with other work, so the containers get Docker's random names plus the
/// <see cref="Label"/> label, and are only ever removed by this class or by Testcontainers' Ryuk reaper, which removes
/// them if the run dies before disposing them.
/// </para>
/// </remarks>
public static class RegistryInfrastructure
{
    /// <summary>The label on every container this suite starts.</summary>
    public const string Label = "dev.protostar.registry-acceptance";

    private const int S3Port = 9000;
    private const string ObjectStoreAccessKey = "acceptance-access-key";
    private const string ObjectStoreSecretKey = "acceptance-secret-key-0123456789abcdefghij";

    private static PostgreSqlContainer? _postgres;
    private static IContainer? _objectStore;

    /// <summary>The token signing and encryption certificates, as the production host loads them.</summary>
    public static TokenCertificateFiles Certificates { get; private set; } = null!;

    public static async Task StartAsync()
    {
        Certificates = TokenCertificateFiles.Create();

        _postgres = new PostgreSqlBuilder("postgres:18")
            .WithLabel(Label, "postgres")
            .Build();

        _objectStore = new ContainerBuilder("rustfs/rustfs:1.0.5")
            .WithLabel(Label, "objectstore")
            .WithEnvironment("RUSTFS_ACCESS_KEY", ObjectStoreAccessKey)
            .WithEnvironment("RUSTFS_SECRET_KEY", ObjectStoreSecretKey)
            .WithPortBinding(S3Port, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(S3Port).ForPath("/health")))
            .Build();

        await Task.WhenAll(_postgres.StartAsync(), _objectStore.StartAsync());
    }

    public static async Task StopAsync()
    {
        if (_postgres is not null)
            await _postgres.DisposeAsync();
        if (_objectStore is not null)
            await _objectStore.DisposeAsync();
        Certificates?.Dispose();
    }

    /// <summary>A connection string for <paramref name="database"/> on the shared Postgres server.</summary>
    public static string ConnectionString(string database) =>
        new NpgsqlConnectionStringBuilder(Postgres.GetConnectionString()) { Database = database }.ConnectionString;

    /// <summary>A connection string for the server's maintenance database, to create and drop scenario databases from.</summary>
    public static string AdminConnectionString => ConnectionString("postgres");

    public static string ObjectStoreUrl =>
        $"http://{ObjectStore.Hostname}:{ObjectStore.GetMappedPublicPort(S3Port)}";

    public static string AccessKey => ObjectStoreAccessKey;

    public static string SecretKey => ObjectStoreSecretKey;

    private static PostgreSqlContainer Postgres =>
        _postgres ?? throw new InvalidOperationException("The test run's containers have not been started.");

    private static IContainer ObjectStore =>
        _objectStore ?? throw new InvalidOperationException("The test run's containers have not been started.");
}
