namespace Worker.Models;

public record TaskParams
{
    public required string Language { get; init; }
    public required string Model { get; init; }
    
    /// <summary>
    /// Number of threads to use for processing. Defaults to Environment.ProcessorCount if not specified.
    /// </summary>
    public int? ThreadCount { get; init; }
    
    /// <summary>
    /// If true, translates the audio to English instead of transcribing in the original language.
    /// </summary>
    public bool Translate { get; init; } = false;
}

public class WorkerConfig
{
    public string Name { get; set; } = "whisper-worker";
    public string ApiUrl { get; set; } = "http://localhost:5000";
    public string Token { get; set; } = string.Empty;
    public string ModelPath { get; set; } = "/models";
    public string TempPath { get; set; } = "/tmp/whisper";
    public int HeartbeatIntervalSeconds { get; set; } = 30;
    public List<string> SupportedTasks { get; set; } = ["transcription"];
    public List<string> SupportedFormats { get; set; } = ["txt", "json", "srt", "vtt"];

    // All Whisper supported languages by default
    public List<string> SupportedLanguages { get; set; } = [
        "auto", "af", "am", "ar", "as", "az", "ba", "be", "bg", "bn", "bo", "br", "bs", "ca",
        "cs", "cy", "da", "de", "el", "en", "es", "et", "eu", "fa", "fi", "fo", "fr", "gl",
        "gu", "ha", "haw", "he", "hi", "hr", "ht", "hu", "hy", "id", "is", "it", "ja", "jw",
        "ka", "kk", "km", "kn", "ko", "la", "lb", "ln", "lo", "lt", "lv", "mg", "mi", "mk",
        "ml", "mn", "mr", "ms", "mt", "my", "ne", "nl", "nn", "no", "oc", "pa", "pl", "ps",
        "pt", "ro", "ru", "sa", "sd", "si", "sk", "sl", "sn", "so", "sq", "sr", "su", "sv",
        "sw", "ta", "te", "tg", "th", "tk", "tl", "tr", "tt", "uk", "ur", "uz", "vi", "yi", "yo", "zh"
    ];

    public List<string> SupportedModels { get; set; } = ["tiny", "base", "small", "medium", "large"];
    public string DefaultModel { get; set; } = "base";
}
