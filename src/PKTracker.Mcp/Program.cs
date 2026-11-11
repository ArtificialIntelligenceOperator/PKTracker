using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PKTracker.Mcp;

// MCP-сервер PKTracker (stdio). stdout занят протоколом — все логи только в stderr.
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services.AddSingleton(McpOptions.FromArgs(args));
builder.Services.AddSingleton<TrackerContext>();
builder.Services.AddHostedService<GgCoexistenceWatch>();
builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "pktracker", Version = typeof(TrackerContext).Assembly.GetName().Version?.ToString() ?? "1.0" };
        o.ServerInstructions = TrackerContext.Instructions;
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly()
    .WithPromptsFromAssembly()
    .WithResourcesFromAssembly();

await builder.Build().RunAsync();
