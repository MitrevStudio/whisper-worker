using System.Security.Cryptography;

namespace Worker.Services;

public class FileDownloader
{
    private readonly HttpClient _httpClient;
    private readonly string _tempPath;
    private const long MaxDownloadSizeBytes = 10L * 1024 * 1024 * 1024; // 10 GB
    private const int BufferSizeBytes = 64 * 1024; // 64 KB chunks

    public FileDownloader(HttpClient httpClient, string tempPath)
    {
        _httpClient = httpClient;
        _tempPath = tempPath;
        Directory.CreateDirectory(_tempPath);
    }

    /// <summary>
    /// Download chunks sequentially and assemble into a single file.
    /// </summary>
    public async Task<string> DownloadChunksAndAssembleAsync(string chunkBaseUrl, int chunkCount, string expectedChecksum, CancellationToken ct)
    {
        // Validate checksum is provided (mandatory for security)
        if (string.IsNullOrWhiteSpace(expectedChecksum))
        {
            throw new ArgumentException("Checksum is required for file verification", nameof(expectedChecksum));
        }

        var fileName = $"{Guid.NewGuid()}.wav";
        var filePath = Path.Combine(_tempPath, fileName);

        long totalBytesDownloaded = 0;

        await using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSizeBytes))
        {
            for (int i = 0; i < chunkCount; i++)
            {
                var chunkUrl = $"{chunkBaseUrl}/{i}";

                using var response = await _httpClient.GetAsync(chunkUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                await using var contentStream = await response.Content.ReadAsStreamAsync(ct);

                byte[] buffer = new byte[BufferSizeBytes];
                int bytesRead;
                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    totalBytesDownloaded += bytesRead;

                    // Enforce size limit during download (defense in depth)
                    if (totalBytesDownloaded > MaxDownloadSizeBytes)
                    {
                        fileStream.Close();
                        File.Delete(filePath);
                        throw new InvalidOperationException(
                            $"File size exceeds maximum allowed size of {MaxDownloadSizeBytes / (1024 * 1024 * 1024)} GB");
                    }

                    await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
                }
            }

            await fileStream.FlushAsync(ct);
        }

        // Verify checksum
        var actualChecksum = await ComputeChecksumAsync(filePath, ct);
        var expected = expectedChecksum.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? expectedChecksum[7..]
            : expectedChecksum;

        // Use constant-time comparison to prevent timing attacks
        if (!CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(actualChecksum.ToLowerInvariant()),
            System.Text.Encoding.UTF8.GetBytes(expected.ToLowerInvariant())))
        {
            File.Delete(filePath);
            throw new InvalidOperationException("Checksum verification failed");
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
