using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using Protostar.Registry.Api;
using Protostar.Registry.Api.Common;
using Protostar.Registry.Api.Identity;
using Protostar.Registry.Api.Infrastructure;
using Protostar.Registry.Api.Memory;
using Protostar.Registry.Api.Projects;
using Protostar.Registry.Api.Search;
using Protostar.Registry.Api.Sessions;
using Protostar.Registry.Api.Skills;
using Scalar.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Postgres + EF Core. Aspire injects the "registrydb" connection string. UseOpenIddict() adds
// OpenIddict's tables (applications, authorizations, scopes, tokens) to the same context.
// LogHandledRacesAtDebug keeps insert races the services recover from out of the Error log.
builder.AddNpgsqlDbContext<RegistryDbContext>(
    "registrydb",
    configureDbContextOptions: options => options.UseOpenIddict().LogHandledRacesAtDebug());

// Token signing and encryption keys. Loaded before anything else is built so a production host without them
// stops here with a message naming the settings, instead of failing on the first token request.
var tokenCertificates = TokenCertificates.Load(builder.Configuration, builder.Environment);

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

        // Configured PFX files in production (they outlive the container, so tokens survive restarts); the
        // per-machine development certificates only in Development when none are configured.
        if (tokenCertificates is null)
        {
            options.AddDevelopmentEncryptionCertificate()
                .AddDevelopmentSigningCertificate();
        }
        else
        {
            options.AddSigningCertificate(tokenCertificates.Signing)
                .AddEncryptionCertificate(tokenCertificates.Encryption);
        }

        // Normally the issuer is the request's origin (the public one, once forwarded headers apply).
        // OpenIddict:Issuer pins it, so a request that reaches the API another way cannot change it.
        if (builder.Configuration["OpenIddict:Issuer"] is { Length: > 0 } issuer)
            options.SetIssuer(new Uri(issuer, UriKind.Absolute));

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

// Cookie carries the interactive sign-in; GitHub is the upstream identity (no passwords here). The
// Development-only local dev user is registered there too, and nowhere else.
builder.Services.AddRegistrySignIn(builder.Configuration, builder.Environment);

// Behind a TLS-terminating proxy, believe X-Forwarded-* from the configured proxies only, so the issuer,
// discovery document and redirects carry the public https origin.
builder.Services.AddRegistryForwardedHeaders(builder.Configuration);

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
builder.Services.AddScoped<SkillQueryService>();
builder.Services.AddScoped<SkillChangeService>();

// Sessions: transcript bookkeeping in Postgres, transcript bytes in any S3-compatible object store
// (RustFS under Aspire, which sets ObjectStore__* for the API).
builder.Services.AddOptions<ObjectStoreOptions>()
    .Bind(builder.Configuration.GetSection(ObjectStoreOptions.Section))
    .Validate(o => !string.IsNullOrWhiteSpace(o.AccessKey) && !string.IsNullOrWhiteSpace(o.SecretKey),
        "ObjectStore:AccessKey and ObjectStore:SecretKey are required.")
    .Validate(o => o.OrphanSweepInterval > TimeSpan.Zero && o.OrphanSweepGrace >= TimeSpan.Zero,
        "ObjectStore:OrphanSweepInterval must be positive and ObjectStore:OrphanSweepGrace must not be negative.")
    .ValidateOnStart();
builder.Services.AddSingleton<IBlobStore, S3BlobStore>();
builder.Services.AddScoped<TranscriptService>();

// Chunk blobs orphaned by a lost append race are swept on an interval by every instance (safe to overlap,
// see OrphanChunkBlobSweep), unless ObjectStore:OrphanSweepEnabled is false. Not in the test host, which has
// no database or store.
builder.Services.AddScoped<OrphanChunkBlobSweep>();
if (!builder.Environment.IsEnvironment("Testing") &&
    builder.Configuration.GetValue(ObjectStoreOptions.Section + ":" + nameof(ObjectStoreOptions.OrphanSweepEnabled), true))
{
    builder.Services.AddHostedService<OrphanChunkBlobSweepService>();
}

// Projects: the identity sessions (and later memory and search) share across machines (ADR 0002).
builder.Services.AddScoped<ProjectService>();
builder.Services.AddScoped<TranscriptProjects>();
builder.Services.AddScoped<MemoryService>();
builder.Services.AddScoped<IFolderFilings, TranscriptFolderFilings>();
builder.Services.AddScoped<IDomainEventHandler<ProjectRemoteAttached>, RefileShallowClones>();
builder.Services.AddScoped<IDomainEventHandler<FolderLinkChanged>, RefileLinkedFolder>();

// Search: an owner-scoped Postgres full-text index over redacted session and memory text, kept current by
// domain event handlers as content arrives, and filled in for older content by a background backfill (not in the
// database-free test host).
builder.Services.AddScoped<SearchService>();
builder.Services.AddScoped<SearchIndexer>();
builder.Services.AddScoped<SearchBackfill>();
builder.Services.AddScoped<IDomainEventHandler<TranscriptChunkAppended>, IndexAppendedChunk>();
builder.Services.AddScoped<IDomainEventHandler<MemoryFileChanged>, IndexChangedMemoryFile>();
if (!builder.Environment.IsEnvironment("Testing"))
    builder.Services.AddHostedService<SearchBackfillService>();

