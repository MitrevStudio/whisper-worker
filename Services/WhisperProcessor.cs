using FFMpegCore;
using FFMpegCore.Pipes;
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
    private readonly Dictionary<string, WhisperFactory> _factories = new();
    private readonly object _lock = new();
    private int _lastLoggedProgress = -1;
    private static bool _runtimeConfigured = false;
    private static readonly object _runtimeLock = new();

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
                RuntimeOptions.RuntimeLibraryOrder = new List<RuntimeLibrary>
                {
                    RuntimeLibrary.Cuda
                };
                _logger?.LogInformation("Whisper runtime configured for GPU acceleration (CUDA only)");
            }
            else
            {
                // CPU fallback for non-GPU environments
                RuntimeOptions.RuntimeLibraryOrder = new List<RuntimeLibrary>
                {
                    RuntimeLibrary.Cpu
                };
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
        _lastLoggedProgress = -1;
        _logger?.LogInformation("Starting transcription processing");

        var modelFile = GetModelFilePath(taskParams.Model);
        if (!File.Exists(modelFile))
        {
            await DownloadModelAsync(taskParams.Model, modelFile, ct);
        }

        var factory = GetOrCreateFactory(modelFile);

        await using var processor = factory.CreateBuilder()
            .WithLanguage(taskParams.Language == "auto" ? "auto" : taskParams.Language)
            .WithProgressHandler(progress =>
            {
                if (progress != _lastLoggedProgress)
                {
                    _logger?.LogDebug("Processing progress: {Progress}%", progress);
                    _lastLoggedProgress = progress;
                }
                onProgress(progress);
            })
            .WithSegmentEventHandler(segment =>
            {
                onSegment(segment.Start, segment.End, segment.Text ?? "");
            })
            .Build();

        _logger?.LogDebug("Starting audio transcription");

        // Convert audio/video to 16kHz WAV format that Whisper requires
        await using var audioStream = await ConvertToWhisperFormatAsync(audioPath, ct);

        // ProcessAsync still needs to be consumed to drive the processing
        await foreach (var _ in processor.ProcessAsync(audioStream, ct))
        {
            // Segments are already captured via WithSegmentEventHandler
        }

        _logger?.LogInformation("Transcription processing completed");
    }

    /// <summary>
    /// Converts audio/video file to 16kHz mono WAV format required by Whisper using FFmpeg.
    /// Supports all audio and video formats that FFmpeg supports.
    /// </summary>
    private async Task<MemoryStream> ConvertToWhisperFormatAsync(string inputPath, CancellationToken ct)
    {
        var extension = Path.GetExtension(inputPath);

        if (!SupportedAudioExtensions.Contains(extension) && !SupportedVideoExtensions.Contains(extension))
        {
            throw new NotSupportedException(
                $"Format '{extension}' is not supported. Supported audio formats: {string.Join(", ", SupportedAudioExtensions)}. " +
                $"Supported video formats: {string.Join(", ", SupportedVideoExtensions)}");
        }

        var isVideo = SupportedVideoExtensions.Contains(extension);
        _logger?.LogDebug("Converting {MediaType} file to 16kHz mono WAV format", isVideo ? "video" : "audio");

        var wavStream = new MemoryStream();

        try
        {
            // Use a temporary file for the output since FFMpegCore works better with files
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

                // Read the temp file into memory stream
                await using var fileStream = File.OpenRead(tempWavPath);
                await fileStream.CopyToAsync(wavStream, ct);
            }
            finally
            {
                // Clean up temp file
                if (File.Exists(tempWavPath))
                {
                    try { File.Delete(tempWavPath); } catch { /* ignore cleanup errors */ }
                }
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to convert media file. Make sure FFmpeg is installed and available in PATH. Error: {ex.Message}", ex);
        }

        // Reset stream position to beginning for reading
        wavStream.Seek(0, SeekOrigin.Begin);

        _logger?.LogDebug("Media conversion completed. Size: {SizeBytes} bytes", wavStream.Length);

        return wavStream;
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
                "large" => GgmlType.LargeV3, // Defaulting large to V3
                "large-v1" => GgmlType.LargeV1,
                "large-v2" => GgmlType.LargeV2,
                "large-v3" => GgmlType.LargeV3,
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
        using var fileWriter = File.OpenWrite(destinationPath);
        await modelStream.CopyToAsync(fileWriter, ct);
        _logger?.LogInformation("Model downloaded successfully");
    }

    private WhisperFactory GetOrCreateFactory(string modelFile)
    {
        lock (_lock)
        {
            if (!_factories.TryGetValue(modelFile, out var factory))
            {
                _logger?.LogInformation("Loading Whisper model from: {ModelFile}", modelFile);
                _logger?.LogInformation("Configured runtime priority: {Priority}",
                    string.Join(" -> ", RuntimeOptions.RuntimeLibraryOrder ?? new List<RuntimeLibrary>()));

                // Log available runtime libraries
                var cudaPath = Path.Combine(AppContext.BaseDirectory, "runtimes", "cuda", "linux-x64");
                var cpuPath = Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64");
                _logger?.LogInformation("CUDA runtime path exists: {Exists}, Path: {Path}",
                    Directory.Exists(cudaPath), cudaPath);
                _logger?.LogInformation("CPU runtime path exists: {Exists}, Path: {Path}",
                    Directory.Exists(cpuPath), cpuPath);

                if (Directory.Exists(cudaPath))
                {
                    var files = Directory.GetFiles(cudaPath, "*.so");
                    _logger?.LogInformation("CUDA runtime files: {Files}", string.Join(", ", files.Select(Path.GetFileName)));
                }

                try
                {
                    factory = WhisperFactory.FromPath(modelFile);

                    // Log the ACTUAL loaded runtime
                    var loadedRuntime = RuntimeOptions.LoadedLibrary;
                    _logger?.LogInformation("Whisper model loaded successfully. ACTUAL Runtime: {Runtime}",
                        loadedRuntime?.ToString() ?? "Unknown");
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to load Whisper with preferred runtime, attempting fallback");
                    // Reset to allow any runtime
                    RuntimeOptions.RuntimeLibraryOrder = null;
                    factory = WhisperFactory.FromPath(modelFile);
                    var loadedRuntime = RuntimeOptions.LoadedLibrary;
                    _logger?.LogInformation("Whisper model loaded with fallback. ACTUAL Runtime: {Runtime}",
                        loadedRuntime?.ToString() ?? "Unknown");
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
        lock (_lock)
        {
            foreach (var factory in _factories.Values)
            {
                factory.Dispose();
            }
            _factories.Clear();
        }
    }
}
