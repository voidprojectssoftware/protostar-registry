using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Protostar.Registry.Acceptance.Support;

/// <summary>The real registry API, in process, configured as a production deployment against one scenario's database and bucket.</summary>
/// <remarks>
/// <para>
/// The environment is <c>Production</c>, so the host takes the same path a deployment does: token certificates from PFX
/// files, migrations at startup, the client seeder running, no Development sign-in and no
/// Scalar UI. Settings go in with <c>UseSetting</c>, not <c>ConfigureAppConfiguration</c>, because Program reads some of
/// them (the connection string, the certificate paths) before the host is built.
/// </para>
/// <para>
/// Two things differ from production, both only here: <see cref="AcceptanceSignIn"/> stands in for GitHub, and Data
/// Protection keys are ephemeral, so the run never writes keys under the user's home. The orphan sweep and the
/// background reprocessing pass are off because they run on timers: a pass that started on its own could interleave
/// with a scenario's steps. The reprocessing scenarios run a pass themselves.
/// </para>
/// </remarks>
public sealed class RegistryFactory : WebApplicationFactory<Program>
{
    private readonly string _database;
    private readonly string _bucket;

    public RegistryFactory(string database, string bucket)
    {
        _database = database;
        _bucket = bucket;
    }

    /// <summary>What the registry has logged at Warning and above since it started.</summary>
    public CapturedLogs Logs { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        var certificates = RegistryInfrastructure.Certificates;
        builder.UseEnvironment(Environments.Production);
        builder.UseSetting("ConnectionStrings:registrydb", RegistryInfrastructure.ConnectionString(_database));
        builder.UseSetting("ObjectStore:ServiceUrl", RegistryInfrastructure.ObjectStoreUrl);
        builder.UseSetting("ObjectStore:AccessKey", RegistryInfrastructure.AccessKey);
        builder.UseSetting("ObjectStore:SecretKey", RegistryInfrastructure.SecretKey);
        builder.UseSetting("ObjectStore:Bucket", _bucket);
        builder.UseSetting("ObjectStore:OrphanSweepEnabled", "false");
        builder.UseSetting("Reprocessing:Enabled", "false");
        builder.UseSetting("OpenIddict:SigningCertificate:Path", certificates.SigningPath);
        builder.UseSetting("OpenIddict:SigningCertificate:Password", certificates.Password);
        builder.UseSetting("OpenIddict:EncryptionCertificate:Path", certificates.EncryptionPath);
        builder.UseSetting("OpenIddict:EncryptionCertificate:Password", certificates.Password);
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        // TestServer has no request body size feature, so every upload would warn that its limit was not applied.
        builder.UseSetting("Logging:LogLevel:Microsoft.AspNetCore.Routing.EndpointRoutingMiddleware", "Error");

        builder.ConfigureLogging(logging => logging.AddProvider(Logs));

        builder.ConfigureTestServices(services =>
        {
            services.AddTransient<IStartupFilter, AcceptanceSignIn>();
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
        });
    }
}
