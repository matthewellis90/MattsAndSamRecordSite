using System.Net;
using System.Threading.RateLimiting;
using MattsRecords.Models;
using MattsRecords.Services;
using MattsRecords.Controllers;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

if (args is ["--hash-admin-password"])
{
    Environment.ExitCode = AdminAuthentication.WritePasswordHash();
    return;
}

var estimatePressings = args.Length == 4 && args[0] == "--estimate-pressings";
var enrichPressings = estimatePressings || (args.Length is 3 or 4 && args[0] == "--enrich-pressings");
var builder = WebApplication.CreateBuilder(enrichPressings ? [] : args);
var development = builder.Environment.IsDevelopment();
const string lookupUserAgent = "TheRecordRoom/2.1 (https://github.com/matthewellis90/RecordRoomEllisCasa)";

builder.Services.AddControllersWithViews(options =>
    options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<CollectionDatabases>();
builder.Services.AddScoped<ICollectionAccounts, CollectionAccounts>();
builder.Services.AddScoped<IAccountRecovery, AccountRecovery>();
builder.Services.AddScoped<IAccountEmail, AccountEmail>();
builder.Services.AddScoped<ICollectionSharing, CollectionSharing>();
builder.Services.AddScoped<PressingBatch>();
builder.Services.Configure<AccountMailOptions>(builder.Configuration.GetSection("AccountMail"));
builder.Services.AddSingleton<IAccountMail, SmtpAccountMail>();
builder.Services.AddHostedService<RecoveryMailWorker>();
builder.Services.AddDbContext<RecordContext>((services, options) => options.UseSqlServer(
    services.GetRequiredService<CollectionDatabases>().ForRequest(services.GetRequiredService<IHttpContextAccessor>().HttpContext?.User)));
builder.Services.AddScoped<RecordRepository>();
var musicBrainzClient = builder.Services.AddHttpClient<PressingLookup>(client =>
{
    client.BaseAddress = new Uri("https://musicbrainz.org/ws/2/");
    client.Timeout = enrichPressings ? TimeSpan.FromMinutes(3) : TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(lookupUserAgent);
});
if (enrichPressings)
    musicBrainzClient.AddHttpMessageHandler(() => new MusicBrainzBatchRetry());
builder.Services.AddHttpClient<DiscogsLookup>(client =>
{
    client.BaseAddress = new Uri("https://api.discogs.com/");
    client.Timeout = TimeSpan.FromSeconds(8);
    client.DefaultRequestHeaders.UserAgent.ParseAdd(lookupUserAgent);
    if (!string.IsNullOrWhiteSpace(builder.Configuration["Discogs:Token"]))
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Discogs token=" + builder.Configuration["Discogs:Token"]);
});

builder.Services.Configure<AdminCredentials>(builder.Configuration.GetSection("Admin"));
builder.Services.Configure<PasswordHasherOptions>(options =>
    options.IterationCount = AdminAuthentication.HashIterations);
builder.Services.AddSingleton<IPasswordHasher<AdminCredentials>, PasswordHasher<AdminCredentials>>();
builder.Services.AddSingleton<IPasswordHasher<CollectionAccount>, PasswordHasher<CollectionAccount>>();
builder.Services.AddSingleton<AdminAuthentication>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = development ? "MattsRecords" : "__Host-MattsRecords";
        options.Cookie.Path = "/";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        options.LoginPath = "/Admin/Login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = false;
        options.Events.OnValidatePrincipal = AdminAuthentication.ValidateCookieAsync;
    });
builder.Services.AddAuthorization(options => options.AddPolicy(RecoveryController.OwnerPolicy, policy =>
    policy.RequireAuthenticatedUser().RequireAssertion(context => !CollectionSession.IsPersonal(context.User))));
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = development ? "MattsRecords.Antiforgery" : "__Host-MattsRecords.Antiforgery";
    options.Cookie.Path = "/";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});

