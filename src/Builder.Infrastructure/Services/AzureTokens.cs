using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Builder.Application;
using Builder.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace Builder.Infrastructure.Services;

public sealed class AzureOptions
{
    /// <summary>Entra ID authority host (sovereign clouds: login.microsoftonline.us, login.chinacloudapi.cn).</summary>
    public string Authority { get; set; } = "https://login.microsoftonline.com";
}

/// <summary>Client-credentials flow against Entra ID, with an in-memory cache per (tenant, client, scope).</summary>
public sealed class EntraTokens(HttpClient http, IOptions<AzureOptions> options, IClock clock) : IEntraTokens
{
    private static readonly ConcurrentDictionary<string, AccessToken> Cache = new();

    public async Task<AccessToken> GetAsync(string tenantId, string clientId, string clientSecret, string scope, CancellationToken ct)
    {
        var key = $"{tenantId}|{clientId}|{scope}|{clientSecret.GetHashCode()}";
        if (Cache.TryGetValue(key, out var cached) && cached.ExpiresOn - clock.UtcNow > TimeSpan.FromMinutes(5)) return cached;

        using var res = await http.PostAsync($"{options.Value.Authority.TrimEnd('/')}/{Uri.EscapeDataString(tenantId)}/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["scope"] = scope,
            }), ct);
        if (!res.IsSuccessStatusCode)
        {
            var error = await res.Content.ReadFromJsonAsync<EntraError>(ct).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null);
            throw new ExternalServiceException($"Entra ID refused the service principal: {Short(error?.ErrorDescription) ?? res.ReasonPhrase}");
        }
        var body = await res.Content.ReadFromJsonAsync<EntraToken>(ct) ?? throw new ExternalServiceException("Empty token response from Entra ID.");
        var token = new AccessToken(body.AccessToken, clock.UtcNow.AddSeconds(body.ExpiresIn));
        Cache[key] = token;
        return token;
    }

    /// <summary>The first sentence(s) of an Entra error, without its trace/correlation ids and timestamp.</summary>
    private static string? Short(string? description) =>
        description?.Split('\n')[0].Split(" Trace ID:")[0].Trim();

    private sealed record EntraToken([property: JsonPropertyName("access_token")] string AccessToken, [property: JsonPropertyName("expires_in")] int ExpiresIn);
    private sealed record EntraError([property: JsonPropertyName("error_description")] string? ErrorDescription);
}

public sealed class AcrTokens(HttpClient http, IClock clock) : IAcrTokens
{
    public async Task<AccessToken> ExchangeAsync(string registry, string tenantId, string armToken, CancellationToken ct)
    {
        using var res = await http.PostAsync($"https://{registry}/oauth2/exchange", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "access_token",
            ["service"] = registry,
            ["tenant"] = tenantId,
            ["access_token"] = armToken,
        }), ct);
        if (!res.IsSuccessStatusCode)
            throw new ExternalServiceException($"{registry} refused the token exchange ({(int)res.StatusCode}). Give the service principal the AcrPush (or AcrPull) role on the registry.");
        var body = await res.Content.ReadFromJsonAsync<Exchange>(ct) ?? throw new ExternalServiceException("Empty response from the registry.");
        return new AccessToken(body.RefreshToken, ExpiryOf(body.RefreshToken) ?? clock.UtcNow.AddHours(3));
    }

    /// <summary>ACR refresh tokens are JWTs; their exp claim says how long docker login stays valid.</summary>
    private static DateTimeOffset? ExpiryOf(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            return doc.RootElement.TryGetProperty("exp", out var exp) ? DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()) : null;
        }
        catch (FormatException) { return null; }
        catch (JsonException) { return null; }
    }

    private sealed record Exchange([property: JsonPropertyName("refresh_token")] string RefreshToken);
}
