using System.Text.Json.Serialization;

namespace Worker.Models;

public class TranscriptionTaskMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "task";

    [JsonPropertyName("data")]
    public TranscriptionTaskData Data { get; set; } = new();
}

public class TranscriptionTaskData
{
    [JsonPropertyName("task_id")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("file")]
    public TaskFile File { get; set; } = new();

    [JsonPropertyName("params")]
    public TaskParams Params { get; set; } = new();
}

public class TaskFile
{
    [JsonPropertyName("download_url")]
    public string DownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("checksum")]
    public string Checksum { get; set; } = string.Empty;
}

public class TaskParams
{
    [JsonPropertyName("language")]
    public string Language { get; set; } = "auto";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "base";
}
