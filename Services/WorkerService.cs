using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Worker.Models;

namespace Worker.Services;

public class WorkerService : BackgroundService
{
    private readonly ILogger<WorkerService> _logger;
    private readonly WorkerConfig _config;
    private readonly HttpClient _httpClient;
    private readonly WhisperProcessor _whisperProcessor;
    private readonly FileDownloader _fileDownloader;

    private HubConnection? _hubConnection;
    private WorkerState _state = WorkerState.Starting;
    private string? _currentTaskId;

    public WorkerService(
        ILogger<WorkerService> logger,
        IOptions<WorkerConfig> config,
        HttpClient httpClient)
    {
        _logger = logger;
        _config = config.Value;
        _httpClient = httpClient;
        _whisperProcessor = new WhisperProcessor(_config.ModelPath);
        _fileDownloader = new FileDownloader(_httpClient, _config.TempPath);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker {Name} starting...", _config.Name);

        try
        {
            // Startup checks
            if (!await PerformStartupChecksAsync(stoppingToken))
            {
                _state = WorkerState.Error;
                _logger.LogCritical("Startup checks failed. Worker cannot start.");
                return;
            }

            // Connect to API
            if (!await ConnectToApiAsync(stoppingToken))
            {
                _state = WorkerState.Error;
                _logger.LogCritical("Failed to connect to API. Worker cannot start.");
                return;
            }

            // Register with API (SignalR handshake)
            await RegisterAsync(stoppingToken);

            // Set state to idle
            _state = WorkerState.Idle;
            _logger.LogInformation("Worker {Name} is now IDLE and ready for tasks", _config.Name);

            // Heartbeat loop
            await RunHeartbeatLoopAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Worker shutting down...");
        }
        catch (Exception ex)
        {
            _state = WorkerState.Error;
            _logger.LogCritical(ex, "Fatal error in worker");
        }
        finally
        {
            if (_hubConnection is not null)
            {
                await _hubConnection.DisposeAsync();
            }
            _whisperProcessor.Dispose();
        }
    }

