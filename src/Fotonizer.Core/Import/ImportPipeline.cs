using System.Diagnostics;
using System.IO;
using Fotonizer.Core.Logging;
using Fotonizer.Core.Media;
using Fotonizer.Core.Models;
using Fotonizer.Core.Settings;

namespace Fotonizer.Core.Import;

/// <summary>
/// The heart of Fotonizer: discover → dedupe → analyse bursts → copy/rename/enhance → index for upload.
/// </summary>
public sealed class ImportPipeline : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly ImportDatabase _database;
    private readonly ImageScorer _scorer = new();
    private readonly BurstSelector _burstSelector = new();
    private readonly PortableDeviceImporter _portableImporter = new();

    public ImportPipeline(SettingsStore settings, ImportDatabase database)
    {
        _settings = settings;
        _database = database;
    }

    public Task<ImportSummary> RunAsync(ImportRequest request, IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => RunCoreAsync(request, progress, cancellationToken), cancellationToken);
    }

    private async Task<ImportSummary> RunCoreAsync(ImportRequest request, IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var settings = _settings.Snapshot();
        var found = 0;
        var imported = 0;
        var duplicates = 0;
        var burstGroups = 0;
        var burstSkipped = 0;
        var enhanced = 0;
        var uploadQueued = 0;
        var errors = 0;
        string? tempDirectory = null;

        try
        {
            if (string.IsNullOrWhiteSpace(settings.DestinationRoot))
                throw new InvalidOperationException("还没有设置照片库目录。");
            Directory.CreateDirectory(settings.DestinationRoot);

            progress?.Report(new ImportProgress(ImportStage.Preparing, 0, 0, null,
                $"准备导入 {request}"));

            List<SourceFile> sourceFiles;
            if (request.Kind == ImportSourceKind.PortableDevice)
            {
                var result = await _portableImporter
                    .DownloadAsync(request, settings, _database, progress, cancellationToken)
                    .ConfigureAwait(false);
                sourceFiles = result.Files;
                tempDirectory = result.TempDirectory;
            }
            else
            {
                if (!Directory.Exists(request.SourcePath))
                    throw new DirectoryNotFoundException($"找不到来源目录：{request.SourcePath}");
                sourceFiles = SourceScanner.Scan(request.SourcePath, settings, progress, cancellationToken);
            }

            found = sourceFiles.Count;
            if (found == 0)
            {
                return new ImportSummary(true, request, 0, 0, 0, 0, 0, 0, 0, 0, null,
                    stopwatch.Elapsed);
            }

            // ---- dedupe + read metadata ----
            var metadata = new List<PhotoMetadata>(found);
            var quickHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var batchHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var index = 0;

            foreach (var file in sourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;
                progress?.Report(new ImportProgress(ImportStage.Scanning, index, found,
                    Path.GetFileName(file.Path), "正在读取照片信息…"));

                try
                {
                    var quick = FileFingerprint.QuickHash(file.Path);
                    if (_database.ContainsQuickHash(quick) || _database.IsSkippedHash(quick) ||
                        !batchHashes.Add(quick))
                    {
                        duplicates++;
                        MarkSeen(file);
                        continue;
                    }

                    quickHashes[file.Path] = quick;
                    var isVideo = MediaTypes.IsVideo(file.Path);
                    var meta = MetadataReader.Read(file.Path, isVideo, file.SourceKey);
                    metadata.Add(meta);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors++;
                    AppLog.Shared.Warn($"读取 {Path.GetFileName(file.Path)} 失败：{ex.Message}");
                }
            }

            // ---- burst grouping / best-shot selection ----
            var groups = await _burstSelector.FindAndScoreAsync(metadata, settings, _scorer,
                cancellationToken, progress).ConfigureAwait(false);
            burstGroups = groups.Count;

            var groupMembership = new Dictionary<string, (BurstGroup Group, int Index)>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
            {
                for (var i = 0; i < group.Items.Count; i++)
                    groupMembership[group.Items[i].Metadata.SourcePath] = (group, i);
            }

            // ---- copy / rename / enhance ----
            var processed = 0;
            var counter = 0;
            var deferredRejects = new List<PhotoMetadata>();
            var failedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var meta in metadata)
            {
                cancellationToken.ThrowIfCancellationRequested();
                processed++;
                progress?.Report(new ImportProgress(ImportStage.Importing, processed, metadata.Count,
                    Path.GetFileName(meta.SourcePath), "正在导入…"));

                groupMembership.TryGetValue(meta.SourcePath, out var membership);
                var isBurstBest = membership.Group?.Best != null &&
                                  ReferenceEquals(membership.Group.Best.Metadata, meta);
                var isReject = membership.Group != null && !isBurstBest;

                if (isReject && settings.BurstKeepMode == BurstKeepMode.Skip)
                {
                    // Decide after the winner has been imported, so a failed winner does not
                    // cause the whole burst to be permanently remembered as "skipped".
                    deferredRejects.Add(meta);
                    continue;
                }

                counter++;
                var outcome = await ImportOneAsync(meta, request, settings, quickHashes, membership.Group,
                    isReject, isBurstBest, counter, cancellationToken).ConfigureAwait(false);

                if (isBurstBest && outcome is OneFileOutcome.Failed)
                    failedGroups.Add(membership.Group!.Id);

                switch (outcome)
                {
                    case OneFileOutcome.Imported:
                        imported++;
                        if (!isReject) uploadQueued++;
                        break;
                    case OneFileOutcome.Duplicate:
                        duplicates++;
                        break;
                    case OneFileOutcome.Failed:
                        errors++;
                        break;
                }
            }

            foreach (var meta in deferredRejects)
            {
                if (!groupMembership.TryGetValue(meta.SourcePath, out var membership)) continue;
                if (membership.Group != null && failedGroups.Contains(membership.Group.Id))
                {
                    AppLog.Shared.Warn($"连拍组 {membership.Group.Id} 的最佳照片导入失败，落选照片保留待下次重试。");
                    continue;
                }

                if (quickHashes.TryGetValue(meta.SourcePath, out var quick))
                    _database.MarkSkippedHash(quick, "burst:" + membership.Group?.Id);
                burstSkipped++;
                MarkSeen(new SourceFile { Path = meta.SourcePath, SourceKey = meta.SourceKey });
            }

            AppLog.Shared.Info(
                $"导入完成 {request}：发现 {found}，导入 {imported}，重复 {duplicates}，连拍跳过 {burstSkipped}，错误 {errors}");
            return new ImportSummary(true, request, found, imported, duplicates, burstGroups,
                burstSkipped, enhanced, uploadQueued, errors, null, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            AppLog.Shared.Warn($"导入已取消：{request}");
            return new ImportSummary(false, request, found, imported, duplicates, burstGroups, burstSkipped,
                enhanced, uploadQueued, errors, "已取消", stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            AppLog.Shared.Error($"导入失败 {request}: {ex.Message}", ex);
            return ImportSummary.Failed(request, ex.Message, stopwatch.Elapsed);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempDirectory))
            {
                try
                {
                    if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, recursive: true);
                }
                catch (Exception ex)
                {
                    AppLog.Shared.Debug($"清理临时目录失败：{ex.Message}");
                }
            }
        }

        async Task<OneFileOutcome> ImportOneAsync(PhotoMetadata meta, ImportRequest importRequest,
            AppSettings current, Dictionary<string, string> hashes, BurstGroup? group, bool reject,
            bool isBest, int sequence, CancellationToken ct)
        {
            var tempCopy = "";
            var tempEnhanced = "";
            try
            {
                var effectiveMode = reject ? EnhanceMode.Off : current.EnhanceMode;
                var willEnhance = effectiveMode != EnhanceMode.Off &&
                                  !meta.IsVideo && MediaTypes.IsEnhanceable(meta.SourcePath);

                var outputExtension = willEnhance ? ".jpg" : meta.Extension;

                var relativeFolder = NamingTemplate.RenderRelativeFolder(current.SubfolderTemplate, meta, sequence);
                if (current.OrganizeByDevice)
                    relativeFolder = Path.Combine(
                        NamingTemplate.BuildDeviceFolder(meta,
                            current.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                                ? "Unknown camera"
                                : "未识别相机"),
                        relativeFolder);
                if (reject)
                    relativeFolder = Path.Combine(relativeFolder,
                        NamingTemplate.SanitizeFileName(current.BurstFolderName));
                var destinationDirectory = Path.Combine(current.DestinationRoot, relativeFolder);
                Directory.CreateDirectory(destinationDirectory);

                var fileName = NamingTemplate.BuildFileName(current.FileNameTemplate, meta, sequence, outputExtension);
                if (reject)
                {
                    var groupIndex = group?.Items.FindIndex(i => ReferenceEquals(i.Metadata, meta)) ?? 0;
                    fileName = InsertSuffix(fileName, $"-b{groupIndex + 1}");
                }

                var finalPath = NamingTemplate.EnsureUniquePath(destinationDirectory, fileName);
                tempCopy = Path.Combine(destinationDirectory, ".fotonizer-" + Guid.NewGuid().ToString("N") + ".tmp");

                var sha = await FileFingerprint.CopyAndHashAsync(meta.SourcePath, tempCopy, ct)
                    .ConfigureAwait(false);

                if (_database.ContainsSha(sha))
                {
                    FileFingerprint.TryDelete(tempCopy);
                    MarkSeen(new SourceFile { Path = meta.SourcePath, SourceKey = meta.SourceKey });
                    return OneFileOutcome.Duplicate;
                }

                var finalSource = tempCopy;
                if (willEnhance)
                {
                    tempEnhanced = tempCopy + ".jpg";
                    try
                    {
                        PhotoEnhancer.EnhanceToJpeg(tempCopy, tempEnhanced, effectiveMode, current.JpegQuality);
                        finalSource = tempEnhanced;
                        enhanced++;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Shared.Warn($"自动增强失败，保留原图 {Path.GetFileName(meta.SourcePath)}: {ex.Message}");
                        FileFingerprint.TryDelete(tempEnhanced);
                        tempEnhanced = "";
                    }
                }

                File.Move(finalSource, finalPath, overwrite: false);
                if (!string.IsNullOrEmpty(tempEnhanced)) FileFingerprint.TryDelete(tempCopy);

                ApplyFileTimes(finalPath, meta.CapturedAt);

                var record = new ImportedFileRecord
                {
                    Sha256 = sha,
                    QuickHash = hashes.TryGetValue(meta.SourcePath, out var quick) ? quick : "",
                    SourcePath = meta.SourcePath,
                    DestPath = finalPath,
                    FileName = Path.GetFileName(finalPath),
                    Size = new FileInfo(finalPath).Length,
                    CapturedAt = meta.CapturedAt,
                    ImportedAt = DateTime.Now,
                    UploadState = reject ? UploadState.Skipped : UploadState.Pending,
                    BurstGroup = group?.Id,
                    Score = group?.Items.FirstOrDefault(i => ReferenceEquals(i.Metadata, meta))?.Score?.Total,
                    IsBurstPick = isBest
                };

                if (!_database.AddFile(record))
                {
                    FileFingerprint.TryDelete(finalPath);
                    MarkSeen(new SourceFile { Path = meta.SourcePath, SourceKey = meta.SourceKey });
                    return OneFileOutcome.Duplicate;
                }

                MarkSeen(new SourceFile { Path = meta.SourcePath, SourceKey = meta.SourceKey });

                if (current.DeleteSourceAfterCopy && importRequest.Kind != ImportSourceKind.PortableDevice)
                {
                    try
                    {
                        File.Delete(meta.SourcePath);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Shared.Warn($"删除来源文件失败 {meta.SourcePath}: {ex.Message}");
                    }
                }

                return OneFileOutcome.Imported;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Shared.Error($"导入 {Path.GetFileName(meta.SourcePath)} 失败：{ex.Message}");
                return OneFileOutcome.Failed;
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempCopy)) FileFingerprint.TryDelete(tempCopy);
                if (!string.IsNullOrEmpty(tempEnhanced)) FileFingerprint.TryDelete(tempEnhanced);
            }
        }
    }

    private void MarkSeen(SourceFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.SourceKey)) _database.MarkSourceSeen(file.SourceKey);
    }

    private static string InsertSuffix(string fileName, string suffix)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        return stem + suffix + ext;
    }

    private static void ApplyFileTimes(string path, DateTime capturedAt)
    {
        if (capturedAt.Year is < 1601 or > 9998) return;
        try
        {
            File.SetLastWriteTime(path, capturedAt);
            File.SetCreationTime(path, capturedAt);
        }
        catch
        {
        }
    }

    private enum OneFileOutcome
    {
        Imported,
        Duplicate,
        Failed
    }

    public void Dispose()
    {
        _scorer.Dispose();
    }
}
