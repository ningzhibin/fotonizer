using System.IO;
using Fotonizer.Core.Import;
using Fotonizer.Core.Logging;
using Fotonizer.Core.Media;
using Fotonizer.Core.Models;
using Fotonizer.Core.Settings;

namespace Fotonizer.Core.Upload;

public sealed record UploadStatus(int Uploaded, int Failed, int Remaining, string Message);

/// <summary>Background uploader: drains the pending queue in the library database.</summary>
public sealed class UploadService : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly ImportDatabase _database;
    private readonly GooglePhotosClient _client;
    private readonly AppLog _log = AppLog.Shared;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTime _lastConfigurationWarning = DateTime.MinValue;
    private DateTime _lastReauthorizationWarning = DateTime.MinValue;

    public event EventHandler<UploadStatus>? StatusChanged;

    public UploadService(SettingsStore settings, ImportDatabase database, GooglePhotosClient client)
    {
        _settings = settings;
        _database = database;
        _client = client;
    }

    public void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Kick()
    {
        try
        {
            if (_signal.CurrentCount == 0) _signal.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;
        _cts.Cancel();
        if (_loop != null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _loop = null;
        _cts.Dispose();
        _cts = null;
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var didWork = false;
            try
            {
                didWork = await ProcessAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException ex)
            {
                _log.Warn($"上传循环被意外取消，将继续运行：{ex.Message}");
            }
            catch (Exception ex)
            {
                _log.Warn($"上传循环异常：{ex.Message}");
            }

            var delay = didWork ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(30);
            try
            {
                await _signal.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<bool> ProcessAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        if (!settings.UploadEnabled) return false;

        if (!_client.IsConfigured)
        {
            if (DateTime.Now - _lastConfigurationWarning > TimeSpan.FromMinutes(10))
            {
                _lastConfigurationWarning = DateTime.Now;
                _log.Warn("已开启 Google 照片上传，但还没有完成授权，暂不上传。");
            }

            return false;
        }

        if (_client.NeedsReauthorization)
        {
            if (DateTime.Now - _lastReauthorizationWarning > TimeSpan.FromMinutes(10))
            {
                _lastReauthorizationWarning = DateTime.Now;
                _log.Warn("Google 照片授权已失效，请打开设置重新连接。");
            }

            return false;
        }

        var pending = _database.GetPendingUploads(25, settings.UploadRetryLimit);
        if (pending.Count == 0) return false;

        var uploaded = 0;
        var failed = 0;
        var remaining = pending.Count;

        foreach (var record in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            remaining--;

            if (!settings.UploadVideos && MediaTypes.IsVideo(record.DestPath))
            {
                _database.MarkUploadSkipped(record.Sha256, "设置中已关闭视频上传");
                continue;
            }

            if (!File.Exists(record.DestPath))
            {
                _database.MarkUploadAttemptFailed(record.Sha256, "本地文件已不存在", settings.UploadRetryLimit);
                failed++;
                Report(uploaded, failed, remaining, $"文件缺失：{record.FileName}");
                continue;
            }

            var albumTitle = RenderAlbumTitle(record, settings);
            string? albumId = null;
            if (!string.IsNullOrWhiteSpace(albumTitle))
            {
                try
                {
                    albumId = await GetOrCreateAlbumAsync(albumTitle, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.Warn($"创建/使用 Google 相册“{albumTitle}”失败，将上传到时间线：{ex.Message}");
                }
            }

            try
            {
                Report(uploaded, failed, remaining,
                    $"正在上传 {record.FileName}（{FormatSize(record.Size)}）…");
                _log.Info($"开始上传 {record.FileName}（{FormatSize(record.Size)}）…");
                var mediaItemId = await _client
                    .UploadAsync(record.DestPath, record.FileName, albumId, cancellationToken)
                    .ConfigureAwait(false);

                _database.MarkUploaded(record.Sha256, mediaItemId);
                uploaded++;
                _log.Info($"已上传到 Google 照片：{record.FileName}");
                Report(uploaded, failed, remaining, $"已上传 {record.FileName}");
            }
            catch (GoogleAuthException ex)
            {
                _log.Warn($"Google 授权失效，暂停上传：{ex.Message}");
                Report(uploaded, failed, remaining, "Google 授权失效，请重新连接");
                return uploaded > 0;
            }
            catch (GooglePhotosException ex) when (ex.StatusCode is System.Net.HttpStatusCode.TooManyRequests
                                                       or System.Net.HttpStatusCode.ServiceUnavailable)
            {
                _log.Warn($"Google 暂时限流/不可用：{ex.Message}");
                Report(uploaded, failed, remaining, "Google 限流，稍后自动重试");
                return uploaded > 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                // A per-request timeout (not the app shutting down) must never kill the
                // upload worker. Record the failure and let the next loop retry.
                failed++;
                _database.MarkUploadAttemptFailed(record.Sha256, "上传超时", settings.UploadRetryLimit);
                _log.Warn($"上传超时 {record.FileName}：{ex.Message}");
                Report(uploaded, failed, remaining, $"上传超时：{record.FileName}");
            }
            catch (Exception ex)
            {
                failed++;
                _database.MarkUploadAttemptFailed(record.Sha256, ex.Message, settings.UploadRetryLimit);
                _log.Warn($"上传失败 {record.FileName}：{ex.Message}");
                Report(uploaded, failed, remaining, $"上传失败：{record.FileName}");
            }
        }

        return uploaded > 0 || failed > 0;
    }

    private async Task<string?> GetOrCreateAlbumAsync(string title, CancellationToken cancellationToken)
    {
        var key = "album:" + title;
        var existing = _database.GetMeta(key);
        if (!string.IsNullOrWhiteSpace(existing)) return existing;

        var id = await _client.CreateAlbumAsync(title, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(id)) return null;

        _database.SetMeta(key, id);
        _log.Info($"已创建 Google 相册：{title}");
        return id;
    }

    private static string RenderAlbumTitle(ImportedFileRecord record, AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.AlbumTemplate)) return "";
        var meta = new PhotoMetadata
        {
            SourcePath = record.FileName,
            CapturedAt = record.CapturedAt
        };
        return NamingTemplate.Render(settings.AlbumTemplate, meta).Trim();
    }

    private void Report(int uploaded, int failed, int remaining, string message)
    {
        try
        {
            StatusChanged?.Invoke(this, new UploadStatus(uploaded, failed, remaining, message));
        }
        catch
        {
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "未知大小";
        var mb = bytes / 1024.0 / 1024.0;
        return mb >= 1024 ? $"{mb / 1024:F2} GB" : $"{mb:F1} MB";
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _signal.Dispose();
    }
}
