using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Fotonizer.Core.Settings;
using Fotonizer.Core.Upload;

namespace Fotonizer.Tests;

public class GooglePhotosClientTests
{
    [Fact]
    public async Task Upload_SendsRawUploadAndBatchCreate()
    {
        var dir = TestImages.CreateTempDirectory();
        try
        {
            var settings = new SettingsStore(Path.Combine(dir, "settings.json"));
            settings.Current.GoogleClientId = "client-id";
            settings.Save();

            var secrets = new SecretStore(Path.Combine(dir, "secrets.bin"));
            secrets.Update(c =>
            {
                c.ClientSecret = "client-secret";
                c.RefreshToken = "refresh-token";
            });

            var handler = new FakeHandler();
            using var client = new GooglePhotosClient(settings, secrets, new HttpClient(handler));

            var file = Path.Combine(dir, "photo.jpg");
            await File.WriteAllBytesAsync(file, new byte[] { 1, 2, 3, 4 });

            var id = await client.UploadAsync(file, "photo.jpg", "album-1", CancellationToken.None);

            Assert.Equal("media-1", id);
            Assert.Contains(handler.Requests, r => r.Uri.Contains("oauth2.googleapis.com/token"));
            Assert.Contains(handler.Requests, r => r.Uri.EndsWith("/v1/uploads"));
            Assert.Contains(handler.Requests, r => r.Uri.EndsWith("/v1/mediaItems:batchCreate"));

            var upload = handler.Requests.Single(r => r.Uri.EndsWith("/v1/uploads"));
            Assert.Equal("raw", upload.Headers["X-Goog-Upload-Protocol"]);
            Assert.Equal("image/jpeg", upload.Headers["X-Goog-Upload-Content-Type"]);
            Assert.StartsWith("Bearer ", upload.Headers["Authorization"]);

            var batch = handler.Requests.Single(r => r.Uri.EndsWith("/v1/mediaItems:batchCreate"));
            using var json = JsonDocument.Parse(batch.Body!);
            Assert.Equal("album-1", json.RootElement.GetProperty("albumId").GetString());
            var item = json.RootElement.GetProperty("newMediaItems")[0];
            Assert.Equal("photo.jpg", item.GetProperty("simpleMediaItem").GetProperty("fileName").GetString());
            Assert.Equal("upload-token", item.GetProperty("simpleMediaItem").GetProperty("uploadToken").GetString());
        }
        finally
        {
            TestImages.DeleteDirectory(dir);
        }
    }

    [Fact]
    public async Task CreateAlbum_ReturnsId()
    {
        var dir = TestImages.CreateTempDirectory();
        try
        {
            var settings = new SettingsStore(Path.Combine(dir, "settings.json"));
            settings.Current.GoogleClientId = "client-id";
            settings.Save();
            var secrets = new SecretStore(Path.Combine(dir, "secrets.bin"));
            secrets.SetRefreshToken("refresh-token");

            var handler = new FakeHandler();
            using var client = new GooglePhotosClient(settings, secrets, new HttpClient(handler));

            var id = await client.CreateAlbumAsync("2024-05", CancellationToken.None);
            Assert.Equal("album-1", id);
        }
        finally
        {
            TestImages.DeleteDirectory(dir);
        }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value),
                StringComparer.OrdinalIgnoreCase);
            Requests.Add(new RecordedRequest(request.RequestUri!.ToString(), headers, body));

            var uri = request.RequestUri.ToString();
            if (uri.Contains("oauth2.googleapis.com/token"))
                return Json("""{"access_token":"access-token","expires_in":3600,"token_type":"Bearer"}""");
            if (uri.EndsWith("/v1/uploads"))
                return Text("upload-token");
            if (uri.EndsWith("/v1/albums"))
                return Json("""{"id":"album-1","title":"2024-05"}""");
            if (uri.EndsWith("/v1/mediaItems:batchCreate"))
                return Json("""
                    {"newMediaItemResults":[{"status":{"message":"Success","code":0},"mediaItem":{"id":"media-1"}}]}
                    """);

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
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

    public sealed record RecordedRequest(string Uri, Dictionary<string, string> Headers, string? Body);
}
