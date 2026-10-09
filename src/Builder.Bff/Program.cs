using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Yarp.ReverseProxy.Transforms;

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
builder.Services.AddAuthorization();
builder.Services.AddHttpClient("api", c => c.BaseAddress = new Uri(apiUrl));

builder.Services.AddReverseProxy()
    .LoadFromMemory(
        [
            new() { RouteId = "api", ClusterId = "api", AuthorizationPolicy = "default", Match = new() { Path = "/api/{**rest}" } },
            new() { RouteId = "hub", ClusterId = "api", AuthorizationPolicy = "default", Match = new() { Path = "/hubs/ui/{**rest}" } },
        ],
        [new() { ClusterId = "api", Destinations = new Dictionary<string, Yarp.ReverseProxy.Configuration.DestinationConfig> { ["api"] = new() { Address = apiUrl } } }])
    .AddTransforms(t => t.AddRequestTransform(ctx =>
    {
        ctx.ProxyRequest.Headers.Remove("Cookie");
        ctx.ProxyRequest.Headers.Authorization = new("Bearer", jwt.ForUser(ctx.HttpContext.User));
        return ValueTask.CompletedTask;
    }));

var app = builder.Build();

var uiRoot = Path.GetFullPath(config["Ui:Path"] ?? Path.Combine(builder.Environment.ContentRootPath, "../../ui/dist"));
if (Directory.Exists(uiRoot))
{
    var files = new PhysicalFileProvider(uiRoot);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
}

app.UseAuthentication();
app.UseAuthorization();

// CSRF: a cross-site form can't add custom headers, so every proxied API call must carry X-CSRF.
// Agent endpoints are never reachable through the BFF.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api/agent"))
    {
        ctx.Response.StatusCode = 404;
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
    using var request = new HttpRequestMessage(HttpMethod.Post, "/internal/login") { Content = JsonContent.Create(input) };
    request.Headers.Authorization = new("Bearer", jwt.ForService());
    using var response = await http.CreateClient("api").SendAsync(request, ct);
    if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        return Results.Problem(title: "Wrong user name or password.", statusCode: 401);
    response.EnsureSuccessStatusCode();
    var user = (await response.Content.ReadFromJsonAsync<UserInfo>(ct))!;

    var identity = new ClaimsIdentity(
        [new Claim("sub", user.UserName), new Claim("name", user.DisplayName), new Claim("admin", user.IsAdmin ? "true" : "false")],
        CookieAuthenticationDefaults.AuthenticationScheme, "sub", "role");
    await ctx.SignInAsync(new ClaimsPrincipal(identity));
    return Results.Ok(user);
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
