// The API's HTTPS port: 7443, the port the GitHub OAuth app's callback names, unless PROTOSTAR_API_PORT
// says otherwise. Another port lets a second stack run beside the first (one per worktree, each with its
// own database and store); only the dev sign-in works there, because GitHub redirects to 7443 alone.
const int DefaultApiPort = 7443;
var apiPortSetting = Environment.GetEnvironmentVariable("PROTOSTAR_API_PORT");
var apiPort = string.IsNullOrEmpty(apiPortSetting) ? DefaultApiPort
    : int.TryParse(apiPortSetting, out var port) && port is > 0 and <= 65535 ? port
    : throw new InvalidOperationException($"PROTOSTAR_API_PORT must be a TCP port number (1 to 65535), not '{apiPortSetting}'.");

// The launch profile pins the dashboard and its OTLP and resource-service endpoints to fixed ports too, so a
// second stack would still collide there. Off the default port, let the OS pick them; the dashboard prints
// where it landed. Command-line configuration outranks the profile's environment variables.
string[] appHostArgs = apiPort == DefaultApiPort ? args :
[
    .. args,
    "--ASPNETCORE_URLS=https://127.0.0.1:0",
    "--ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL=https://127.0.0.1:0",
    "--ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL=https://127.0.0.1:0",
];

var builder = DistributedApplication.CreateBuilder(appHostArgs);

// GitHub OAuth app credentials. Defaults live in appsettings.json ("placeholder") so the stack
// boots out of the box; real values come from user-secrets (Parameters:GitHubClientId/Secret) and
// override them. Only the real values make the GitHub login leg work end to end.
var gitHubClientId = builder.AddParameter("GitHubClientId");
var gitHubClientSecret = builder.AddParameter("GitHubClientSecret", secret: true);

// Postgres with a persistent data volume so users survive container restarts in dev.
var postgres = builder.AddPostgres("postgres").WithDataVolume().WithImageTag("18");
var registrydb = postgres.AddDatabase("registrydb");

// S3-compatible object store for transcript content (gigabytes per user, far too much for database rows).
// RustFS in dev; the API speaks plain S3, so production can use any S3-compatible store. Credentials are
// generated once per machine and kept in user-secrets; the data volume keeps content across restarts.
// The volume is named the way WithDataVolume names the Postgres one (VolumeNameGenerator: a hash of this
// AppHost's path), so each checkout or worktree pairs its own database with its own store. The orphan sweep
// relies on that pairing. Before #33 every checkout shared a volume named protostar-registry-objectstore:
// databases created then reference blobs left in that volume, which this stack no longer mounts.
var objectStoreAccessKey = builder.AddParameter("ObjectStoreAccessKey", new GenerateParameterDefault { MinLength = 20, Special = false }, persist: true);
var objectStoreSecretKey = builder.AddParameter("ObjectStoreSecretKey", new GenerateParameterDefault { MinLength = 40, Special = false }, secret: true, persist: true);
var objectStore = builder.AddContainer("objectstore", "rustfs/rustfs", "1.0.5")
    .WithHttpEndpoint(targetPort: 9000, name: "s3")
    .WithEnvironment("RUSTFS_ACCESS_KEY", objectStoreAccessKey)
    .WithEnvironment("RUSTFS_SECRET_KEY", objectStoreSecretKey)
    .WithHttpHealthCheck("/health", endpointName: "s3");
objectStore.WithVolume(VolumeNameGenerator.Generate(objectStore, "data"), "/data");

builder.AddProject<Projects.Protostar_Registry_Api>("api")
    .WithReference(registrydb)
    .WaitFor(registrydb)
    .WaitFor(objectStore)
    .WithEnvironment("ObjectStore__ServiceUrl", objectStore.GetEndpoint("s3"))
    .WithEnvironment("ObjectStore__AccessKey", objectStoreAccessKey)
    .WithEnvironment("ObjectStore__SecretKey", objectStoreSecretKey)
    // Pin the API to a stable HTTPS port in dev so `--registry` and the GitHub OAuth callback
    // (https://localhost:7443/signin-github) stay the same across runs. Non-proxied = the API binds
    // the port directly with the ASP.NET Core dev cert.
    .WithEndpoint("https", endpoint =>
    {
        endpoint.Port = apiPort;
        endpoint.IsProxied = false;
    })
    // Nothing uses the plain HTTP endpoint (OpenIddict refuses HTTP), so let its port float rather than
    // take the launch profile's fixed 5021, which a second stack would collide on.
    .WithEndpoint("http", endpoint => endpoint.Port = null)
    .WithHttpHealthCheck("/health", endpointName: "https")
    .OnResourceReady((_, _, _) =>
    {
        var registry = $"https://localhost:{apiPort}";
        Console.WriteLine();
        Console.WriteLine($"  Protostar registry API: {registry}");
        Console.WriteLine($"  CLI: protostar auth login --registry {registry}" +
            (apiPort == DefaultApiPort ? string.Empty : " --provider dev   (GitHub sign-in works on 7443 only)"));
        Console.WriteLine();
        return Task.CompletedTask;
    })
    .WithEnvironment("GitHub__ClientId", gitHubClientId)
    .WithEnvironment("GitHub__ClientSecret", gitHubClientSecret)
    .WithUrls(context =>
    {
        var https = context.GetEndpoint("https");

        // Give the auto-generated endpoint URLs human-readable names.
        foreach (var url in context.Urls)
        {
            url.DisplayText = url.Endpoint?.EndpointName switch
            {
                "https" => "API base (HTTPS)",
                "http" => "API base (HTTP)",
                _ => url.DisplayText,
            };
        }

        // Named, clickable links to the things that actually live on the API (relative to HTTPS).
        context.Urls.Add(new() { Url = "/scalar/v1", DisplayText = "API reference (Scalar)", Endpoint = https });
        context.Urls.Add(new() { Url = "/openapi/v1.json", DisplayText = "OpenAPI document", Endpoint = https });
        context.Urls.Add(new() { Url = "/v1/meta", DisplayText = "Service metadata", Endpoint = https });
        context.Urls.Add(new() { Url = "/.well-known/openid-configuration", DisplayText = "OpenID configuration", Endpoint = https });
        context.Urls.Add(new() { Url = "/health", DisplayText = "Health checks", Endpoint = https });
    });

builder.Build().Run();