    private async Task<bool> PerformStartupChecksAsync(CancellationToken ct)
    {
        _logger.LogInformation("Performing startup checks...");

        // Ensure model path exists
        if (!Directory.Exists(_config.ModelPath))
        {
            _logger.LogInformation("Model path does not exist, creating: {Path}", _config.ModelPath);
            try
            {
                Directory.CreateDirectory(_config.ModelPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create model path: {Path}", _config.ModelPath);
                return false;
            }
        }

        // Check temp path is writable
        Directory.CreateDirectory(_config.TempPath);
        var testFile = Path.Combine(_config.TempPath, ".write-test");
        try
        {
            await File.WriteAllTextAsync(testFile, "test", ct);
            File.Delete(testFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Temp path is not writable: {Path}", _config.TempPath);
            return false;
        }

        // Check token is configured
        if (string.IsNullOrEmpty(_config.Token))
        {
            _logger.LogError("WORKER_TOKEN is not configured. Create a worker in the admin panel to get a token.");
            return false;
        }

        if (!_config.Token.StartsWith("sk-worker-"))
        {
            _logger.LogError("Invalid token format. Token must start with 'sk-worker-'");
            return false;
        }

        // Check API URL is configured
        if (string.IsNullOrEmpty(_config.ApiUrl))
        {
            _logger.LogError("API URL is not configured");
            return false;
        }

        _logger.LogInformation("All startup checks passed");
        return true;
    }

    private async Task<bool> ConnectToApiAsync(CancellationToken ct)
    {
        var hubUrl = $"{_config.ApiUrl.TrimEnd('/')}/ws/worker";
        _logger.LogInformation("Connecting to API at {Url}...", hubUrl);

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(_config.Token);
            })
            .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) })
            .Build();

        // Setup handlers
        _hubConnection.On<string>("Registered", OnRegistered);
        _hubConnection.On<JsonElement>("ReceiveTask", OnReceiveTask);

        _hubConnection.Closed += async (error) =>
        {
            _logger.LogWarning(error, "Connection closed");
            _state = WorkerState.Error;
        };

        _hubConnection.Reconnecting += (error) =>
        {
            _logger.LogWarning(error, "Connection lost, reconnecting...");
            return Task.CompletedTask;
        };

        _hubConnection.Reconnected += async (connectionId) =>
        {
            _logger.LogInformation("Reconnected with connection ID: {Id}", connectionId);
            await RegisterAsync(ct);
            _state = _currentTaskId is null ? WorkerState.Idle : WorkerState.Busy;
        };

        try
        {
            await _hubConnection.StartAsync(ct);
            _logger.LogInformation("Connected to API with connection ID: {Id}", _hubConnection.ConnectionId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to API");
            return false;
        }
    }

    private async Task RegisterAsync(CancellationToken ct)
    {
        _logger.LogInformation("Registering worker with API...");

        await _hubConnection!.InvokeAsync("Register", new
        {
            name = _config.Name,
            capabilities = new
            {
                tasks = _config.SupportedTasks,
                formats = _config.SupportedFormats,
                languages = _config.SupportedLanguages,
                models = _whisperProcessor.GetAvailableModels().ToList()
            }
        }, ct);
    }

    private void OnRegistered(string workerId)
    {
        _logger.LogInformation("Worker registered with ID: {Id}", workerId);
    }

    private async void OnReceiveTask(JsonElement taskElement)
    {
        if (_state == WorkerState.Busy)
        {
            _logger.LogWarning("Received task while busy, rejecting");
            await SendErrorAsync(_currentTaskId ?? "unknown", "Worker is busy");
            return;
        }

        string? taskId = null;
        string? filePath = null;

        try
        {
            var taskData = taskElement.GetProperty("data");
            taskId = taskData.GetProperty("task_id").GetString()!;
            _currentTaskId = taskId;
            _state = WorkerState.Busy;

            _logger.LogInformation("Received task: {TaskId}", taskId);

            // Accept task
            await _hubConnection!.InvokeAsync("TaskAccepted", Guid.Parse(taskId));

            // Extract task parameters
            var fileInfo = taskData.GetProperty("file");
            var downloadUrl = fileInfo.GetProperty("download_url").GetString()!;
            var checksum = fileInfo.TryGetProperty("checksum", out var checksumProp)
                ? checksumProp.GetString() ?? ""
                : "";

            var paramsObj = taskData.GetProperty("params");
            var taskParams = new TaskParams
            {
                Language = paramsObj.TryGetProperty("language", out var lang) ? lang.GetString() ?? "auto" : "auto",
                Model = paramsObj.TryGetProperty("model", out var model) ? model.GetString() ?? _config.DefaultModel : _config.DefaultModel,
                OutputFormat = paramsObj.TryGetProperty("output_format", out var fmt) ? fmt.GetString() ?? "json" : "json",
                Timestamps = paramsObj.TryGetProperty("timestamps", out var ts) && ts.GetBoolean()
            };

            // Download file
            _logger.LogInformation("Downloading file from {Url}", downloadUrl);
            await SendProgressAsync(taskId, 0);
            filePath = await _fileDownloader.DownloadAsync(downloadUrl, checksum, CancellationToken.None);
            _logger.LogInformation("File downloaded to {Path}", filePath);

            // Process with Whisper
            _logger.LogInformation("Starting transcription with model: {Model}, language: {Language}",
                taskParams.Model, taskParams.Language);

            var result = await _whisperProcessor.ProcessAsync(
                filePath,
                taskParams,
                progress => _ = SendProgressAsync(taskId, progress),
                CancellationToken.None);

            // Send result
            await _hubConnection!.InvokeAsync("Result", new { TaskId = Guid.Parse(taskId), Status = "Completed", Output = JsonSerializer.SerializeToDocument(result) });
            _logger.LogInformation("Task {TaskId} completed successfully", taskId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Task {TaskId} failed", taskId);
            if (taskId is not null)
            {
                await SendErrorAsync(taskId, ex.Message);
            }
        }
        finally
        {
            if (filePath is not null)
            {
                _fileDownloader.Cleanup(filePath);
            }
            _currentTaskId = null;
            _state = WorkerState.Idle;
            _logger.LogInformation("Worker is now IDLE");
        }
    }

    private async Task SendProgressAsync(string taskId, int progress)
    {
        try
        {
            await _hubConnection!.InvokeAsync("Progress", new { TaskId = Guid.Parse(taskId), Progress = progress });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send progress update");
        }
    }

    private async Task SendErrorAsync(string taskId, string message)
    {
        try
        {
            await _hubConnection!.InvokeAsync("Error", new { TaskId = Guid.Parse(taskId), Message = message, Fatal = false });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send error");
        }
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_config.HeartbeatIntervalSeconds), ct);

                if (_hubConnection?.State == HubConnectionState.Connected)
                {
                    await _hubConnection.InvokeAsync("Heartbeat", new { State = _state.ToString() }, ct);
                    _logger.LogDebug("Heartbeat sent: {State}", _state);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send heartbeat");
            }
        }
    }
}
