var builder = DistributedApplication.CreateBuilder(args);

// GitHub OAuth app credentials. Defaults live in appsettings.json ("placeholder") so the stack
// boots out of the box; real values come from user-secrets (Parameters:GitHubClientId/Secret) and
// override them. Only the real values make the GitHub login leg work end to end.
var gitHubClientId = builder.AddParameter("GitHubClientId");
var gitHubClientSecret = builder.AddParameter("GitHubClientSecret", secret: true);

// Postgres with a persistent data volume so users survive container restarts in dev.
var postgres = builder.AddPostgres("postgres").WithDataVolume();
var registrydb = postgres.AddDatabase("registrydb");

builder.AddProject<Projects.Protostar_Registry_Api>("api")
    .WithReference(registrydb)
    .WaitFor(registrydb)
    .WithEnvironment("GitHub__ClientId", gitHubClientId)
    .WithEnvironment("GitHub__ClientSecret", gitHubClientSecret);

builder.Build().Run();
