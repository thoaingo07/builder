using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Testing;

namespace Builder.Tests;

/// <summary>Helpers for end-to-end tests that talk to the stack through the BFF, like the UI does.</summary>
internal static class E2E
{
    public static async Task<HttpClient> LoginAsync(AspireFixture aspire)
    {
        var http = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() })
        {
            BaseAddress = aspire.App.GetEndpoint("bff", "http"),
        };
        http.DefaultRequestHeaders.Add("X-CSRF", "1");
        await WaitUntilAsync(async () =>
            (await http.PostAsJsonAsync("/bff/login", new { userName = "admin", password = "admin" })).IsSuccessStatusCode,
            "login");
        return http;
    }

    public static async Task<JsonNode> PostAsync(HttpClient http, string url, object body)
    {
        var response = await http.PostAsJsonAsync(url, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"POST {url} → {(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!;
    }

    public static async Task<JsonNode> GetAsync(HttpClient http, string url) => (await http.GetFromJsonAsync<JsonNode>(url))!;

    public static string? StatusOf(JsonNode build, string key) =>
        build["jobs"]!.AsArray().FirstOrDefault(j => j!["key"]!.GetValue<string>() == key)?["status"]!.GetValue<string>();

    public static JsonNode Job(JsonNode build, string key) =>
        build["jobs"]!.AsArray().Single(j => j!["key"]!.GetValue<string>() == key)!;

    /// <summary>Queues a build and waits until it finishes; returns the final build.</summary>
    public static async Task<JsonNode> RunBuildAsync(HttpClient http, string pipelineId, object? input = null, int seconds = 120)
    {
        var build = await PostAsync(http, $"/api/pipelines/{pipelineId}/builds", input ?? new { });
        JsonNode detail = build;
        await WaitUntilAsync(async () =>
        {
            detail = await GetAsync(http, $"/api/builds/{build["id"]}");
            return detail["status"]!.GetValue<string>() is "Succeeded" or "Failed" or "Canceled";
        }, $"build #{build["number"]} to finish", seconds);
        return detail;
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, string what, int seconds = 90)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            try
            {
                if (await condition()) return;
            }
            catch (HttpRequestException) { /* still starting */ }
            catch (JsonException) { }
            await Task.Delay(500);
        }
        Assert.Fail($"Timed out waiting for {what}.");
    }

    /// <summary>Creates a bare git repository with the given files on branch main; returns its path.</summary>
    public static string CreateRepository(string root, IReadOnlyDictionary<string, string> files)
    {
        var bare = Path.Combine(root, "repo.git");
        var work = Path.Combine(root, "work");
        Directory.CreateDirectory(work);
        Git(root, "init", "--quiet", "--bare", bare);
        Git(work, "init", "--quiet", "-b", "main");
        foreach (var (path, content) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(work, path))!);
            File.WriteAllText(Path.Combine(work, path), content);
        }
        Git(work, "add", "-A");
        Git(work, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "--quiet", "-m", "init");
        Git(work, "push", "--quiet", bare, "HEAD:main");
        return bare;
    }

    private static void Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)}: {p.StandardError.ReadToEnd()}");
    }
}
