using System.Buffers;
using System.IO;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Fotonizer.Core.Logging;

namespace Fotonizer.Core.Import;

public static class FileFingerprint
{
    private const int SampleSize = 64 * 1024;

    /// <summary>
    /// Cheap identity check: SHA-256 over length + first 64 KiB + last 64 KiB.
    /// Used to skip files that were already imported without reading entire (possibly huge) videos.
    /// </summary>
    public static string QuickHash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.SequentialScan);
        return QuickHash(stream);
    }

    public static string QuickHash(Stream stream)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> lengthBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, stream.Length);
        hash.AppendData(lengthBytes);

        var buffer = ArrayPool<byte>.Shared.Rent(SampleSize);
        try
        {
            var read = stream.Read(buffer, 0, SampleSize);
            if (read > 0) hash.AppendData(buffer, 0, read);

            if (stream.Length > SampleSize)
            {
                stream.Seek(-SampleSize, SeekOrigin.End);
                read = stream.Read(buffer, 0, SampleSize);
                if (read > 0) hash.AppendData(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static async Task<string> CopyAndHashAsync(string sourcePath, string destinationPath,
        CancellationToken cancellationToken = default, Action<long>? bytesCopied = null)
    {
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 1024 * 1024, FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                total += read;
                bytesCopied?.Invoke(total);
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLog.Shared.Debug($"删除临时文件失败 {path}: {ex.Message}");
        }
    }
}
