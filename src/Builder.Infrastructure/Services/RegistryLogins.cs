using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Builder.Application;
using Builder.Application.Abstractions;

namespace Builder.Infrastructure.Services;

/// <summary>
/// Checks a user name + token against a registry the way docker login does (Docker Registry HTTP API v2):
/// GET /v2/, and on a Bearer challenge ask its token service with basic auth.
/// </summary>
public sealed class RegistryLogins(HttpClient http) : IRegistryLogins
{
    public async Task<string> CheckAsync(string registry, string username, string token, CancellationToken ct)
    {
        var host = registry is "docker.io" ? "registry-1.docker.io" : registry;
        var basic = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{token}")));
        HttpResponseMessage challenge;
        try
        {
            challenge = await http.GetAsync($"https://{host}/v2/", ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ExternalServiceException($"Cannot reach {registry}: {ex.Message}");
        }
        using (challenge)
        {
            var bearer = challenge.Headers.WwwAuthenticate.FirstOrDefault(h => h.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase));
            if (challenge.StatusCode == HttpStatusCode.Unauthorized && bearer?.Parameter is { } p)
            {
                var parts = Parse(p);
                if (!parts.TryGetValue("realm", out var realm)) throw new ExternalServiceException($"{registry} sent a challenge without a realm.");
                var url = parts.TryGetValue("service", out var service) ? $"{realm}?service={Uri.EscapeDataString(service)}" : realm;
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = basic;
                using var res = await http.SendAsync(req, ct);
                return Verdict(registry, username, res.StatusCode);
            }
            using var direct = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/v2/");
            direct.Headers.Authorization = basic;
            using var res2 = await http.SendAsync(direct, ct);
            return Verdict(registry, username, res2.StatusCode);
        }
    }

    private static string Verdict(string registry, string username, HttpStatusCode code) => code switch
    {
        HttpStatusCode.OK => $"{registry} accepted the login of {username}.",
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            throw new Domain.DomainException($"{registry} refused the login of {username}: check the user name and the access token."),
        _ => throw new ExternalServiceException($"{registry} answered {(int)code} to the login."),
    };

    /// <summary>realm="https://auth.docker.io/token",service="registry.docker.io" → dictionary.</summary>
    private static Dictionary<string, string> Parse(string parameter) =>
        System.Text.RegularExpressions.Regex.Matches(parameter, "(\\w+)=\"([^\"]*)\"")
            .ToDictionary(m => m.Groups[1].Value.ToLowerInvariant(), m => m.Groups[2].Value);
}
