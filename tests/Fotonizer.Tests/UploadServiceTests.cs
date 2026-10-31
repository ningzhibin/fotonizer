using System.Net;
using System.Net.Http;
using System.Text;
using Fotonizer.Core.Import;
using Fotonizer.Core.Models;
using Fotonizer.Core.Settings;
using Fotonizer.Core.Upload;

namespace Fotonizer.Tests;

public class UploadServiceTests
{
    [Fact]
    public void PendingUploads_AreOrderedSmallestFirst()
    {
        var dir = TestImages.CreateTempDirectory();
        try
        {
            var db = new ImportDatabase(Path.Combine(dir, "library.db"));
            Add(db, dir, "big.mp4", 5_000_000);
            Add(db, dir, "small.jpg", 1000);

            var pending = db.GetPendingUploads(10, 5);
            Assert.Equal(2, pending.Count);
            Assert.Equal("small.jpg", pending[0].FileName);
            Assert.Equal("big.mp4", pending[1].FileName);
        }
        finally
        {
            TestImages.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task UploadTimeout_DoesNotStopTheUploadWorker()
    {
        var dir = TestImages.CreateTempDirectory();
        try
        {
            var settings = new SettingsStore(Path.Combine(dir, "settings.json"));
            settings.Current.GoogleClientId = "client-id";
            settings.Current.UploadEnabled = true;
            settings.Current.UploadRetryLimit = 5;
            settings.Current.AlbumTemplate = "";
            settings.Save();

            var secrets = new SecretStore(Path.Combine(dir, "secrets.bin"));
            secrets.SetRefreshToken("refresh-token");

            var database = new ImportDatabase(Path.Combine(dir, "library.db"));
            var file = Path.Combine(dir, "photo.jpg");
            await File.WriteAllBytesAsync(file, new byte[1024]);

            database.AddFile(new ImportedFileRecord
            {
                Sha256 = "sha-1",
                QuickHash = "quick-1",
                DestPath = file,
                FileName = "photo.jpg",
                Size = 1024,
                CapturedAt = DateTime.Now,
                ImportedAt = DateTime.Now,
                UploadState = UploadState.Pending
            });

            var handler = new TimeoutThenSuccessHandler();
            using var client = new GooglePhotosClient(settings, secrets, new HttpClient(handler));
            var service = new UploadService(settings, database, client);
            service.Start();

            var deadline = DateTime.UtcNow.AddSeconds(15);
            var uploaded = false;
            while (DateTime.UtcNow < deadline)
            {
                var stats = database.GetStats();
                if (stats.Uploaded == 1)
                {
                    uploaded = true;
                    break;
                }

                await Task.Delay(250);
            }

            await service.StopAsync();
            service.Dispose();
            database.Dispose();

            Assert.True(uploaded, "Upload worker should recover from a per-request timeout and retry.");
            Assert.True(handler.UploadAttempts >= 2);
        }
        finally
        {
            TestImages.DeleteDirectory(dir);
        }
    }

    private static void Add(ImportDatabase db, string dir, string name, long size)
    {
        db.AddFile(new ImportedFileRecord
        {
            Sha256 = name,
            QuickHash = name,
            DestPath = Path.Combine(dir, name),
            FileName = name,
            Size = size,
            CapturedAt = DateTime.Now,
            ImportedAt = DateTime.Now
        });
    }

    private sealed class TimeoutThenSuccessHandler : HttpMessageHandler
    {
        private int _uploadAttempts;

        public int UploadAttempts => _uploadAttempts;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            if (uri.Contains("oauth2.googleapis.com/token"))
                return Task.FromResult(Json("""{"access_token":"access-token","expires_in":3600,"token_type":"Bearer"}"""));

            if (uri.EndsWith("/v1/uploads"))
            {
                var attempt = Interlocked.Increment(ref _uploadAttempts);
                if (attempt == 1)
                {
                    // Simulates HttpClient's per-request timeout: a TaskCanceledException
                    // that is NOT caused by the application's cancellation token.
                    return Task.FromException<HttpResponseMessage>(
                        new TaskCanceledException("simulated upload timeout"));
                }

                return Task.FromResult(Text("upload-token"));
            }

            if (uri.EndsWith("/v1/mediaItems:batchCreate"))
                return Task.FromResult(Json("""
                    {"newMediaItemResults":[{"status":{"message":"Success","code":0},"mediaItem":{"id":"media-1"}}]}
                    """));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(text, Encoding.UTF8, "text/plain")
        };
    }
}
