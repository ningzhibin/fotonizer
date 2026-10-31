using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fotonizer.Core.Logging;
using Fotonizer.Core.Settings;

namespace Fotonizer.Core.Upload;

public sealed class GooglePhotosException : Exception
{
    public HttpStatusCode StatusCode { get; }

    public GooglePhotosException(HttpStatusCode statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// Minimal Google Photos Library API client: raw upload + mediaItems:batchCreate + album creation.
/// Uses the append-only scope, so the app can only see/manage media it uploaded itself.
/// </summary>
public sealed class GooglePhotosClient : IDisposable
{
    private const string ApiBase = "https://photoslibrary.googleapis.com/v1";
    private static readonly TimeSpan MinRequestInterval = TimeSpan.FromMilliseconds(350);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly SettingsStore _settings;
    private readonly SecretStore _secrets;
    private readonly GooglePhotosAuth _auth;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private DateTimeOffset _lastRequest = DateTimeOffset.MinValue;
    private string? _accessToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public bool NeedsReauthorization { get; private set; }

    public GooglePhotosClient(SettingsStore settings, SecretStore secrets, HttpClient? http = null)
    {
        _settings = settings;
        _secrets = secrets;
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _auth = new GooglePhotosAuth(_http);
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_settings.Current.GoogleClientId) && _secrets.HasRefreshToken;

    public bool HasAccessToken => _accessToken != null && _expiresAt > DateTimeOffset.UtcNow;

    public async Task AuthorizeInteractiveAsync(string clientId, string? clientSecret,
        CancellationToken cancellationToken)
    {
        var result = await _auth.AuthorizeAsync(clientId, clientSecret, cancellationToken).ConfigureAwait(false);
        _secrets.Update(credentials =>
        {
            credentials.ClientSecret = string.IsNullOrWhiteSpace(clientSecret) ? null : clientSecret;
            if (!string.IsNullOrWhiteSpace(result.RefreshToken))
                credentials.RefreshToken = result.RefreshToken;
        });
        _accessToken = result.AccessToken;
        _expiresAt = result.ExpiresAt;
        NeedsReauthorization = false;
        AppLog.Shared.Info("Google 照片授权成功。");
    }

    public void SignOut()
    {
        _secrets.SetRefreshToken(null);
        _accessToken = null;
        _expiresAt = DateTimeOffset.MinValue;
        NeedsReauthorization = false;
    }

    public async Task EnsureAuthorizedAsync(CancellationToken cancellationToken)
    {
        if (_accessToken != null && _expiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return;

        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_accessToken != null && _expiresAt > DateTimeOffset.UtcNow.AddMinutes(2)) return;

            var clientId = _settings.Current.GoogleClientId;
            if (string.IsNullOrWhiteSpace(clientId))
                throw new GoogleAuthException("请先填写 Google OAuth 客户端 ID。");

            var refreshToken = _secrets.Credentials.RefreshToken;
            if (string.IsNullOrWhiteSpace(refreshToken))
                throw new GoogleAuthException("还没有连接 Google 照片。");

            try
            {
                var result = await _auth.RefreshAsync(clientId, _secrets.Credentials.ClientSecret,
                    refreshToken, cancellationToken).ConfigureAwait(false);
                _accessToken = result.AccessToken;
                _expiresAt = result.ExpiresAt;
                NeedsReauthorization = false;
            }
            catch (GoogleAuthException ex)
            {
                if (ex.InvalidGrant) NeedsReauthorization = true;
                _accessToken = null;
                throw;
            }
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    public async Task ValidateAsync(CancellationToken cancellationToken)
    {
        await EnsureAuthorizedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Uploads a file and returns the Google media item id.</summary>
    public async Task<string> UploadAsync(string filePath, string displayName, string? albumId,
        CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureAuthorizedAsync(cancellationToken).ConfigureAwait(false);

            // Raw uploads can be several GB (videos are allowed up to 20 GB). A fixed
            // HttpClient timeout of a few minutes would abort them and, in older versions,
            // even stop the upload worker. The long per-request limit is only a safety net;
            // normal cancellation (app exit) still comes through the caller's token.
            using var uploadTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            uploadTimeout.CancelAfter(TimeSpan.FromHours(12));
            var uploadToken = uploadTimeout.Token;

            string token;
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                       bufferSize: 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var content = new StreamContent(stream))
            {
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using var request = new HttpRequestMessage(HttpMethod.Post, ApiBase + "/uploads");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                request.Headers.TryAddWithoutValidation("X-Goog-Upload-Protocol", "raw");
                request.Headers.TryAddWithoutValidation("X-Goog-Upload-Content-Type", GetMimeType(filePath));
                request.Headers.TryAddWithoutValidation("X-Goog-Upload-File-Name", SanitizeHeader(displayName));
                request.Content = content;

                await ThrottleAsync(uploadToken).ConfigureAwait(false);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    uploadToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(uploadToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                        NeedsReauthorization = true;
                    throw new GooglePhotosException(response.StatusCode, ParseError(body, response.StatusCode));
                }

                token = body.Trim();
                if (string.IsNullOrWhiteSpace(token))
                    throw new GooglePhotosException(response.StatusCode, "Google 没有返回上传令牌。");
            }

            var payload = new BatchCreateRequest
            {
                AlbumId = string.IsNullOrWhiteSpace(albumId) ? null : albumId,
                NewMediaItems = new[]
                {
                    new NewMediaItem
                    {
                        Description = "",
                        SimpleMediaItem = new SimpleMediaItem
                        {
                            FileName = displayName,
                            UploadToken = token
                        }
                    }
                }
            };

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var batchRequest = new HttpRequestMessage(HttpMethod.Post, ApiBase + "/mediaItems:batchCreate")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            batchRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

            using var batchTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            batchTimeout.CancelAfter(TimeSpan.FromMinutes(2));

            await ThrottleAsync(batchTimeout.Token).ConfigureAwait(false);
            using var batchResponse = await _http.SendAsync(batchRequest, batchTimeout.Token).ConfigureAwait(false);
            var batchBody = await batchResponse.Content.ReadAsStringAsync(batchTimeout.Token).ConfigureAwait(false);

            if (!batchResponse.IsSuccessStatusCode)
            {
                if (batchResponse.StatusCode == HttpStatusCode.Unauthorized) NeedsReauthorization = true;
                throw new GooglePhotosException(batchResponse.StatusCode,
                    ParseError(batchBody, batchResponse.StatusCode));
            }

            using var document = JsonDocument.Parse(batchBody);
            var results = document.RootElement.GetProperty("newMediaItemResults");
            if (results.GetArrayLength() == 0)
                throw new GooglePhotosException(batchResponse.StatusCode, "Google 返回了空的上传结果。");

            var first = results[0];
            var statusMessage = "unknown";
            var statusCode = 0;
            if (first.TryGetProperty("status", out var status))
            {
                if (status.TryGetProperty("message", out var messageElement))
                    statusMessage = messageElement.GetString() ?? statusMessage;
                if (status.TryGetProperty("code", out var codeElement))
                {
                    if (codeElement.ValueKind == JsonValueKind.Number) statusCode = codeElement.GetInt32();
                    else if (codeElement.ValueKind == JsonValueKind.String &&
                             int.TryParse(codeElement.GetString(), out var parsed)) statusCode = parsed;
                }
            }

            if (statusCode != 0)
                throw new GooglePhotosException(batchResponse.StatusCode,
                    $"Google 拒绝了这张照片：{statusMessage}");

            if (first.TryGetProperty("mediaItem", out var mediaItem) &&
                mediaItem.TryGetProperty("id", out var idElement))
                return idElement.GetString() ?? "";

            throw new GooglePhotosException(batchResponse.StatusCode,
                $"Google 没有返回媒体项 ID：{statusMessage}");
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public async Task<string> CreateAlbumAsync(string title, CancellationToken cancellationToken)
    {
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureAuthorizedAsync(cancellationToken).ConfigureAwait(false);

            var safeTitle = title.Length > 100 ? title[..100] : title;
            var json = JsonSerializer.Serialize(new { album = new { title = safeTitle } }, JsonOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiBase + "/albums")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

            using var albumTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            albumTimeout.CancelAfter(TimeSpan.FromMinutes(2));

            await ThrottleAsync(albumTimeout.Token).ConfigureAwait(false);
            using var response = await _http.SendAsync(request, albumTimeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(albumTimeout.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized) NeedsReauthorization = true;
                throw new GooglePhotosException(response.StatusCode, ParseError(body, response.StatusCode));
            }

            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("id", out var idElement))
                return idElement.GetString() ?? "";

            throw new GooglePhotosException(response.StatusCode, "创建相册失败：Google 没有返回相册 ID。");
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task ThrottleAsync(CancellationToken cancellationToken)
    {
        var elapsed = DateTimeOffset.UtcNow - _lastRequest;
        if (elapsed < MinRequestInterval)
            await Task.Delay(MinRequestInterval - elapsed, cancellationToken).ConfigureAwait(false);
        _lastRequest = DateTimeOffset.UtcNow;
    }

    private static string ParseError(string body, HttpStatusCode status)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(message))
                    return status switch
                    {
                        HttpStatusCode.TooManyRequests => "Google 照片请求过于频繁，稍后会自动重试。",
                        HttpStatusCode.Forbidden => $"没有权限：{message}",
                        HttpStatusCode.Unauthorized => "Google 授权已过期，请重新连接。",
                        _ => message!
                    };
            }
        }
        catch
        {
        }

        return $"Google 照片请求失败（HTTP {(int)status}）。";
    }

