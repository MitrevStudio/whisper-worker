using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Worker.Models;
using Worker.Services;

var builder = Host.CreateApplicationBuilder(args);

// Load configuration from environment variables with WORKER_ prefix
// This allows WORKER_TOKEN, WORKER_APIURL, etc.
builder.Configuration.AddEnvironmentVariables("WORKER_");

// Also map WORKER_TOKEN directly to Worker:Token for convenience
var workerToken = Environment.GetEnvironmentVariable("WORKER_TOKEN");
if (!string.IsNullOrEmpty(workerToken))
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Worker:Token"] = workerToken
    });
}

// Parse comma-separated SupportedModels from environment variable
// This allows WORKER__SupportedModels: "turbo,base" instead of indexed format
var supportedModels = builder.Configuration.GetValue<string>("Worker:SupportedModels");
if (!string.IsNullOrEmpty(supportedModels) && !supportedModels.Contains("System.Collections"))
{
    var models = supportedModels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var modelConfig = new Dictionary<string, string?>();
    for (int i = 0; i < models.Length; i++)
    {
        modelConfig[$"Worker:SupportedModels:{i}"] = models[i];
    }
    builder.Configuration.AddInMemoryCollection(modelConfig);
}

builder.Services.Configure<WorkerConfig>(builder.Configuration.GetSection("Worker"));

// Register HttpClient with extended timeout for large file downloads
builder.Services.AddHttpClient<WorkerService>(client =>
{
    client.Timeout = TimeSpan.FromHours(2); // Large files need extended timeout
});

// Register worker service
builder.Services.AddHostedService<WorkerService>();

var host = builder.Build();

await host.RunAsync();
