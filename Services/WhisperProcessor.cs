using FFMpegCore;
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

    // Supported audio extensions
    private static readonly HashSet<string> SupportedAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".aac", ".m4a", ".wma", ".ogg", ".flac", ".aiff", ".aif",
        ".opus", ".webm", ".ac3", ".amr", ".ape", ".au", ".mka", ".ra", ".tta", ".wv"
    };

    // Supported video extensions (audio will be extracted)
    private static readonly HashSet<string> SupportedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpeg", ".mpg",
        ".3gp", ".3g2", ".ogv", ".ts", ".mts", ".m2ts", ".vob", ".rm", ".rmvb", ".asf"
    };

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

        await using var processor = builder.Build();
        
        // Convert to WAV file and use FileStream instead of MemoryStream
        // This avoids memory issues with large files (1GB+ WAV data)
        var tempWavPath = await ConvertToWhisperFormatAsync(audioPath, ct);
        
        try
        {
            // Use FileStream with optimized buffer size for large file reading
            await using var audioStream = new FileStream(
                tempWavPath, 
                FileMode.Open, 
                FileAccess.Read, 
                FileShare.Read, 
                bufferSize: 1024 * 1024,  // 1MB buffer for better sequential read performance
                useAsync: true);
            
            _logger?.LogDebug("Processing audio file: {Size} bytes", audioStream.Length);
            
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
        finally
        {
            // Clean up temp WAV file
            if (File.Exists(tempWavPath))
            {
                try { File.Delete(tempWavPath); } catch { /* ignore cleanup errors */ }
            }
        }
    }

    /// <summary>
    /// Converts audio/video file to 16kHz mono WAV format required by Whisper using FFmpeg.
    /// Returns the path to the temporary WAV file (caller must clean up).
    /// Uses FFprobe for format detection instead of relying on file extension.
    /// </summary>
    private async Task<string> ConvertToWhisperFormatAsync(string inputPath, CancellationToken ct)
    {
        // Use FFprobe to detect actual format instead of relying on extension
        var mediaInfo = await FFProbe.AnalyseAsync(inputPath, cancellationToken: ct);
        
        if (mediaInfo.PrimaryAudioStream is null)
        {
            throw new InvalidOperationException("No audio stream found in the media file");
        }

        var hasVideo = mediaInfo.PrimaryVideoStream is not null;
        _logger?.LogDebug(
            "Converting {MediaType} file to 16kHz mono WAV format. Detected format: {Format}, Duration: {Duration}",
            hasVideo ? "video" : "audio",
            mediaInfo.Format.FormatName,
            mediaInfo.Duration);

        var tempWavPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.wav");

        try
        {
            var result = await FFMpegArguments
                .FromFileInput(inputPath)
                .OutputToFile(tempWavPath, overwrite: true, options => options
                    .WithAudioSamplingRate(16000)      // 16kHz sample rate
                    .WithAudioCodec("pcm_s16le")       // 16-bit PCM
                    .ForceFormat("wav")                 // WAV format
                    .WithCustomArgument("-ac 1")        // Mono channel
                    .WithCustomArgument("-vn"))         // No video (extract audio only)
                .CancellableThrough(ct)
                .ProcessAsynchronously();

            if (!result)
            {
                throw new InvalidOperationException("FFmpeg conversion failed");
            }

            var fileInfo = new FileInfo(tempWavPath);
            _logger?.LogDebug("Media conversion completed. WAV file size: {SizeBytes} bytes ({SizeMB:F1} MB)", 
                fileInfo.Length, 
                fileInfo.Length / (1024.0 * 1024.0));

            return tempWavPath;
        }
        catch (Exception ex)
        {
            // Clean up on failure
            if (File.Exists(tempWavPath))
            {
                try { File.Delete(tempWavPath); } catch { }
            }
            
            if (ex is InvalidOperationException)
                throw;
                
            throw new InvalidOperationException(
                $"Failed to convert media file. Make sure FFmpeg is installed and available in PATH. Error: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Gets all supported file extensions (audio and video).
    /// </summary>
    public static IEnumerable<string> GetSupportedExtensions() =>
        SupportedAudioExtensions.Concat(SupportedVideoExtensions);

    /// <summary>
    /// Checks if a file extension is supported.
    /// </summary>
    public static bool IsFormatSupported(string extension) =>
        SupportedAudioExtensions.Contains(extension) || SupportedVideoExtensions.Contains(extension);

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

    /// <summary>
    /// Disposes all cached WhisperFactory instances.
    /// Call this during application shutdown to properly release resources.
    /// </summary>
    public static void DisposeAllFactories()
    {
        lock (_factoryLock)
        {
            foreach (var factory in _factories.Values)
            {
                try
                {
                    factory.Dispose();
                }
                catch
                {
                    // Ignore disposal errors during shutdown
                }
            }
            _factories.Clear();
        }
    }

    public void Dispose()
    {
        // Note: Static factories are intentionally NOT disposed here
        // They persist across WhisperProcessor instances for performance
        // Call DisposeAllFactories() during application shutdown for cleanup
    }
}
