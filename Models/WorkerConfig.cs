namespace Worker.Models;

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
    public List<string> SupportedLanguages { get; set; } = ["auto", "bg", "en"];
    public List<string> SupportedModels { get; set; } = ["tiny", "base", "small", "medium", "large"];
    public string DefaultModel { get; set; } = "base";
}
