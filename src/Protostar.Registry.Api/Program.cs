using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using Protostar.Registry.Api;
using Protostar.Registry.Api.Common;
using Protostar.Registry.Api.Identity;
using Protostar.Registry.Api.Infrastructure;
using Protostar.Registry.Api.Skills;
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

// Skill-push endpoints authenticate with a registry access token + "registry" scope, not the default
// cookie/GitHub login.
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(SkillEndpoints.Policy, policy =>
    {
        policy.AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
        policy.RequireAuthenticatedUser();
        policy.RequireAssertion(ctx => ctx.User.HasScope("registry"));
    });
});

builder.Services.AddScoped<SkillPushService>();

// Domain events: a dispatcher plus an open-generic logging handler as the placeholder consumer. Real
// handlers (evaluators, the refinement loop) register their own IDomainEventHandler<T> later.
builder.Services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
builder.Services.AddScoped(typeof(IDomainEventHandler<>), typeof(LoggingDomainEventHandler<>));

// OpenAPI document (served at /openapi/v1.json) + an interactive Scalar reference UI in dev. In
// Development, advertise the OAuth2 flow so the Scalar UI can sign in and call protected endpoints.
builder.Services.AddOpenApi(options =>
{
    if (builder.Environment.IsDevelopment())
        options.AddDocumentTransformer<OAuthSecuritySchemeTransformer>();
});

// Seed the public CLI client outside the test host (which has no live database).
if (!builder.Environment.IsEnvironment("Testing"))
    builder.Services.AddHostedService<OpenIddictClientSeeder>();

// Seed the browser client the Scalar UI logs in with. Development only; never seed it in production.
if (builder.Environment.IsDevelopment())
    builder.Services.AddHostedService<ScalarUiClientSeeder>();

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
    // The OAuth flow lets you sign in from the UI and call the protected skill endpoints with a token.
    app.MapOpenApi();
    app.MapScalarApiReference(options =>
    {
        options.WithTitle("protostar registry API")
            .AddPreferredSecuritySchemes(OAuthSecuritySchemeTransformer.SchemeId)
            .AddAuthorizationCodeFlow(OAuthSecuritySchemeTransformer.SchemeId, flow =>
            {
                flow.ClientId = ScalarUiClientSeeder.ClientId;
                flow.Pkce = Pkce.Sha256;
                flow.SelectedScopes = ["openid", "profile", "email", "registry"];
                // Pin the redirect to the reference page; some Scalar versions otherwise send the origin.
                flow.RedirectUri = "https://localhost:7443/scalar/v1";
            });
    });
}

app.MapAuthEndpoints();
app.MapLoginEndpoints();
app.MapSkillEndpoints();

// API-contract version surface. The CLI checks `apiMajors` on connect to decide compatibility.
app.MapGet("/v1/meta", () => new ApiMeta("protostar-registry", ApiInfo.Version, ApiInfo.ApiMajors))
    .WithName("GetMeta")
    .WithTags("Meta")
    .WithSummary("Registry identity and API-compatibility surface")
    .Produces<ApiMeta>();

app.Run();

// Exposed for WebApplicationFactory in the integration tests.
public partial class Program;
