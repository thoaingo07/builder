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
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = Request.Headers[AgentHubNames.TokenHeader].ToString();
        if (string.IsNullOrEmpty(presented)) return Task.FromResult(AuthenticateResult.NoResult());
        if (string.IsNullOrEmpty(Options.Token) ||
            !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(Options.Token)))
            return Task.FromResult(AuthenticateResult.Fail("Invalid agent token"));

        var identity = new ClaimsIdentity([new Claim("role", "agent")], Scheme.Name, "sub", "role");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
