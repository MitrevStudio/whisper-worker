using System.Text;
using System.Text.Json;
using Whisper.net;

namespace Worker.Services;

public static class OutputFormatter
{
    public static object Format(IEnumerable<SegmentData> segments, string format, bool includeTimestamps)
    {
        return format.ToLowerInvariant() switch
        {
            "txt" => FormatText(segments),
            "srt" => FormatSrt(segments),
            "vtt" => FormatVtt(segments),
            "json" => FormatJson(segments, includeTimestamps),
            _ => FormatJson(segments, includeTimestamps)
        };
    }

    private static string FormatText(IEnumerable<SegmentData> segments)
    {
        var sb = new StringBuilder();
        foreach (var segment in segments)
        {
            sb.AppendLine(segment.Text.Trim());
        }
        return sb.ToString().Trim();
    }

    private static string FormatSrt(IEnumerable<SegmentData> segments)
    {
        var sb = new StringBuilder();
        var index = 1;

        foreach (var segment in segments)
        {
            sb.AppendLine(index.ToString());
            sb.AppendLine($"{FormatSrtTime(segment.Start)} --> {FormatSrtTime(segment.End)}");
            sb.AppendLine(segment.Text.Trim());
            sb.AppendLine();
            index++;
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatSrtTime(TimeSpan time)
    {
        return $"{time.Hours:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}";
    }

    private static string FormatVtt(IEnumerable<SegmentData> segments)
    {
        var sb = new StringBuilder();
        sb.AppendLine("WEBVTT");
        sb.AppendLine();

        foreach (var segment in segments)
        {
            sb.AppendLine($"{FormatVttTime(segment.Start)} --> {FormatVttTime(segment.End)}");
            sb.AppendLine(segment.Text.Trim());
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatVttTime(TimeSpan time)
    {
        return $"{time.Hours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }

    private static object FormatJson(IEnumerable<SegmentData> segments, bool includeTimestamps)
    {
        var result = segments.Select(s => new
        {
            text = s.Text.Trim(),
            start = includeTimestamps ? (double?)s.Start.TotalSeconds : null,
            end = includeTimestamps ? (double?)s.End.TotalSeconds : null
        }).ToList();

        return new
        {
            transcription = result,
            full_text = string.Join(" ", result.Select(r => r.text))
        };
    }
}
