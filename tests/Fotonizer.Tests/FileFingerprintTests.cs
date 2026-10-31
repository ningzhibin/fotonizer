using Fotonizer.Core.Import;
using Fotonizer.Core.Models;

namespace Fotonizer.Tests;

public class FileFingerprintTests
{
    [Fact]
    public async Task CopyAndHash_ReturnsSameHashAsSource()
    {
        var dir = TestImages.CreateTempDirectory();
        try
        {
            var source = Path.Combine(dir, "a.bin");
            await File.WriteAllBytesAsync(source, Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray());
            var copy = Path.Combine(dir, "b.bin");

            var hash = await FileFingerprint.CopyAndHashAsync(source, copy);
            Assert.Equal(FileFingerprint.Sha256(source), hash);
            Assert.Equal(new FileInfo(source).Length, new FileInfo(copy).Length);
        }
        finally
        {
            TestImages.DeleteDirectory(dir);
        }
    }

    [Fact]
    public void QuickHash_IsStableAndDistinguishesContent()
    {
        var dir = TestImages.CreateTempDirectory();
        try
        {
            var a = Path.Combine(dir, "a.jpg");
            var b = Path.Combine(dir, "b.jpg");
            var c = Path.Combine(dir, "c.jpg");
            File.WriteAllBytes(a, new byte[100_000]);
            File.WriteAllBytes(b, new byte[100_000]);
            File.WriteAllBytes(c, Enumerable.Repeat((byte)7, 100_200).ToArray());

            Assert.Equal(FileFingerprint.QuickHash(a), FileFingerprint.QuickHash(b));
            Assert.NotEqual(FileFingerprint.QuickHash(a), FileFingerprint.QuickHash(c));
        }
        finally
        {
            TestImages.DeleteDirectory(dir);
        }
    }

    [Fact]
    public void Database_DeduplicatesByShaAndQuickHash()
    {
        var dir = TestImages.CreateTempDirectory();
        try
        {
            var db = new ImportDatabase(Path.Combine(dir, "library.db"));
            var record = new ImportedFileRecord
            {
                Sha256 = "abc",
                QuickHash = "quick",
                DestPath = Path.Combine(dir, "x.jpg"),
                FileName = "x.jpg",
                Size = 10,
                CapturedAt = DateTime.Now,
                ImportedAt = DateTime.Now
            };

            Assert.True(db.AddFile(record));
            Assert.False(db.AddFile(record));
            Assert.True(db.ContainsSha("abc"));
            Assert.True(db.ContainsQuickHash("quick"));
            Assert.False(db.ContainsSha("nope"));

            var pending = db.GetPendingUploads(10, 5);
            Assert.Single(pending);

            db.MarkUploaded("abc", "google-1");
            Assert.Empty(db.GetPendingUploads(10, 5));
            Assert.Equal(1, db.GetStats().Uploaded);
        }
        finally
        {
            TestImages.DeleteDirectory(dir);
        }
    }
}
