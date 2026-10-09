using Builder.Agent;
using Builder.Contracts;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddEnvironmentVariables("BUILDER_AGENT_");
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection("Agent"));
builder.Services.AddHttpClient("server", (sp, c) =>
{
    var o = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
    c.BaseAddress = new Uri(o.ServerUrl.TrimEnd('/') + "/");
    c.DefaultRequestHeaders.Add(AgentHubNames.TokenHeader, o.Token);
    c.Timeout = TimeSpan.FromMinutes(30);
});
builder.Services.AddHostedService<AgentWorker>();

builder.Build().Run();
