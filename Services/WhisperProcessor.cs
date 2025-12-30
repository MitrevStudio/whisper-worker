using Microsoft.Extensions.Logging;
using Whisper.net;
using Whisper.net.Ggml;
using Whisper.net.LibraryLoader;
using Worker.Models;

namespace Worker.Services;

public class WhisperProcessor : IDisposable
{
    private readonly string _modelPath;
    private readonly ILogger<WhisperProcessor>? _logger;

    // Static cache for WhisperFactory instances - persists across WhisperProcessor instances
    private static readonly Dictionary<string, WhisperFactory> _factories = new();
    private static readonly object _factoryLock = new();

    // Thread-safe progress tracking
    private int _lastLoggedProgress = -1;
    private static bool _runtimeConfigured = false;
    private static readonly object _runtimeLock = new();

    // Hallucination detection constants
    private const int MaxConsecutiveDuplicates = 2;

    // Anti-hallucination thresholds (tuned for large-v3-turbo)
    private const float DefaultNoSpeechThreshold = 0.6f;
    private const float DefaultEntropyThreshold = 2.4f;      // Filter low-entropy (repetitive) outputs
    private const float DefaultLogProbThreshold = -1.0f;     // Filter low-confidence outputs
    private const float DefaultTemperature = 0.0f;           // Deterministic output (less hallucination)
    private const float DefaultTemperatureInc = 0.2f;        // Gradual increase on retry

    // Context settings for long files - balance between quality and stability
    private const int DefaultMaxLastTextTokens = 16;         // Limited context to prevent overflow (~2-3 sentences)

    public WhisperProcessor(string modelPath, ILogger<WhisperProcessor>? logger = null)
    {
        _modelPath = modelPath;
        _logger = logger;
        ConfigureRuntimeLibraryOrder();
    }

    /// <summary>
    /// Configures the runtime library order to prioritize CUDA over CPU.
    /// This ensures GPU acceleration is used when available.
    /// </summary>
    private void ConfigureRuntimeLibraryOrder()
    {
        lock (_runtimeLock)
        {
            if (_runtimeConfigured)
                return;

            // Check if we're running in a GPU container (CUDA libraries available)
            var cudaPath = Path.Combine(AppContext.BaseDirectory, "runtimes", "cuda", "linux-x64");
            var hasCudaRuntime = Directory.Exists(cudaPath) &&
                                 Directory.GetFiles(cudaPath, "libggml-cuda-whisper.so").Length > 0;

            if (hasCudaRuntime && OperatingSystem.IsLinux())
            {
                // Force CUDA only - don't allow CPU fallback which can mask GPU issues
                RuntimeOptions.RuntimeLibraryOrder =
                [
                    RuntimeLibrary.Cuda
                ];
                _logger?.LogInformation("Whisper runtime configured for GPU acceleration (CUDA only)");
            }
            else
            {
                // CPU fallback for non-GPU environments
                RuntimeOptions.RuntimeLibraryOrder =
                [
                    RuntimeLibrary.Cpu
                ];
                _logger?.LogInformation("Whisper runtime configured for CPU (no CUDA runtime found)");
            }

            _runtimeConfigured = true;
        }
    }

