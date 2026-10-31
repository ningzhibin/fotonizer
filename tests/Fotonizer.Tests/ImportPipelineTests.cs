using Fotonizer.Core.Import;
using Fotonizer.Core.Models;
using Fotonizer.Core.Settings;

namespace Fotonizer.Tests;

public class ImportPipelineTests
{
    [Fact]
    public async Task ImportsRenamesDeduplicatesAndPicksBurstBest()
    {
        var root = TestImages.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "source");
            var destination = Path.Combine(root, "library");
            var data = Path.Combine(root, "data");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(destination);

            var baseTime = new DateTime(2024, 5, 1, 10, 15, 0);
            var sharp = TestImages.CreateBgra(400, 300, (x, y) =>
            {
                var v = (x * 31 + y * 57) % 255;
                return ((byte)v, (byte)(255 - v), (byte)((x + y) % 255));
            });

            var burstFiles = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var pixels = i == 0 ? sharp : TestImages.BoxBlur(sharp, 400, 300, i * 2);
                var path = Path.Combine(source, $"BURST_{i}.JPG");
                TestImages.WriteJpeg(path, pixels, 400, 300);
                File.SetLastWriteTime(path, baseTime.AddMilliseconds(i * 150));
                File.SetCreationTime(path, baseTime.AddMilliseconds(i * 150));
                burstFiles.Add(path);
            }

            var single = Path.Combine(source, "SINGLE.JPG");
            TestImages.WriteJpeg(single, TestImages.CreateBgra(300, 200, (x, y) => ((byte)x, (byte)y, 120)), 300, 200);
            File.SetLastWriteTime(single, new DateTime(2024, 5, 1, 8, 0, 0));
            File.SetCreationTime(single, new DateTime(2024, 5, 1, 8, 0, 0));

            File.Copy(single, Path.Combine(source, "SINGLE_COPY.JPG"));

            var settingsPath = Path.Combine(data, "settings.json");
            var store = new SettingsStore(settingsPath);
            store.Current.DestinationRoot = destination;
            store.Current.SubfolderTemplate = @"{yyyy}\{MM}";
            store.Current.FileNameTemplate = "{yyyy}-{MM}-{dd}-{HH}{mm}{ss}";
            store.Current.EnhanceMode = EnhanceMode.Off;
            store.Current.BurstSelection = true;
            store.Current.BurstMinCount = 3;
            store.Current.BurstWindowSeconds = 2;
            store.Current.CopyVideos = false;
            store.Current.UploadEnabled = false;
            store.Current.OrganizeByDevice = true;
            store.Save();

            using var database = new ImportDatabase(Path.Combine(data, "library.db"));
            using var pipeline = new ImportPipeline(store, database);

            var summary = await pipeline.RunAsync(new ImportRequest
            {
                Kind = ImportSourceKind.Folder,
                SourcePath = source,
                DisplayName = "test"
            }, null, CancellationToken.None);

            Assert.True(summary.Success, summary.Error);
            Assert.Equal(5, summary.Found);
            Assert.Equal(2, summary.Imported);
            Assert.Equal(1, summary.Duplicates);
            Assert.Equal(1, summary.BurstGroups);
            Assert.Equal(2, summary.BurstSkipped);
            Assert.Equal(0, summary.Errors);

            var files = Directory.GetFiles(destination, "*.jpg", SearchOption.AllDirectories);
            Assert.Equal(2, files.Length);
            Assert.All(files, f => Assert.Contains("未识别相机", f));
            Assert.Contains(files, f => Path.GetFileName(f) == "2024-05-01-101500.jpg");
            Assert.Contains(files, f => Path.GetFileName(f) == "2024-05-01-080000.jpg");

            var all = database.GetStats();
            Assert.Equal(2, all.TotalFiles);
            Assert.Equal(2, all.PendingUpload);

            // second run: everything is a duplicate
            var second = await pipeline.RunAsync(new ImportRequest
            {
                Kind = ImportSourceKind.Folder,
                SourcePath = source,
                DisplayName = "test"
            }, null, CancellationToken.None);

            Assert.True(second.Success);
            Assert.Equal(0, second.Imported);
            Assert.Equal(5, second.Duplicates);
        }
        finally
        {
            TestImages.DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task KeepBurstRejects_CopiesThemIntoSubfolder()
    {
        var root = TestImages.CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "source");
            var destination = Path.Combine(root, "library");
            Directory.CreateDirectory(source);

            var time = new DateTime(2024, 6, 2, 12, 0, 0);
            var sharp = TestImages.CreateBgra(320, 240, (x, y) => ((byte)(x % 255), (byte)(y % 255), 30));
            for (var i = 0; i < 3; i++)
            {
                var path = Path.Combine(source, $"IMG_{i}.JPG");
                TestImages.WriteJpeg(path, i == 0 ? sharp : TestImages.BoxBlur(sharp, 320, 240, i * 2), 320, 240);
                File.SetLastWriteTime(path, time.AddMilliseconds(i * 100));
            }

            var store = new SettingsStore(Path.Combine(root, "settings.json"));
            store.Current.DestinationRoot = destination;
            store.Current.EnhanceMode = EnhanceMode.Off;
            store.Current.BurstMinCount = 3;
            store.Current.BurstKeepMode = BurstKeepMode.KeepInSubfolder;
            store.Save();

            using var database = new ImportDatabase(Path.Combine(root, "library.db"));
            using var pipeline = new ImportPipeline(store, database);

            var summary = await pipeline.RunAsync(new ImportRequest
            {
                Kind = ImportSourceKind.Folder,
                SourcePath = source,
                DisplayName = "test"
            }, null, CancellationToken.None);

            Assert.True(summary.Success, summary.Error);
            Assert.Equal(3, summary.Imported);
            var burstDir = Directory.GetDirectories(destination, "_Burst", SearchOption.AllDirectories);
            Assert.Single(burstDir);
            Assert.Equal(2, Directory.GetFiles(burstDir[0]).Length);
        }
        finally
        {
            TestImages.DeleteDirectory(root);
        }
    }
}
