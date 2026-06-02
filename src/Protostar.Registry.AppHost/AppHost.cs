var builder = DistributedApplication.CreateBuilder(args);

// GitHub OAuth app credentials. Defaults live in appsettings.json ("placeholder") so the stack
// boots out of the box; real values come from user-secrets (Parameters:GitHubClientId/Secret) and
// override them. Only the real values make the GitHub login leg work end to end.
var gitHubClientId = builder.AddParameter("GitHubClientId");
var gitHubClientSecret = builder.AddParameter("GitHubClientSecret", secret: true);

// Postgres with a persistent data volume so users survive container restarts in dev.
var postgres = builder.AddPostgres("postgres").WithDataVolume().WithImageTag("18");
var registrydb = postgres.AddDatabase("registrydb");

builder.AddProject<Projects.Protostar_Registry_Api>("api")
    .WithReference(registrydb)
    .WaitFor(registrydb)
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
