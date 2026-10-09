using System.Net;
using System.Text;
using Builder.Application;
using Builder.Application.Abstractions;
using Builder.Infrastructure.Services;
using Builder.Infrastructure.Taskfiles;
using Microsoft.Extensions.Options;

namespace Builder.Tests;

/// <summary>Short-lived credentials: Entra client credentials, ACR exchange, and the Taskfile keys that request them.</summary>
public class CredentialTests
{
    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    }

    [Fact]
    public async Task Entra_client_credentials_are_requested_once_and_cached()
    {
        var stub = new Stub(_ => Json("""{"token_type":"Bearer","expires_in":3599,"access_token":"eyJ.entra.token"}"""));
        var clock = new FixedClock();
        var entra = new EntraTokens(new HttpClient(stub), Options.Create(new AzureOptions()), clock);

        var token = await entra.GetAsync("tenant-1", "client-1", "s3cret", IEntraTokens.AzureDevOpsScope, default);
        var again = await entra.GetAsync("tenant-1", "client-1", "s3cret", IEntraTokens.AzureDevOpsScope, default);

        Assert.Equal("eyJ.entra.token", token.Token);
        Assert.Equal(clock.UtcNow.AddSeconds(3599), token.ExpiresOn);
        Assert.Same(token, again);
        var request = Assert.Single(stub.Requests);
        Assert.Equal("https://login.microsoftonline.com/tenant-1/oauth2/v2.0/token", request.Url);
        Assert.Contains("grant_type=client_credentials", request.Body);
        Assert.Contains("scope=499b84ac-1321-427f-aa17-267ca6975798%2F.default", request.Body);
    }

    [Fact]
    public async Task Entra_refusal_is_a_readable_upstream_error()
    {
        var stub = new Stub(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":"invalid_client","error_description":"AADSTS7000215: Invalid client secret provided.\r\nTrace ID: x"}""", Encoding.UTF8, "application/json"),
        });
        var entra = new EntraTokens(new HttpClient(stub), Options.Create(new AzureOptions()), new FixedClock());
        var ex = await Assert.ThrowsAsync<ExternalServiceException>(() => entra.GetAsync("t", "c-" + Guid.NewGuid(), "bad", IEntraTokens.ArmScope, default));
        Assert.Contains("AADSTS7000215: Invalid client secret provided.", ex.Message);
        Assert.DoesNotContain("Trace ID", ex.Message);
    }

    [Fact]
    public async Task Acr_exchange_returns_a_docker_password_with_its_real_expiry()
    {
        var exp = new DateTimeOffset(2026, 10, 9, 15, 0, 0, TimeSpan.Zero);
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"exp":{{exp.ToUnixTimeSeconds()}}}""")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var refresh = $"eyJhbGciOiJSUzI1NiJ9.{payload}.sig";
        var stub = new Stub(_ => Json($$"""{"refresh_token":"{{refresh}}"}"""));
        var acr = new AcrTokens(new HttpClient(stub), new FixedClock());

        var token = await acr.ExchangeAsync("shop.azurecr.io", "tenant-1", "arm-token", default);

        Assert.Equal(refresh, token.Token);
        Assert.Equal(exp, token.ExpiresOn);
        var request = Assert.Single(stub.Requests);
        Assert.Equal("https://shop.azurecr.io/oauth2/exchange", request.Url);
        Assert.Contains("grant_type=access_token", request.Body);
        Assert.Contains("service=shop.azurecr.io", request.Body);
        Assert.Contains("access_token=arm-token", request.Body);
    }

    [Fact]
    public void Planner_reads_registries_and_azure_artifacts()
    {
        var plan = new TaskfilePlanner().Plan("""
            version: '3'
            tasks:
              push:
                x-azure-artifacts: true
                x-registries:
                  - Shop.AzureCR.io
                  - { registry: other.azurecr.io, connection: azure-prod }
                cmds:
                  - docker push shop.azurecr.io/web:1
            """, "push");
        var job = Assert.Single(plan.Jobs);
        Assert.True(job.AzureArtifacts);
        Assert.Equal([new("shop.azurecr.io", null), new Domain.Builds.RegistrySpec("other.azurecr.io", "azure-prod")], job.Registries);
    }

    [Theory]
    [InlineData("x-registries: shop.azurecr.io", "must be a list")]
    [InlineData("x-registries: [shop.azurecr.io/web]", "registry host")]
    [InlineData("x-registries: [{ connection: x }]", "registry host or")]
    public void Planner_rejects_bad_registries(string line, string error)
    {
        var ex = Assert.Throws<TaskfileException>(() => new TaskfilePlanner().Plan($"version: '3'\ntasks:\n  push:\n    {line}\n    cmds:\n      - echo\n", "push"));
        Assert.Contains(error, ex.Message);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    internal sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Url, string Body)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return respond(request);
        }
    }
}
