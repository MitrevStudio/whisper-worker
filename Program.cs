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

builder.Services.Configure<WorkerConfig>(builder.Configuration.GetSection("Worker"));

// Register HttpClient
builder.Services.AddHttpClient<WorkerService>();

// Register worker service
builder.Services.AddHostedService<WorkerService>();

var host = builder.Build();

await host.RunAsync();
