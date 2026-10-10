using System.Net;
using System.Text;
using Builder.Infrastructure.Services;

namespace Builder.Tests;

/// <summary>The Test button of a Container registry connection: the docker login handshake, against a fake Docker Hub.</summary>
public sealed class RegistryLoginTests
{
    private sealed class FakeHub(string user, string token) : HttpMessageHandler
    {
        public List<string> Calls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add(request.RequestUri!.ToString());
            if (request.RequestUri!.Host == "registry-1.docker.io")
            {
                var challenge = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                challenge.Headers.TryAddWithoutValidation("WWW-Authenticate", "Bearer realm=\"https://auth.docker.io/token\",service=\"registry.docker.io\"");
                return Task.FromResult(challenge);
            }
            var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{token}"));
            var ok = request.RequestUri.ToString() == "https://auth.docker.io/token?service=registry.docker.io"
                && request.Headers.Authorization?.ToString() == expected;
            return Task.FromResult(new HttpResponseMessage(ok ? HttpStatusCode.OK : HttpStatusCode.Unauthorized));
        }
    }

    [Fact]
    public async Task Docker_Hub_accepts_the_right_token_and_refuses_a_wrong_one()
    {
        var hub = new FakeHub("acme", "dckr_pat_good");
        var logins = new RegistryLogins(new HttpClient(hub));

        Assert.Equal("docker.io accepted the login of acme.", await logins.CheckAsync("docker.io", "acme", "dckr_pat_good", default));
        Assert.Equal(["https://registry-1.docker.io/v2/", "https://auth.docker.io/token?service=registry.docker.io"], hub.Calls);

        var refused = await Assert.ThrowsAsync<Builder.Domain.DomainException>(() => logins.CheckAsync("docker.io", "acme", "wrong", default));
        Assert.Contains("refused the login of acme", refused.Message);
    }
}
