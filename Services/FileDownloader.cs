using System.Security.Cryptography;

namespace Worker.Services;

public class FileDownloader
{
    private readonly HttpClient _httpClient;
    private readonly string _tempPath;

    public FileDownloader(HttpClient httpClient, string tempPath)
    {
        _httpClient = httpClient;
        _tempPath = tempPath;
        Directory.CreateDirectory(_tempPath);
    }

    public async Task<string> DownloadAsync(string url, string expectedChecksum, CancellationToken ct)
    {
        var fileName = $"{Guid.NewGuid()}.wav";
        var filePath = Path.Combine(_tempPath, fileName);

        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(fileStream, ct);
        await fileStream.FlushAsync(ct);
        fileStream.Close();

        // Verify checksum
        if (!string.IsNullOrEmpty(expectedChecksum))
        {
            var actualChecksum = await ComputeChecksumAsync(filePath, ct);
            var expected = expectedChecksum.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? expectedChecksum[7..]
                : expectedChecksum;

            if (!string.Equals(actualChecksum, expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(filePath);
                throw new InvalidOperationException($"Checksum mismatch. Expected: {expected}, Actual: {actualChecksum}");
            }
        }

        return filePath;
    }

    private static async Task<string> ComputeChecksumAsync(string filePath, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Cleanup(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
