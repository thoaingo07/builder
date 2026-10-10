using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Yarp.ReverseProxy.Transforms;

const string ExternalScheme = "external";

// Backend-for-frontend: the browser holds only an HttpOnly session cookie. The BFF proxies
// /api/* and /hubs/ui to the API and attaches a short-lived JWT minted from the cookie.
var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

var apiUrl = config["Api:Url"] ?? "http://localhost:19100";
var jwt = new JwtMinter(config);

builder.Services.AddDataProtection()
    .SetApplicationName("Builder.Bff")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(config["DataDirectory"] ?? "data/bff")));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "builder.session";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.ExpireTimeSpan = TimeSpan.FromHours(12);
        o.SlidingExpiration = true;
        // an SPA wants status codes, not redirects
        o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
    });
// Google sign-in (optional): Google signs in to a short-lived "external" cookie; /bff/login/google/done then
// asks the API whether that verified e-mail belongs to a predefined user before issuing the real session.
var googleClientId = config["Auth:Google:ClientId"];
var googleEnabled = !string.IsNullOrWhiteSpace(googleClientId) && !string.IsNullOrWhiteSpace(config["Auth:Google:ClientSecret"]);
// Auth:PasswordSignIn=false turns user name / password sign-in off (e.g. once Google sign-in works)
var passwordEnabled = config.GetValue("Auth:PasswordSignIn", true);
if (googleEnabled)
{
    builder.Services.AddAuthentication()
        .AddCookie(ExternalScheme, o =>
        {
            o.Cookie.Name = "builder.external";
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.HttpOnly = true;
            o.ExpireTimeSpan = TimeSpan.FromMinutes(5);
        })
        .AddGoogle(o =>
        {
            o.SignInScheme = ExternalScheme;
            o.ClientId = googleClientId!;
            o.ClientSecret = config["Auth:Google:ClientSecret"]!;
            o.CallbackPath = "/signin-google";
            o.ClaimActions.MapJsonKey("email_verified", "email_verified");
            o.Events.OnRemoteFailure = c =>
            {
                c.Response.Redirect("/login?error=google_failed");
                c.HandleResponse();
                return Task.CompletedTask;
            };
        });
}
builder.Services.AddAuthorization();
builder.Services.AddHttpClient("api", c => c.BaseAddress = new Uri(apiUrl));

builder.Services.AddReverseProxy()
    .LoadFromMemory(
        [
            new() { RouteId = "api", ClusterId = "api", AuthorizationPolicy = "default", Match = new() { Path = "/api/{**rest}" } },
            new() { RouteId = "hub", ClusterId = "api", AuthorizationPolicy = "default", Match = new() { Path = "/hubs/ui/{**rest}" } },
            // daemons reach the API through the same public endpoint; they authenticate with their agent token at the API
            new() { RouteId = "agent-hub", ClusterId = "api", AuthorizationPolicy = "anonymous", Match = new() { Path = "/hubs/agent/{**rest}" } },
            new() { RouteId = "agent-api", ClusterId = "api", AuthorizationPolicy = "anonymous", Match = new() { Path = "/api/agent/{**rest}" } },
            // git host webhooks (authenticated by the repository's webhook secret at the API)
            new() { RouteId = "agent-hooks", ClusterId = "api", AuthorizationPolicy = "anonymous", Match = new() { Path = "/hooks/{**rest}" } },
        ],
        [new() { ClusterId = "api", Destinations = new Dictionary<string, Yarp.ReverseProxy.Configuration.DestinationConfig> { ["api"] = new() { Address = apiUrl } } }])
    .AddTransforms(t => t.AddRequestTransform(ctx =>
    {
        ctx.ProxyRequest.Headers.Remove("Cookie");
        // user routes: swap the session cookie for a short-lived JWT; agent routes pass X-Agent-Token through untouched
        if (!t.Route.RouteId.StartsWith("agent-", StringComparison.Ordinal))
            ctx.ProxyRequest.Headers.Authorization = new("Bearer", jwt.ForUser(ctx.HttpContext.User));
        return ValueTask.CompletedTask;
    }));

var app = builder.Build();

// Behind a TLS proxy (tailscale serve, Caddy, ...) the BFF must build URLs - the Google redirect URI, Secure
// cookies - for the address users see, not the local one. Auth:PublicOrigin = https://host:port.
if (config["Auth:PublicOrigin"] is { Length: > 0 } publicOrigin)
{
    var origin = new Uri(publicOrigin);
    app.Use((ctx, next) =>
    {
        ctx.Request.Scheme = origin.Scheme;
        ctx.Request.Host = new HostString(origin.Authority);
        return next();
    });
}

var uiRoot = Path.GetFullPath(config["Ui:Path"] ?? Path.Combine(builder.Environment.ContentRootPath, "../../ui/dist"));
if (Directory.Exists(uiRoot))
{
    var files = new PhysicalFileProvider(uiRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    var types = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
    types.Mappings[".md"] = "text/markdown; charset=utf-8"; // /runner-guide.md (public, read by coding agents)
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ContentTypeProvider = types });
}

app.UseAuthentication();
app.UseAuthorization();

// CSRF: a cross-site form can't add custom headers, so every proxied API call must carry X-CSRF.
app.Use(async (ctx, next) =>
{
    // agents are not browsers: no cookie, no CSRF header (their token is checked by the API)
    if (ctx.Request.Path.StartsWithSegments("/api/agent") || ctx.Request.Path.StartsWithSegments("/hubs/agent"))
    {
        ctx.Request.Headers.Remove("Cookie");
        await next();
        return;
    }
    if ((ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/bff"))
        && !HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Headers["X-CSRF"] != "1")
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { title = "Missing X-CSRF header.", status = 400 });
        return;
    }
    await next();
});

