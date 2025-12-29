using System.Buffers;
using System.Security.Cryptography;

namespace Worker.Services;

public class FileDownloader
{
    private readonly HttpClient _httpClient;
    private readonly string _tempPath;
    private const long MaxDownloadSizeBytes = 10L * 1024 * 1024 * 1024; // 10 GB
    private const int BufferSizeBytes = 64 * 1024; // 64 KB chunks
    private const int MaxParallelDownloads = 4; // ????????? ????????

    public FileDownloader(HttpClient httpClient, string tempPath)
    {
        _httpClient = httpClient;
        _tempPath = tempPath;
        Directory.CreateDirectory(_tempPath);
    }

    /// <summary>
    /// Download chunks in parallel and assemble into a single file.
    /// </summary>
    public async Task<string> DownloadChunksAndAssembleAsync(string chunkBaseUrl, int chunkCount, string expectedChecksum, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedChecksum))
        {
            throw new ArgumentException("Checksum is required for file verification", nameof(expectedChecksum));
        }

        var fileName = $"{Guid.NewGuid()}.wav";
        var filePath = Path.Combine(_tempPath, fileName);
        var chunkFiles = new string[chunkCount];

        try
        {
            // ????????? ??????? ?? chunks
            await Parallel.ForEachAsync(
                Enumerable.Range(0, chunkCount),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxParallelDownloads,
                    CancellationToken = ct
                },
                async (i, token) =>
                {
                    chunkFiles[i] = await DownloadChunkAsync(chunkBaseUrl, i, token);
                });

            // ??????????? ? ??????????? ?? checksum ????????????
            var actualChecksum = await AssembleChunksWithChecksumAsync(chunkFiles, filePath, ct);

            // ???????? ?? checksum
            var expected = expectedChecksum.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? expectedChecksum[7..]
                : expectedChecksum;

            if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(actualChecksum.ToLowerInvariant()),
                System.Text.Encoding.UTF8.GetBytes(expected.ToLowerInvariant())))
            {
                File.Delete(filePath);
                throw new InvalidOperationException("Checksum verification failed");
            }

            return filePath;
        }
        finally
        {
            // ????????? ?? ?????????? chunk ???????
            foreach (var chunkFile in chunkFiles)
            {
                if (!string.IsNullOrEmpty(chunkFile) && File.Exists(chunkFile))
                {
                    try { File.Delete(chunkFile); } catch { }
                }
            }
        }
    }

    private async Task<string> DownloadChunkAsync(string chunkBaseUrl, int chunkIndex, CancellationToken ct)
    {
        var chunkUrl = $"{chunkBaseUrl}/{chunkIndex}";
        var chunkPath = Path.Combine(_tempPath, $"{Guid.NewGuid()}.chunk");

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSizeBytes);
        try
        {
            using var response = await _httpClient.GetAsync(chunkUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(chunkPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSizeBytes, useAsync: true);

            int bytesRead;
            while ((bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, BufferSizeBytes), ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            }

            return chunkPath;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<string> AssembleChunksWithChecksumAsync(string[] chunkFiles, string outputPath, CancellationToken ct)
    {
        long totalBytes = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSizeBytes);

        try
        {
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSizeBytes, useAsync: true);

            foreach (var chunkFile in chunkFiles)
            {
                await using var chunkStream = new FileStream(chunkFile, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSizeBytes, useAsync: true);

                int bytesRead;
                while ((bytesRead = await chunkStream.ReadAsync(buffer.AsMemory(0, BufferSizeBytes), ct)) > 0)
                {
                    totalBytes += bytesRead;

                    if (totalBytes > MaxDownloadSizeBytes)
                    {
                        throw new InvalidOperationException(
                            $"File size exceeds maximum allowed size of {MaxDownloadSizeBytes / (1024 * 1024 * 1024)} GB");
                    }

                    // ????????? ? ???????? ????????????
                    await outputStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                    sha256.AppendData(buffer, 0, bytesRead);
                }
            }

            await outputStream.FlushAsync(ct);
            return Convert.ToHexString(sha256.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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
