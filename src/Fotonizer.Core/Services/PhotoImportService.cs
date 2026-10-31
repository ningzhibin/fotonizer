using System.Collections.Concurrent;
using System.IO;
using System.Threading.Channels;
using Fotonizer.Core.Import;
using Fotonizer.Core.Logging;
using Fotonizer.Core.Models;
using Fotonizer.Core.Settings;

namespace Fotonizer.Core.Services;

/// <summary>Serialises import jobs (devices can arrive simultaneously; disk IO should stay sequential).</summary>
public sealed class PhotoImportService : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly ImportPipeline _pipeline;
    private readonly AppLog _log = AppLog.Shared;
    private readonly Channel<ImportRequest> _channel = Channel.CreateUnbounded<ImportRequest>();
    private readonly HashSet<string> _queued = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _queueGate = new();
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _worker;

    public event EventHandler<ImportRequest>? ImportStarted;
    public event EventHandler<ImportSummary>? ImportCompleted;
    public event EventHandler<ImportProgress>? ProgressChanged;

    public bool IsBusy { get; private set; }
    public ImportRequest? CurrentRequest { get; private set; }

    public PhotoImportService(SettingsStore settings, ImportDatabase database)
    {
        _settings = settings;
        _pipeline = new ImportPipeline(settings, database);
    }

    public void Start()
    {
        if (_worker != null) return;
        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => WorkerLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;
        _cts.Cancel();
        _channel.Writer.TryComplete();

        if (_worker != null)
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _worker = null;
        _cts.Dispose();
        _cts = null;
    }

    /// <summary>Queues an import unless the same source is already queued/running.</summary>
    public bool Enqueue(ImportRequest request)
    {
        if (_settings.Current.Paused)
        {
            _log.Debug("已暂停，忽略导入请求：" + request);
            return false;
        }

        lock (_queueGate)
        {
            if (_queued.Contains(request.Key)) return false;
            if (IsBusy && CurrentRequest != null &&
                string.Equals(CurrentRequest.Key, request.Key, StringComparison.OrdinalIgnoreCase))
                return false;

            _queued.Add(request.Key);
        }

        if (!_channel.Writer.TryWrite(request))
        {
            lock (_queueGate) _queued.Remove(request.Key);
            return false;
        }

        _log.Info($"已排队导入：{request}");
        return true;
    }

    /// <summary>Runs an import immediately (CLI / “立即导入”). Does not touch the queue.</summary>
    public async Task<ImportSummary> RunNowAsync(ImportRequest request, CancellationToken cancellationToken = default)
    {
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var progress = new Progress<ImportProgress>(p => SafeRaise(ProgressChanged, p));
            SafeRaise(ImportStarted, request);
            try
            {
                var summary = await _pipeline.RunAsync(request, progress, cancellationToken).ConfigureAwait(false);
                SafeRaise(ImportCompleted, summary);
                return summary;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                SafeRaise(ImportCompleted, ImportSummary.Failed(request, ex.Message, TimeSpan.Zero));
                throw;
            }
        }
        finally
        {
            _runGate.Release();
        }
    }

    private async Task WorkerLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_settings.Current.Paused)
                {
                    lock (_queueGate) _queued.Remove(request.Key);
                    continue;
                }

                IsBusy = true;
                CurrentRequest = request;
                var progress = new Progress<ImportProgress>(p => SafeRaise(ProgressChanged, p));

                try
                {
                    await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    lock (_queueGate) _queued.Remove(request.Key);
                    IsBusy = false;
                    CurrentRequest = null;
                    break;
                }

                SafeRaise(ImportStarted, request);

                try
                {
                    var summary = await _pipeline.RunAsync(request, progress, cancellationToken).ConfigureAwait(false);
                    SafeRaise(ImportCompleted, summary);
                }
                catch (OperationCanceledException)
                {
                    lock (_queueGate) _queued.Remove(request.Key);
                    break;
                }
                catch (Exception ex)
                {
                    _log.Error($"导入任务异常：{ex.Message}", ex);
                    SafeRaise(ImportCompleted, ImportSummary.Failed(request, ex.Message, TimeSpan.Zero));
                }
                finally
                {
                    _runGate.Release();
                    lock (_queueGate) _queued.Remove(request.Key);
                    IsBusy = false;
                    CurrentRequest = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SafeRaise<T>(EventHandler<T>? handler, T args)
    {
        try
        {
            handler?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _log.Warn($"事件处理器异常：{ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _runGate.Dispose();
        _pipeline.Dispose();
    }
}