app.MapPost("/bff/login", async (LoginRequest input, HttpContext ctx, IHttpClientFactory http, CancellationToken ct) =>
{
    if (!passwordEnabled) return Results.Problem(title: "Password sign-in is turned off.", statusCode: 404);
    using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/login") { Content = JsonContent.Create(input) };
    request.Headers.Authorization = new("Bearer", jwt.ForService());
    using var response = await http.CreateClient("api").SendAsync(request, ct);
    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        return Results.Problem(title: "Wrong user name or password.", statusCode: 401);
    response.EnsureSuccessStatusCode();
    var user = (await response.Content.ReadFromJsonAsync<UserInfo>(ct))!;
    await SignInAsync(ctx, user);
    return Results.Ok(user);
});

app.MapGet("/bff/providers", () => Results.Ok(new { password = passwordEnabled, google = googleEnabled }));

app.MapGet("/bff/login/google", (string? returnUrl) => googleEnabled
    ? Results.Challenge(new AuthenticationProperties { RedirectUri = "/bff/login/google/done?returnUrl=" + Uri.EscapeDataString(LocalUrl(returnUrl)) }, ["Google"])
    : Results.NotFound());

app.MapGet("/bff/login/google/done", async (string? returnUrl, HttpContext ctx, IHttpClientFactory http, CancellationToken ct) =>
{
    var external = await ctx.AuthenticateAsync(ExternalScheme);
    await ctx.SignOutAsync(ExternalScheme);
    if (!external.Succeeded) return Results.Redirect("/login?error=google_failed");

    var p = external.Principal!;
    var email = p.FindFirstValue(ClaimTypes.Email) ?? "";
    var verified = bool.TryParse(p.FindFirstValue("email_verified"), out var v) && v;
    using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/external-login")
    {
        Content = JsonContent.Create(new { provider = "google", email, emailVerified = verified, displayName = p.FindFirstValue(ClaimTypes.Name) }),
    };
    request.Headers.Authorization = new("Bearer", jwt.ForService());
    using var response = await http.CreateClient("api").SendAsync(request, ct);
    if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
    {
        app.Logger.LogWarning("Google sign-in refused for {Email} (verified={Verified}): not a predefined user", email, verified);
        return Results.Redirect("/login?error=not_allowed");
    }
    response.EnsureSuccessStatusCode();
    var user = (await response.Content.ReadFromJsonAsync<UserInfo>(ct))!;
    await SignInAsync(ctx, user);
    return Results.Redirect(LocalUrl(returnUrl));
});

app.MapPost("/bff/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync();
    return Results.NoContent();
});

app.MapGet("/bff/user", (ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true
    ? Results.Ok(new UserInfo(user.FindFirstValue("sub")!, user.FindFirstValue("name") ?? "", user.FindFirstValue("admin") == "true"))
    : Results.Unauthorized());

app.MapReverseProxy();

// SPA fallback for client-side routes
if (Directory.Exists(uiRoot))
    app.MapFallback(async ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/hubs") || ctx.Request.Path.StartsWithSegments("/bff"))
        {
            ctx.Response.StatusCode = 404;
            return;
        }
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache";
        await ctx.Response.SendFileAsync(Path.Combine(uiRoot, "index.html"));
    });

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

app.Run();

static Task SignInAsync(HttpContext ctx, UserInfo user) => ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
    new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim("sub", user.UserName), new Claim("name", user.DisplayName), new Claim("admin", user.IsAdmin ? "true" : "false")],
        CookieAuthenticationDefaults.AuthenticationScheme, "sub", "role")));

// Only same-site paths: never redirect to another origin after sign-in.
static string LocalUrl(string? url) =>
    !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : "/";

internal sealed record LoginRequest(string UserName, string Password);
internal sealed record UserInfo(string UserName, string DisplayName, bool IsAdmin);

/// <summary>Mints the 5-minute JWTs the API trusts (issuer = this BFF, shared signing key).</summary>
internal sealed class JwtMinter(IConfiguration config)
{
    private readonly JsonWebTokenHandler _handler = new();
    private readonly SigningCredentials _credentials = new(
        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Auth:Jwt:SigningKey"] is { Length: >= 32 } k
            ? k : throw new InvalidOperationException("Auth:Jwt:SigningKey must be at least 32 characters."))),
        SecurityAlgorithms.HmacSha256);

    public string ForUser(ClaimsPrincipal user)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = user.FindFirstValue("sub") ?? "",
            ["name"] = user.FindFirstValue("name") ?? "",
        };
        if (user.FindFirstValue("admin") == "true") claims["role"] = "admin";
        return Mint(claims);
    }

    public string ForService() => Mint(new Dictionary<string, object> { ["sub"] = "bff", ["svc"] = "bff" });

    private string Mint(Dictionary<string, object> claims) => _handler.CreateToken(new SecurityTokenDescriptor
    {
        Issuer = config["Auth:Jwt:Issuer"] ?? "builder-bff",
        Audience = config["Auth:Jwt:Audience"] ?? "builder-api",
        Claims = claims,
        Expires = DateTime.UtcNow.AddMinutes(5),
        SigningCredentials = _credentials,
    });
}

public partial class Program;
