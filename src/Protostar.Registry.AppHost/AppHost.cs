var builder = DistributedApplication.CreateBuilder(args);

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
var objectStoreAccessKey = builder.AddParameter("ObjectStoreAccessKey", new GenerateParameterDefault { MinLength = 20, Special = false }, persist: true);
var objectStoreSecretKey = builder.AddParameter("ObjectStoreSecretKey", new GenerateParameterDefault { MinLength = 40, Special = false }, secret: true, persist: true);
var objectStore = builder.AddContainer("objectstore", "rustfs/rustfs", "1.0.5")
    .WithHttpEndpoint(targetPort: 9000, name: "s3")
    .WithEnvironment("RUSTFS_ACCESS_KEY", objectStoreAccessKey)
    .WithEnvironment("RUSTFS_SECRET_KEY", objectStoreSecretKey)
    .WithVolume("protostar-registry-objectstore", "/data")
    .WithHttpHealthCheck("/health", endpointName: "s3");

builder.AddProject<Projects.Protostar_Registry_Api>("api")
    .WithReference(registrydb)
    .WaitFor(registrydb)
    .WaitFor(objectStore)
    .WithEnvironment("ObjectStore__ServiceUrl", objectStore.GetEndpoint("s3"))
    .WithEnvironment("ObjectStore__AccessKey", objectStoreAccessKey)
    .WithEnvironment("ObjectStore__SecretKey", objectStoreSecretKey)
    // Pin the API to a stable HTTPS port in dev so `--registry` and the GitHub OAuth callback
    // (https://localhost:7443/signin-github) stay the same across runs. Non-proxied = the API binds
    // 7443 directly with the ASP.NET Core dev cert.
    .WithEndpoint("https", endpoint =>
    {
        endpoint.Port = 7443;
        endpoint.IsProxied = false;
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
