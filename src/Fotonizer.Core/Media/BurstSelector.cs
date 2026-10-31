using Fotonizer.Core.Logging;
using Fotonizer.Core.Models;

namespace Fotonizer.Core.Media;

public sealed class BurstItem
{
    public required PhotoMetadata Metadata { get; init; }
    public ImageScore? Score { get; set; }
}

public sealed class BurstGroup
{
    public required string Id { get; init; }
    public required List<BurstItem> Items { get; init; }
    public BurstItem? Best { get; set; }
    public string Reason { get; set; } = "";
}

/// <summary>
/// Finds groups of photos taken in rapid succession and picks the best frame.
/// Groups are formed on capture time (and camera model) so they work for both USB cards
/// and MTP cameras whose filenames carry no burst information.
/// </summary>
public sealed class BurstSelector
{
    private static readonly HashSet<string> ScoreableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".png", ".heic", ".heif", ".tif", ".tiff", ".bmp"
    };

    public async Task<List<BurstGroup>> FindAndScoreAsync(IReadOnlyList<PhotoMetadata> photos,
        AppSettings settings, ImageScorer scorer, CancellationToken cancellationToken,
        IProgress<ImportProgress>? progress = null)
    {
        var groups = new List<BurstGroup>();
        if (!settings.BurstSelection) return groups;

        var ordered = photos
            .Where(p => !p.IsVideo && ScoreableExtensions.Contains(p.Extension))
            .OrderBy(p => p.CapturedAt)
            .ToList();

        var window = TimeSpan.FromSeconds(settings.BurstWindowSeconds);
        var index = 0;
        var groupNumber = 0;
        var scored = 0;
        var totalToScore = 0;

        // First pass: build time clusters.
        var rawGroups = new List<List<PhotoMetadata>>();
        while (index < ordered.Count)
        {
            var cluster = new List<PhotoMetadata> { ordered[index] };
            var next = index + 1;
            while (next < ordered.Count)
            {
                var gap = ordered[next].CapturedAt - cluster[^1].CapturedAt;
                if (gap <= window && SameCamera(cluster[^1], ordered[next]))
                {
                    cluster.Add(ordered[next]);
                    next++;
                }
                else
                {
                    break;
                }
            }

            if (cluster.Count >= settings.BurstMinCount) rawGroups.Add(cluster);
            index = next;
        }

        totalToScore = rawGroups.Sum(g => g.Count);

        foreach (var cluster in rawGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var items = cluster.Select(m => new BurstItem { Metadata = m }).ToList();

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    item.Score = await scorer.ScoreAsync(item.Metadata.SourcePath, item.Metadata, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    item.Score = new ImageScore { Error = ex.Message };
                }

                scored++;
                progress?.Report(new ImportProgress(ImportStage.Analyzing, scored, totalToScore,
                    Path.GetFileName(item.Metadata.SourcePath), "正在比较连拍照片…"));
            }

            var usable = items.Where(i => i.Score is { Error: null }).ToList();
            if (usable.Count == 0)
                usable = items;

            var sharpMax = usable.Max(i => i.Score!.Sharpness);
            var faceSharpMax = usable.Max(i => i.Score!.FaceSharpness);
            var anyFaces = usable.Any(i => i.Score!.HasFaces);

            foreach (var item in usable)
            {
                var score = item.Score!;
                var sharpNorm = sharpMax > 0 ? score.Sharpness / sharpMax : 0;
                var faceSharpNorm = faceSharpMax > 0 ? score.FaceSharpness / faceSharpMax : 0;
                score.Total = anyFaces
                    ? 0.40 * sharpNorm + 0.15 * score.Exposure + 0.25 * score.FaceScore + 0.20 * faceSharpNorm
                    : 0.65 * sharpNorm + 0.35 * score.Exposure;
            }

            var best = usable
                .OrderByDescending(i => i.Score!.Total)
                .ThenByDescending(i => i.Score!.Sharpness)
                .ThenBy(i => i.Metadata.CapturedAt)
                .ThenBy(i => i.Metadata.SourcePath, StringComparer.OrdinalIgnoreCase)
                .First();

            groupNumber++;
            var group = new BurstGroup
            {
                Id = $"{best.Metadata.CapturedAt:yyyyMMdd-HHmmss}-{groupNumber}",
                Items = items,
                Best = best,
                Reason = anyFaces
                    ? $"人脸 {best.Score!.FaceCount} 张，清晰度 {best.Score.Sharpness:F0}，曝光 {best.Score.Exposure:F2}"
                    : $"清晰度 {best.Score!.Sharpness:F0}，曝光 {best.Score.Exposure:F2}"
            };
            groups.Add(group);

            AppLog.Shared.Info(
                $"连拍组 {group.Id}: 共 {items.Count} 张，选中 {Path.GetFileName(best.Metadata.SourcePath)}（{group.Reason}）");
        }

        return groups;
    }

    private static bool SameCamera(PhotoMetadata a, PhotoMetadata b) =>
        string.Equals(a.Camera, b.Camera, StringComparison.OrdinalIgnoreCase);
}
