using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.EntityFrameworkCore;
using Protostar.Registry.Api;
using Protostar.Registry.Api.Auth;
using Protostar.Registry.Api.Data;
using Scalar.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Postgres + EF Core. Aspire injects the "registrydb" connection string. UseOpenIddict() adds
// OpenIddict's tables (applications, authorizations, scopes, tokens) to the same context.
builder.AddNpgsqlDbContext<RegistryDbContext>(
    "registrydb",
    configureDbContextOptions: options => options.UseOpenIddict());

// OpenIddict: the registry's own OAuth2/OIDC authorization server. It owns the User records and
// mints the tokens the CLI stores; the login step is federated to GitHub below.
builder.Services.AddOpenIddict()
    .AddCore(options => options
        .UseEntityFrameworkCore()
        .UseDbContext<RegistryDbContext>())
    .AddServer(options =>
    {
        options.SetAuthorizationEndpointUris("connect/authorize")
            .SetTokenEndpointUris("connect/token")
            .SetUserInfoEndpointUris("connect/userinfo")
            .SetEndSessionEndpointUris("connect/logout");

        options.AllowAuthorizationCodeFlow()
            .AllowRefreshTokenFlow()
            .RequireProofKeyForCodeExchange();

        options.RegisterScopes(Scopes.OpenId, Scopes.Profile, Scopes.Email, "registry");

        // Dev certificates are fine locally and in CI. Production supplies real signing/encryption
        // certificates (a deployment concern, tracked separately).
        options.AddDevelopmentEncryptionCertificate()
            .AddDevelopmentSigningCertificate();

        // CLIs validate access tokens as plain JWTs, so don't encrypt them.
        options.DisableAccessTokenEncryption();

        // Endpoints are served over HTTPS in every environment (OpenIddict rejects plain HTTP).
        // Locally the API uses its Aspire HTTPS endpoint backed by the ASP.NET Core dev cert.
        options.UseAspNetCore()
            .EnableAuthorizationEndpointPassthrough()
            .EnableTokenEndpointPassthrough()
            .EnableUserInfoEndpointPassthrough()
            .EnableEndSessionEndpointPassthrough();
    })
    .AddValidation(options =>
    {
        options.UseLocalServer();
        options.UseAspNetCore();
    });

// Cookie carries the interactive sign-in; GitHub is the upstream identity (no passwords here).
// Placeholders keep the app bootable without GitHub credentials (tests, plain `dotnet run`); the
// real flow only works once Parameters:GitHubClientId/Secret are supplied (see README + AppHost).
var gitHubClientId = builder.Configuration["GitHub:ClientId"] ?? "placeholder";
var gitHubClientSecret = builder.Configuration["GitHub:ClientSecret"] ?? "placeholder";

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = AuthEndpoints.GitHubScheme;
})
.AddCookie()
.AddGitHub(AuthEndpoints.GitHubScheme, options =>
{
    options.ClientId = gitHubClientId;
    options.ClientSecret = gitHubClientSecret;
    options.Scope.Add("read:user");
    options.Scope.Add("user:email");
    options.SaveTokens = true;

    // GitHub's default claim mapping covers id + login; capture display name and avatar too.
    options.Events.OnCreatingTicket = context =>
    {
        AddJsonClaim(context, "urn:github:name", "name");
        AddJsonClaim(context, "urn:github:avatar", "avatar_url");
        return Task.CompletedTask;

        static void AddJsonClaim(OAuthCreatingTicketContext context, string claimType, string jsonKey)
        {
            if (context.User.TryGetProperty(jsonKey, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                value.GetString() is { Length: > 0 } text)
            {
                context.Identity?.AddClaim(new Claim(claimType, text));
            }
        }
    };
});

builder.Services.AddAuthorization();

// OpenAPI document (served at /openapi/v1.json) + an interactive Scalar reference UI in dev.
builder.Services.AddOpenApi();

// Seed the public CLI client outside the test host (which has no live database).
if (!builder.Environment.IsEnvironment("Testing"))
    builder.Services.AddHostedService<OpenIddictClientSeeder>();

var app = builder.Build();

app.MapDefaultEndpoints();

app.UseAuthentication();
app.UseAuthorization();

// Apply migrations on startup in Development. Production migrates via a deploy step, not here.
if (app.Environment.IsDevelopment())
{
    using (var scope = app.Services.CreateScope())
    {
        await scope.ServiceProvider.GetRequiredService<RegistryDbContext>().Database.MigrateAsync();
    }

    // API docs: the raw OpenAPI document and an interactive Scalar reference (Swagger-UI successor).
    app.MapOpenApi();
    app.MapScalarApiReference(options => options.WithTitle("protostar registry API"));
}

app.MapAuthEndpoints();

// API-contract version surface. The CLI checks `apiMajors` on connect to decide compatibility.
app.MapGet("/v1/meta", () => Results.Ok(new
{
    service = "protostar-registry",
    version = ApiInfo.Version,
    apiMajors = ApiInfo.ApiMajors,
}));

app.Run();

// Exposed for WebApplicationFactory in the integration tests.
public partial class Program;