    public async Task ProcessAsync(
        string audioPath,
        TaskParams taskParams,
        Action<int> onProgress,
        Action<TimeSpan, TimeSpan, string> onSegment,
        CancellationToken ct)
    {
        Interlocked.Exchange(ref _lastLoggedProgress, -1);

        var modelFile = GetModelFilePath(taskParams.Model);
        if (!File.Exists(modelFile))
        {
            await DownloadModelAsync(taskParams.Model, modelFile, ct);
        }

        var factory = GetOrCreateFactory(modelFile);
        var builder = factory.CreateBuilder()
            .WithLanguage(taskParams.Language == "auto" ? "auto" : taskParams.Language)
            .WithProgressHandler(progress =>
            {
                var lastProgress = Interlocked.Exchange(ref _lastLoggedProgress, progress);
                if (progress != lastProgress)
                {
                    _logger?.LogDebug("Processing progress: {Progress}%", progress);
                }
                onProgress(progress);
            });

        // Configure thread count for optimal performance
        var threadCount = taskParams.ThreadCount ?? Environment.ProcessorCount;
        builder.WithThreads(threadCount);

        // Enable translation to English if requested
        if (taskParams.Translate)
        {
            builder.WithTranslate();
        }

        // Anti-hallucination settings
        builder.WithNoSpeechThreshold(DefaultNoSpeechThreshold);
        builder.WithEntropyThreshold(DefaultEntropyThreshold);
        builder.WithLogProbThreshold(DefaultLogProbThreshold);
        builder.WithTemperature(DefaultTemperature);
        builder.WithTemperatureInc(DefaultTemperatureInc);

        // Limited context for long files - prevents overflow while maintaining some quality
        // Using 16 tokens (~2-3 sentences) provides balance between context and stability
        builder.WithMaxLastTextTokens(DefaultMaxLastTextTokens);

        await using var processor = builder.Build();

        // Input is now pre-converted 16kHz mono WAV from the API
        // Use FileStream directly - no FFmpeg conversion needed
        _logger?.LogDebug("Processing pre-converted WAV file: {Path}", audioPath);

        await using var audioStream = new FileStream(
            audioPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 1024,  // 1MB buffer for better sequential read performance
            useAsync: true);

        _logger?.LogDebug("Processing audio file: {Size} bytes ({SizeMB:F1} MB)",
            audioStream.Length,
            audioStream.Length / (1024.0 * 1024.0));

        // Track previous segment for hallucination detection
        string? previousText = null;
        int consecutiveDuplicates = 0;

        await foreach (var segment in processor.ProcessAsync(audioStream, ct))
        {
            var text = segment.Text?.Trim() ?? "";

            // Skip empty segments
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            // Detect hallucination loop (same text repeated many times)
            if (text == previousText)
            {
                consecutiveDuplicates++;

                if (consecutiveDuplicates >= MaxConsecutiveDuplicates)
                {
                    _logger?.LogWarning(
                        "Hallucination detected: '{Text}' repeated {Count} times, skipping",
                        text.Length > 50 ? text[..50] + "..." : text,
                        consecutiveDuplicates + 1);
                    continue;
                }
            }
            else
            {
                consecutiveDuplicates = 0;
                previousText = text;
            }

            onSegment(segment.Start, segment.End, text);
        }
    }

    private async Task DownloadModelAsync(string modelName, string destinationPath, CancellationToken ct)
    {
        if (!Enum.TryParse<GgmlType>(modelName, true, out var ggmlType))
        {
            // Fallback mapping for common names if enum parsing fails or names differ
            ggmlType = modelName.ToLowerInvariant() switch
            {
                "tiny" => GgmlType.Tiny,
                "tiny.en" => GgmlType.TinyEn,
                "base" => GgmlType.Base,
                "base.en" => GgmlType.BaseEn,
                "small" => GgmlType.Small,
                "small.en" => GgmlType.SmallEn,
                "medium" => GgmlType.Medium,
                "medium.en" => GgmlType.MediumEn,
                "large" => GgmlType.LargeV3Turbo,
                "large-v1" => GgmlType.LargeV1,
                "large-v2" => GgmlType.LargeV2,
                "large-v3" => GgmlType.LargeV3,
                "large-v3-turbo" or "turbo" => GgmlType.LargeV3Turbo,
                _ => throw new ArgumentException($"Unknown model type: {modelName}")
            };
        }

        _logger?.LogInformation("Downloading model: {ModelName}", modelName);

        // Ensure directory exists
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(ggmlType, cancellationToken: ct);
        await using var fileWriter = File.OpenWrite(destinationPath);
        await modelStream.CopyToAsync(fileWriter, ct);
        _logger?.LogInformation("Model downloaded successfully");
    }

    private WhisperFactory GetOrCreateFactory(string modelFile)
    {
        lock (_factoryLock)
        {
            if (!_factories.TryGetValue(modelFile, out var factory))
            {
                try
                {
                    factory = WhisperFactory.FromPath(modelFile);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to load Whisper with preferred runtime, attempting fallback");
                    RuntimeOptions.RuntimeLibraryOrder = null;
                    factory = WhisperFactory.FromPath(modelFile);
                }

                _factories[modelFile] = factory;
            }
            return factory;
        }
    }

    private string GetModelFilePath(string modelName)
    {
        // Model files are expected to be named: ggml-{model}.bin
        var fileName = $"ggml-{modelName.ToLowerInvariant()}.bin";
        return Path.Combine(_modelPath, fileName);
    }

    public IEnumerable<string> GetAvailableModels()
    {
        if (!Directory.Exists(_modelPath))
            return [];

        return Directory.GetFiles(_modelPath, "ggml-*.bin")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Select(n => n.Replace("ggml-", ""))
            .ToList();
    }

    public void Dispose()
    {
        // Note: Static factories are intentionally NOT disposed here
        // They persist across WhisperProcessor instances for performance
        // Call DisposeAllFactories() during application shutdown for cleanup
    }
}
