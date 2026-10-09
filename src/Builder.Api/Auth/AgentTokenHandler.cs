using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Builder.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Builder.Api.Auth;

public static class AuthSchemes
{
    public const string Agent = "Agent";
    public const string AgentPolicy = "agent";
    public const string UserPolicy = "user";
    public const string ServicePolicy = "service";
}

public sealed class AgentTokenOptions : AuthenticationSchemeOptions
{
    public string Token { get; set; } = "";
}

/// <summary>Authenticates agents by the shared registration token in <see cref="AgentHubNames.TokenHeader"/>.</summary>
public sealed class AgentTokenHandler(IOptionsMonitor<AgentTokenOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AgentTokenOptions>(options, logger, encoder)
{
    /// <summary>
    /// The system token (Agents:Token) registers shared agents; an organization's token registers that
    /// organization's agents (claim "org").
    /// </summary>
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = Request.Headers[AgentHubNames.TokenHeader].ToString();
        if (string.IsNullOrEmpty(presented)) return AuthenticateResult.NoResult();

        var claims = new List<Claim> { new("role", "agent") };
        var isSystem = !string.IsNullOrEmpty(Options.Token) &&
            CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(Options.Token));
        if (!isSystem)
        {
            var orgs = Context.RequestServices.GetRequiredService<Builder.Application.Services.OrganizationService>();
            if (await orgs.OrgOfAgentTokenAsync(presented, Context.RequestAborted) is not { } orgId)
                return AuthenticateResult.Fail("Invalid agent token");
            claims.Add(new Claim("org", orgId.ToString()));
        }
        var identity = new ClaimsIdentity(claims, Scheme.Name, "sub", "role");
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
