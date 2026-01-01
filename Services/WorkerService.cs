using System.Text.Json;
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
    private int _connectionAttempt = 0;

    public WorkerService(
        ILogger<WorkerService> logger,
        IOptions<WorkerConfig> config,
        HttpClient httpClient,
        ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _config = config.Value;
        _httpClient = httpClient;
        _whisperProcessor = new WhisperProcessor(_config.ModelPath, loggerFactory.CreateLogger<WhisperProcessor>());
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

            // Configure HttpClient with auth token
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.Token);

            // Connect to API with infinite retry
            await ConnectWithRetryAsync(stoppingToken);

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

        // Validate and secure model path
        if (!ValidateDirectoryPath(_config.ModelPath, "Model path"))
        {
            return false;
        }

        // Ensure model path exists
        if (!Directory.Exists(_config.ModelPath))
        {
            _logger.LogInformation("Model path does not exist, creating");
            try
            {
                Directory.CreateDirectory(_config.ModelPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create model path");
                return false;
            }
        }

        // Validate and secure temp path
        if (!ValidateDirectoryPath(_config.TempPath, "Temp path"))
        {
            return false;
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
            _logger.LogError(ex, "Temp path is not writable");
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

        // Check and validate API URL
        if (string.IsNullOrEmpty(_config.ApiUrl))
        {
            _logger.LogError("API URL is not configured");
            return false;
        }

        if (!ValidateApiUrl(_config.ApiUrl))
        {
            return false;
        }

        // Validate SupportedModels is configured
        if (_config.SupportedModels == null || _config.SupportedModels.Count == 0)
        {
            _logger.LogError("SupportedModels is not configured. Add models via WORKER__SupportedModels__0, WORKER__SupportedModels__1, etc.");
            return false;
        }

        // Validate DefaultModel is configured and exists in SupportedModels
        if (string.IsNullOrEmpty(_config.DefaultModel))
        {
            _logger.LogError("DefaultModel is not configured. Set via WORKER__DefaultModel.");
            return false;
        }

        if (!_config.SupportedModels.Contains(_config.DefaultModel, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogError("DefaultModel '{DefaultModel}' is not in SupportedModels: [{Models}]",
                _config.DefaultModel, string.Join(", ", _config.SupportedModels));
            return false;
        }

        // Pre-download and load all configured models into memory
        // This ensures fast inference by eliminating first-request latency
        try
        {
            _logger.LogInformation("Pre-loading {Count} configured models: {Models}",
                _config.SupportedModels.Count, string.Join(", ", _config.SupportedModels));

            await _whisperProcessor.PreloadModelsAsync(_config.SupportedModels, ct);

            _logger.LogInformation("Model preloading complete. Worker is ready for fast inference.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to preload models. Worker cannot start without models.");
            return false;
        }

        _logger.LogInformation("All startup checks passed");
        return true;
    }

    private bool ValidateDirectoryPath(string path, string pathName)
    {
        try
        {
            // Ensure path is absolute
            if (!Path.IsPathFullyQualified(path))
            {
                _logger.LogError("{PathName} must be an absolute path", pathName);
                return false;
            }

            // Get full path and check for traversal attempts
            var fullPath = Path.GetFullPath(path);

            // Verify the path is valid (no null characters, etc.)
            if (fullPath.Contains('\0'))
            {
                _logger.LogError("{PathName} contains invalid characters", pathName);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{PathName} validation failed", pathName);
            return false;
        }
    }

    private bool ValidateApiUrl(string apiUrl)
    {
        try
        {
            if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var apiUri))
            {
                _logger.LogError("Invalid API URL format");
                return false;
            }

            if (apiUri.Scheme != Uri.UriSchemeHttps && apiUri.Scheme != Uri.UriSchemeHttp)
            {
                _logger.LogError("API URL must use HTTP or HTTPS scheme");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "API URL validation failed");
            return false;
        }
    }

    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        _connectionAttempt = 0;

        while (!ct.IsCancellationRequested)
        {
            _connectionAttempt++;
            _logger.LogInformation("Connection attempt #{Attempt} to API...", _connectionAttempt);

            try
            {
                if (await ConnectToApiAsync(ct))
                {
                    _logger.LogInformation("Successfully connected to API on attempt #{Attempt}", _connectionAttempt);
                    _connectionAttempt = 0;
                    return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Connection attempt #{Attempt} failed", _connectionAttempt);
            }

            var delaySeconds = Math.Min(30, 5 * _connectionAttempt); // Progressive delay: 5s, 10s, 15s, ... up to 30s
            _logger.LogInformation("Retrying connection in {Delay} seconds (attempt #{Attempt})...", delaySeconds, _connectionAttempt);

            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
        }

        ct.ThrowIfCancellationRequested();
    }

    private async Task<bool> ConnectToApiAsync(CancellationToken ct)
    {
        var hubUrl = $"{_config.ApiUrl.TrimEnd('/')}/ws/worker";
        _logger.LogInformation("Connecting to API at {Url}...", hubUrl);

        // Dispose old connection if exists
        if (_hubConnection is not null)
        {
            await _hubConnection.DisposeAsync();
            _hubConnection = null;
        }

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(hubUrl, options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(_config.Token);
                options.Headers.Add("Authorization", $"Bearer {_config.Token}");
            })
            .WithAutomaticReconnect(new InfiniteRetryPolicy())
            .Build();

        // Setup handlers
        _hubConnection.On<string>("Registered", OnRegistered);
        _hubConnection.On<JsonElement>("ReceiveTask", OnReceiveTaskSafe);
        _hubConnection.On<JsonElement>("ReceiveStreamingTask", OnReceiveStreamingTaskSafe);

        _hubConnection.Closed += OnConnectionClosed;

        _hubConnection.Reconnecting += (error) =>
        {
            _logger.LogWarning(error, "Connection lost, reconnecting...");
            _state = WorkerState.Reconnecting;
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

    private async Task OnConnectionClosed(Exception? error)
    {
        // This is called only when automatic reconnect gives up (never with InfiniteRetryPolicy)
        // or when the connection is explicitly stopped
        _logger.LogError(error, "Connection closed permanently. Worker entering error state.");
        _state = WorkerState.Error;

        // Note: With InfiniteRetryPolicy, this should never be called during normal operation.
        // If we reach here, something is seriously wrong (e.g., token revoked, worker deleted)
    }

    // Custom reconnect policy that retries indefinitely with progressive delays
    private class InfiniteRetryPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] _retryDelays =
        [
            TimeSpan.FromSeconds(0),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(5)
        ];

        public TimeSpan? NextRetryDelay(RetryContext context)
        {
            var delay = _retryDelays[Math.Min(context.PreviousRetryCount, _retryDelays.Length - 1)];
            return delay;
        }
    }

    private async Task RegisterAsync(CancellationToken ct)
    {
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

    private void OnReceiveTaskSafe(JsonElement taskElement)
    {
        // Fire and forget, but wrap to prevent unhandled exceptions from crashing the worker
        _ = HandleTaskSafelyAsync(taskElement);
    }

    private void OnReceiveStreamingTaskSafe(JsonElement taskElement)
    {
        // Fire and forget, but wrap to prevent unhandled exceptions from crashing the worker
        _ = HandleStreamingTaskSafelyAsync(taskElement);
    }

    private async Task HandleStreamingTaskSafelyAsync(JsonElement taskElement)
    {
        string? sessionId = null;
        int sequence = -1;
        string? tempWavPath = null;

        try
        {
            sessionId = taskElement.GetProperty("session_id").GetString()!;
            sequence = taskElement.GetProperty("sequence").GetInt32();
            var audioBase64 = taskElement.GetProperty("audio_data").GetString()!;

            var paramsObj = taskElement.GetProperty("params");
            var language = paramsObj.TryGetProperty("language", out var lang) ? lang.GetString() ?? "auto" : "auto";
            var model = paramsObj.TryGetProperty("model", out var modelProp) ? modelProp.GetString() ?? _config.DefaultModel : _config.DefaultModel;

            _logger.LogInformation("Received streaming task: session {SessionId}, sequence {Sequence}, language {Language}, model {Model}",
                sessionId, sequence, language, model);

            // Mark as busy
            _state = WorkerState.Busy;

            // Decode audio data
            var audioBytes = Convert.FromBase64String(audioBase64);

            // Write to temp file
            tempWavPath = Path.Combine(_config.TempPath, $"streaming_{sessionId}_{sequence}.wav");
            await File.WriteAllBytesAsync(tempWavPath, audioBytes);

            // Validate task parameters
            if (!ValidateLanguage(language, out var validatedLanguage))
            {
                validatedLanguage = "auto";
            }

            if (!ValidateModel(model, out var validatedModel))
            {
                _logger.LogWarning("Model '{Model}' not in supported list, falling back to '{Default}'", model, _config.DefaultModel);
                validatedModel = _config.DefaultModel;
            }

            _logger.LogInformation("Using model: {Model} (original: {Original}, language: {Language})", validatedModel, model, validatedLanguage);

            var taskParams = new TaskParams
            {
                Language = validatedLanguage,
                Model = validatedModel
            };

            // Process with Whisper - send segments immediately as they're produced for real-time display
            var segments = new List<(TimeSpan start, TimeSpan end, string text)>();
            var segmentIndex = 0;

            await _whisperProcessor.ProcessAsync(
                tempWavPath,
                taskParams,
                progress => { }, // No progress for streaming
                async (start, end, text) =>
                {
                    // Store for final result
                    segments.Add((start, end, text));
                    var currentIndex = segmentIndex++;

                    // Send immediately to client for real-time display
                    try
                    {
                        await _hubConnection!.InvokeAsync("StreamingSegment", new
                        {
                            SessionId = Guid.Parse(sessionId!),
                            Sequence = sequence,
                            SegmentIndex = currentIndex,
                            Text = text.Trim(),
                            StartSeconds = start.TotalSeconds,
                            EndSeconds = end.TotalSeconds
                        });
                        _logger.LogInformation("Sent streaming segment {Index} for session {SessionId}: {Text}",
                            currentIndex, sessionId, text.Trim().Length > 30 ? text.Trim()[..30] + "..." : text.Trim());
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to send streaming segment {Index}", currentIndex);
                    }
                },
                CancellationToken.None);

            // Combine all segments into one result
            var fullText = string.Join(" ", segments.Select(s => s.text.Trim())).Trim();
            var startSeconds = segments.Count > 0 ? segments[0].start.TotalSeconds : 0;
            var endSeconds = segments.Count > 0 ? segments[^1].end.TotalSeconds : 0;

            // Send result back to API
            await _hubConnection!.InvokeAsync("StreamingResult", new
            {
                SessionId = Guid.Parse(sessionId),
                Sequence = sequence,
                Text = fullText,
                IsFinal = true, // Each chunk produces a final result
                StartSeconds = startSeconds,
                EndSeconds = endSeconds
            });

            _logger.LogInformation("Streaming task completed: session {SessionId}, sequence {Sequence}, text: {Text}",
                sessionId, sequence, fullText.Length > 50 ? fullText[..50] + "..." : fullText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Streaming task failed: session {SessionId}, sequence {Sequence}", sessionId, sequence);

            // Send empty result on error
            if (sessionId != null && sequence >= 0)
            {
                try
                {
                    await _hubConnection!.InvokeAsync("StreamingResult", new
                    {
                        SessionId = Guid.Parse(sessionId),
                        Sequence = sequence,
                        Text = "",
                        IsFinal = true,
                        StartSeconds = 0.0,
                        EndSeconds = 0.0
                    });
                }
                catch { }
            }
        }
        finally
        {
            // Clean up temp file
            if (tempWavPath != null)
            {
                try { File.Delete(tempWavPath); } catch { }
            }

            _state = WorkerState.Idle;
            _logger.LogDebug("Worker is now IDLE after streaming task");
        }
    }

    private async Task HandleTaskSafelyAsync(JsonElement taskElement)
    {
        if (_state == WorkerState.Busy)
        {
            // Extract task ID from the incoming task to reject it properly
            try
            {
                var taskData = taskElement.GetProperty("data");
                var incomingTaskId = taskData.GetProperty("task_id").GetString()!;
                _logger.LogWarning("Received task {TaskId} while busy, rejecting and returning to queue", incomingTaskId);
                await SendRejectAsync(incomingTaskId, "Worker is busy");
            }
            catch
            {
                _logger.LogWarning("Received task while busy but couldn't parse task ID");
            }
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

            // All tasks are now file-based (API pre-converts everything to WAV chunks)
            var sourceType = taskData.TryGetProperty("source_type", out var sourceTypeProp)
                ? sourceTypeProp.GetString() ?? "file"
                : "file";

            if (sourceType != "file")
            {
                throw new InvalidOperationException($"Unsupported source type: {sourceType}. Worker only accepts pre-converted WAV chunks.");
            }

            var paramsObj = taskData.GetProperty("params");
            var language = paramsObj.TryGetProperty("language", out var lang) ? lang.GetString() ?? "auto" : "auto";
            var model = paramsObj.TryGetProperty("model", out var modelProp) ? modelProp.GetString() ?? _config.DefaultModel : _config.DefaultModel;

            // Validate task parameters
            if (!ValidateLanguage(language, out var validatedLanguage))
            {
                throw new ArgumentException($"Unsupported language: {language}");
            }

            if (!ValidateModel(model, out var validatedModel))
            {
                throw new ArgumentException($"Unsupported model: {model}");
            }

            var taskParams = new TaskParams
            {
                Language = validatedLanguage,
                Model = validatedModel
            };

            // Download WAV chunks from API
            await SendDownloadStartedAsync(taskId);
            //await SendProgressAsync(taskId, 0);

            var fileInfo = taskData.GetProperty("file");
            var chunkBaseUrl = fileInfo.GetProperty("chunk_base_url").GetString()!;
            var chunkCount = fileInfo.GetProperty("chunk_count").GetInt32();
            var checksum = fileInfo.TryGetProperty("checksum", out var checksumProp)
                ? checksumProp.GetString() ?? ""
                : "";

            _logger.LogInformation("Downloading {ChunkCount} WAV chunks for task: {TaskId}", chunkCount, taskId);
            filePath = await _fileDownloader.DownloadChunksAndAssembleAsync(chunkBaseUrl, chunkCount, checksum, CancellationToken.None);

            await SendDownloadCompletedAsync(taskId);

            // Process with Whisper
            _logger.LogInformation("Starting transcription with model: {Model}, language: {Language}",
                taskParams.Model, taskParams.Language);

            await _whisperProcessor.ProcessAsync(
                filePath,
                taskParams,
                progress => _ = SendProgressAsync(taskId, progress),
                async (start, end, text) => await SendSegmentAsync(taskId, start, end, text),
                CancellationToken.None);

            // Send completion (segments already sent via callback, stored in Redis, will be persisted on completion)
            await _hubConnection!.InvokeAsync("Result", new { TaskId = Guid.Parse(taskId) });
            _logger.LogInformation("Task {TaskId} completed successfully", taskId);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse task data");
            if (taskId is not null)
            {
                await SendErrorAsync(taskId, "Invalid task format");
            }
        }
        catch (ArgumentException ex)
        {
            _logger.LogError(ex, "Task {TaskId} has invalid parameters", taskId);
            if (taskId is not null)
            {
                await SendErrorAsync(taskId, "Invalid task parameters");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Task {TaskId} was cancelled", taskId);
            if (taskId is not null)
            {
                await SendErrorAsync(taskId, "Task was cancelled");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Task {TaskId} failed with error", taskId);
            if (taskId is not null)
            {
                await SendErrorAsync(taskId, "Task processing failed");
            }
        }
        finally
        {
            if (filePath is not null)
            {
                // Clean up downloaded file
                _fileDownloader.Cleanup(filePath);
            }
            _currentTaskId = null;
            _state = WorkerState.Idle;
            _logger.LogInformation("Worker is now IDLE");
        }
    }

    private bool ValidateLanguage(string language, out string validatedLanguage)
    {
        validatedLanguage = language;

        // Validate length
        if (string.IsNullOrWhiteSpace(language) || language.Length > 20)
        {
            return false;
        }

        // Must be in supported languages list
        if (!_config.SupportedLanguages.Contains(language))
        {
            return false;
        }

        return true;
    }

    private bool ValidateModel(string model, out string validatedModel)
    {
        validatedModel = model;

        // Validate length
        if (string.IsNullOrWhiteSpace(model) || model.Length > 20)
        {
            return false;
        }

        // Must be in supported models list
        if (!_config.SupportedModels.Contains(model))
        {
            return false;
        }

        return true;
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

    private async Task SendSegmentAsync(string taskId, TimeSpan start, TimeSpan end, string text)
    {
        try
        {
            await _hubConnection!.InvokeAsync("Segment", new { TaskId = Guid.Parse(taskId), Start = start.ToString(@"hh\:mm\:ss"), End = end.ToString(@"hh\:mm\:ss"), Text = text });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send segment");
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

    private async Task SendRejectAsync(string taskId, string reason)
    {
        try
        {
            await _hubConnection!.InvokeAsync("TaskRejected", Guid.Parse(taskId), reason);
            _logger.LogInformation("Task {TaskId} rejected and returned to queue", taskId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send rejection for task {TaskId}", taskId);
        }
    }

    private async Task SendDownloadStartedAsync(string taskId)
    {
        try
        {
            await _hubConnection!.InvokeAsync("DownloadStarted", Guid.Parse(taskId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send download started");
        }
    }

    private async Task SendDownloadCompletedAsync(string taskId)
    {
        try
        {
            await _hubConnection!.InvokeAsync("DownloadCompleted", Guid.Parse(taskId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send download completed");
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