    internal static string GetMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".jpe" => "image/jpeg",
        ".png" => "image/png",
        ".heic" => "image/heic",
        ".heif" => "image/heif",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".tif" or ".tiff" => "image/tiff",
        ".bmp" => "image/bmp",
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".avi" => "video/avi",
        ".mkv" => "video/x-matroska",
        ".3gp" => "video/3gpp",
        ".wmv" => "video/x-ms-wmv",
        ".mts" or ".m2ts" => "video/mp2t",
        _ => "application/octet-stream"
    };

    private static string SanitizeHeader(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
            builder.Append(ch is >= ' ' and <= '~' ? ch : '_');
        return builder.ToString();
    }

    private sealed class BatchCreateRequest
    {
        [JsonPropertyName("albumId")] public string? AlbumId { get; set; }
        [JsonPropertyName("newMediaItems")] public NewMediaItem[] NewMediaItems { get; set; } = Array.Empty<NewMediaItem>();
    }

    private sealed class NewMediaItem
    {
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("simpleMediaItem")] public SimpleMediaItem SimpleMediaItem { get; set; } = new();
    }

    private sealed class SimpleMediaItem
    {
        [JsonPropertyName("fileName")] public string FileName { get; set; } = "";
        [JsonPropertyName("uploadToken")] public string UploadToken { get; set; } = "";
    }

    public void Dispose()
    {
        _http.Dispose();
        _auth.Dispose();
        _requestGate.Dispose();
        _tokenGate.Dispose();
    }
}