// Enums travel as camelCase strings ("session", not 0) in bodies, and bind from either form in queries.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));

// Domain events: a dispatcher plus an open-generic logging handler as the placeholder consumer. Real
// handlers (evaluators, the refinement loop) register their own IDomainEventHandler<T> later.
builder.Services.AddScoped<IDomainEventDispatcher, DomainEventDispatcher>();
builder.Services.AddScoped(typeof(IDomainEventHandler<>), typeof(LoggingDomainEventHandler<>));

// OpenAPI document (served at /openapi/v1.json) + an interactive Scalar reference UI in dev. In
// Development, advertise the OAuth2 flow so the Scalar UI can sign in and call protected endpoints.
builder.Services.AddHttpContextAccessor();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<RegistryDocumentTransformer>();
    options.AddDocumentTransformer<OAuthSecuritySchemeTransformer>();
    options.AddOperationTransformer<OAuthSecuritySchemeTransformer>();
});

// Seed the public CLI client outside the test host (which has no live database).
if (!builder.Environment.IsEnvironment("Testing"))
    builder.Services.AddHostedService<OpenIddictClientSeeder>();

// Seed the browser client the Scalar UI logs in with. Development only; never seed it in production.
if (builder.Environment.IsDevelopment())
    builder.Services.AddHostedService<ScalarUiClientSeeder>();

var app = builder.Build();

// Operator and test entry point: `dotnet Protostar.Registry.Api.dll sweep-orphan-blobs` runs one sweep pass
// with this host's configuration and exits, without serving HTTP. Deliberately not an endpoint. It runs even
// when ObjectStore:OrphanSweepEnabled is false, so only point it at a bucket that belongs to this database.
if (args is ["sweep-orphan-blobs", ..])
{
    await using var scope = app.Services.CreateAsyncScope();
    var result = await scope.ServiceProvider.GetRequiredService<OrphanChunkBlobSweep>().RunAsync(CancellationToken.None);
    Console.WriteLine($"Listed {result.Listed} chunk blobs, deleted {result.Deleted} orphans.");
    return;
}

// Operator entry point, like sweep-orphan-blobs: `dotnet Protostar.Registry.Api.dll reindex-search` runs one search
// backfill pass and exits. The service runs the same pass at startup; this is for doing it on demand.
if (args is ["reindex-search", ..])
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<RegistryDbContext>().Database.MigrateAsync();
    var result = await scope.ServiceProvider.GetRequiredService<SearchBackfill>().RunAsync(CancellationToken.None);
    Console.WriteLine($"Indexed {result.Chunks} transcript chunks and {result.MemoryFiles} memory files; removed {result.Superseded} entries of earlier revisions.");
    return;
}

// First, so everything after it (OpenIddict's HTTPS check, issuer, redirects) sees the public scheme and host.
app.UseForwardedHeaders();

app.MapDefaultEndpoints();

app.UseAuthentication();
app.UseAuthorization();

// Apply pending migrations before serving, in every environment but the database-free test host. It runs
// before app.Run starts the hosted services (the client seeders, the orphan sweep), so they always see the
// current schema. EF Core takes a database lock for the duration, so two instances starting together do not
// both migrate; the registry runs as one instance anyway (docs/deploy/production.md).
if (!app.Environment.IsEnvironment("Testing"))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<RegistryDbContext>().Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    // API docs: the raw OpenAPI document and an interactive Scalar reference (Swagger-UI successor).
    // The OAuth flow lets you sign in from the UI and call the protected skill endpoints with a token.
    app.MapOpenApi();
    app.MapScalarApiReference((options, context) =>
    {
        options.WithTitle("protostar registry API")
            .AddPreferredSecuritySchemes(OAuthSecuritySchemeTransformer.SchemeId)
            .AddAuthorizationCodeFlow(OAuthSecuritySchemeTransformer.SchemeId, flow =>
            {
                flow.ClientId = ScalarUiClientSeeder.ClientId;
                flow.Pkce = Pkce.Sha256;
                flow.SelectedScopes = ["openid", "profile", "email", "registry"];
                // Pin the redirect to the reference page on whatever port this stack runs on (7443 unless
                // PROTOSTAR_API_PORT moved it); some Scalar versions otherwise send the origin.
                var request = context.Request;
                flow.RedirectUri = $"{request.Scheme}://{request.Host}{request.PathBase}/scalar/v1";
            });
    });
}

app.MapAuthEndpoints();
app.MapLoginEndpoints();
app.MapSkillEndpoints();
app.MapSessionEndpoints();
app.MapProjectEndpoints();
app.MapMemoryEndpoints();
app.MapSearchEndpoints();

// API-contract version surface. The CLI checks `apiMajors` on connect to decide compatibility.
app.MapGet("/v1/meta", () => new ApiMeta("protostar-registry", ApiInfo.Version, ApiInfo.ApiMajors))
    .WithName("GetMeta")
    .WithTags(ApiTags.Meta)
    .WithSummary("Registry identity and API-compatibility surface")
    .WithDescription("Public. Clients call this first and stop with an upgrade hint when their API major is not listed.")
    .Produces<ApiMeta>();

app.Run();

// Exposed for WebApplicationFactory in the integration tests.
public partial class Program;