var dataProtection = builder.Services.AddDataProtection().SetApplicationName("MattsRecords");
var keysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(keysPath))
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
    if (OperatingSystem.IsWindows())
        dataProtection.ProtectKeysWithDpapi();
}

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardedForHeaderName = "CF-Connecting-IP";
    options.ForwardLimit = 1;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
});
builder.Services.AddHttpsRedirection(options => options.HttpsPort = 443);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AdminAuthentication.LoginRateLimit, context =>
        RateLimitPartition.GetSlidingWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 8,
                Window = TimeSpan.FromMinutes(5),
                SegmentsPerWindow = 5,
                QueueLimit = 0
            }));
    options.AddPolicy(AdminController.RegistrationRateLimit, context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 4, Window = TimeSpan.FromHours(1), QueueLimit = 0 }));
    options.AddPolicy(RecoveryController.RecoveryRateLimit, context =>
        RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 6, Window = TimeSpan.FromMinutes(10), QueueLimit = 0 }));
});
builder.Services.AddHealthChecks()
    .AddCheck<CatalogHealthCheck>("catalog", tags: ["ready"], timeout: TimeSpan.FromSeconds(5));

var app = builder.Build();

if (enrichPressings)
{
    await using var scope = app.Services.CreateAsyncScope();
    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        stop.Cancel();
    };
    var accounts = scope.ServiceProvider.GetRequiredService<ICollectionAccounts>();
    var account = await accounts.FindByNameAsync(args[1], stop.Token);
    if (account is not { State: 1 })
    {
        Console.Error.WriteLine("The specified personal collection could not be found or is not ready.");
        Environment.ExitCode = 1;
        return;
    }

    var databases = scope.ServiceProvider.GetRequiredService<CollectionDatabases>();
    await using var catalog = databases.OpenPersonalCatalog(account.Id);
    await scope.ServiceProvider.GetRequiredService<PressingBatch>().RunAsync(catalog, args[2],
        args.Length == 4 ? args[3] : null, stop.Token, estimatePressings);
    return;
}

app.UseForwardedHeaders();

if (!development)
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
    app.UseWhen(context => !context.Request.Path.StartsWithSegments("/health"),
        branch => branch.UseHttpsRedirection());
}

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' https: data:; frame-src https://open.spotify.com; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    if (context.Request.Path.StartsWithSegments("/shared"))
    {
        headers["Referrer-Policy"] = "no-referrer";
        headers["X-Robots-Tag"] = "noindex, nofollow";
        headers.CacheControl = "private, no-store";
    }
    await next();
});

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.User.Identity?.IsAuthenticated == true || context.Request.Path.StartsWithSegments("/Admin"))
        context.Response.Headers.CacheControl = "no-store";
    if (CollectionSession.IsPersonal(context.User) && !CollectionSession.IsReady(context.User) &&
        context.Request.Path != "/Admin/Preparing" && context.Request.Path != "/Admin/Logout" &&
        !context.Request.Path.StartsWithSegments("/Account") && !context.Request.Path.StartsWithSegments("/shared"))
    {
        context.Response.Redirect("/Admin/Preparing");
        return;
    }
    await next();
});
app.UseAuthorization();

app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});
app.MapDefaultControllerRoute();
app.Run();

internal sealed class CatalogHealthCheck(RecordContext context, ICollectionAccounts accounts) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext check, CancellationToken cancellationToken)
    {
        await context.Artists.AsNoTracking().OrderBy(artist => artist.Id).Take(1).ToListAsync(cancellationToken);
        await context.Albums.AsNoTracking().OrderBy(record => record.Id).Take(1).ToListAsync(cancellationToken);
        await context.SleevePhotos.AsNoTracking().Select(photo => photo.RecordId).AnyAsync(cancellationToken);
        await accounts.FindByIdAsync(Guid.Empty, cancellationToken);
        return HealthCheckResult.Healthy();
    }
}
