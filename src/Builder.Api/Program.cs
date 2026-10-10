using System.Text;
using System.Text.Json.Serialization;
using Builder.Api.Auth;
using Builder.Api.Endpoints;
using Builder.Api.Hubs;
using Builder.Api.Workers;
using Builder.Application;
using Builder.Application.Abstractions;
using Builder.Application.Dtos;
using Builder.Application.Services;
using Builder.Contracts;
using Builder.Domain;
using Builder.Infrastructure;
using Builder.Migrations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddApplication().AddInfrastructure(config);
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSignalR(o =>
    {
        o.MaximumParallelInvocationsPerClient = 4;
        o.MaximumReceiveMessageSize = 1024 * 1024;
    })
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddSingleton<AgentConnections>();
builder.Services.AddSingleton<IAgentGateway, SignalRAgentGateway>();
builder.Services.AddSingleton<IUiNotifier, SignalRUiNotifier>();
builder.Services.AddHostedService<PlannerWorker>();
builder.Services.AddHostedService<SchedulerWorker>();
builder.Services.AddHostedService<AgentMonitorWorker>();
builder.Services.AddHostedService<ScheduleWorker>();
builder.Services.AddSingleton(new BuilderLinks { PublicUrl = config["Builder:PublicUrl"] });

// --- auth: users arrive through the BFF with a short-lived JWT; agents use the shared agent token ---
var jwt = config.GetSection("Auth:Jwt");
var signingKey = jwt["SigningKey"] is { Length: >= 32 } key
    ? key : throw new InvalidOperationException("Auth:Jwt:SigningKey must be at least 32 characters.");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt["Issuer"] ?? "builder-bff",
            ValidAudience = jwt["Audience"] ?? "builder-api",
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            NameClaimType = "sub",
            RoleClaimType = "role",
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    })
    .AddScheme<AgentTokenOptions, AgentTokenHandler>(AuthSchemes.Agent, o =>
        o.Token = config["Agents:Token"] ?? throw new InvalidOperationException("Agents:Token is not configured."));

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthSchemes.UserPolicy, p => p
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser()
        .RequireAssertion(c => !c.User.HasClaim("svc", "bff")))
    .AddPolicy(AuthSchemes.ServicePolicy, p => p
        .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
        .RequireClaim("svc", "bff"))
    .AddPolicy(AuthSchemes.AgentPolicy, p => p
        .AddAuthenticationSchemes(AuthSchemes.Agent)
        .RequireClaim("role", "agent"));

var app = builder.Build();

app.UseExceptionHandler(errors => errors.Run(async context =>
{
    var ex = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, title) = ex switch
    {
        NotFoundException => (StatusCodes.Status404NotFound, ex.Message),
        DomainException or TaskfileException => (StatusCodes.Status409Conflict, ex.Message),
        ForbiddenException => (StatusCodes.Status403Forbidden, ex.Message),
        DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "Someone else changed this at the same time. Try again."),
        BadHttpRequestException bad => (bad.StatusCode, "The request was not valid."),
        ExternalServiceException => (StatusCodes.Status502BadGateway, ex.Message),
        _ => (StatusCodes.Status500InternalServerError, "Unexpected server error."),
    };
    context.Response.StatusCode = status;
    await Results.Problem(title: title, statusCode: status).ExecuteAsync(context);
}));

app.UseAuthentication();
app.UseAuthorization();

app.MapBuilderApi();
app.MapHooks();
app.MapHub<UiHub>("/hubs/ui");
app.MapHub<AgentHub>(AgentHubNames.Route);

await using (var scope = app.Services.CreateAsyncScope())
{
    if (config.GetValue("Database:MigrateOnStartup", true))
    {
        using var migrator = new DatabaseMigrator(config.GetConnectionString("Builder")!);
        await migrator.EnsureDatabaseAsync();
        migrator.Up();
    }
    await scope.ServiceProvider.GetRequiredService<AuthService>().EnsureAdminAsync(
        config["Auth:AdminUser"] ?? "admin",
        config["Auth:AdminPassword"] ?? throw new InvalidOperationException("Auth:AdminPassword is not configured."),
        CancellationToken.None);
    var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
    var adminUser = config["Auth:AdminUser"] ?? "admin";
    if (config.GetValue("Auth:PasswordSignIn", true) && await auth.UsesPasswordAsync(adminUser, "admin", CancellationToken.None))
        app.Logger.LogWarning("The '{Admin}' account still has the default password 'admin'. Change it (user menu → Change password) or set Auth:PasswordSignIn=false.", adminUser);
    await scope.ServiceProvider.GetRequiredService<AuthService>().EnsureAllowedUsersAsync(
        config.GetSection("Auth:AllowedUsers").Get<List<AllowedUser>>() ?? [], CancellationToken.None);
}

app.Run();

public partial class Program;
