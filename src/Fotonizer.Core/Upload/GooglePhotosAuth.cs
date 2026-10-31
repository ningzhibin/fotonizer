using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fotonizer.Core.Logging;

namespace Fotonizer.Core.Upload;

public sealed class GoogleAuthException : Exception
{
    public bool InvalidGrant { get; }

    public GoogleAuthException(string message, bool invalidGrant = false) : base(message)
    {
        InvalidGrant = invalidGrant;
    }

    public GoogleAuthException(string message, Exception inner) : base(message, inner)
    {
    }
}

public sealed class GoogleAuthResult
{
    public required string AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public string? Scope { get; init; }
}

/// <summary>
/// Google OAuth 2.0 for installed applications: loopback redirect + PKCE.
/// Requested scope is photoslibrary.appendonly (upload only), the least invasive scope.
///
/// The callback server is a tiny hand-written HTTP/1.1 server on TcpListener instead of
/// HttpListener. HttpListener can raise an unobserved ObjectDisposedException after the
/// listener is closed while the browser is still finishing the connection; TcpListener
/// gives us full control over the lifetime and avoids that spurious background error.
/// </summary>
public sealed class GooglePhotosAuth : IDisposable
{
    public const string Scope = "https://www.googleapis.com/auth/photoslibrary.appendonly";

    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public GooglePhotosAuth(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _ownsHttp = http == null;
    }

    public async Task<GoogleAuthResult> AuthorizeAsync(string clientId, string? clientSecret,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new GoogleAuthException("请先填写 Google OAuth 客户端 ID。");

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Guid.NewGuid().ToString("N");

        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            throw new GoogleAuthException($"无法监听本地回调端口：{ex.Message}", ex);
        }

        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var redirectUri = $"http://127.0.0.1:{port}/";

            var query = new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["redirect_uri"] = redirectUri,
                ["response_type"] = "code",
                ["scope"] = Scope,
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256",
                ["state"] = state,
                ["access_type"] = "offline",
                ["prompt"] = "consent",
                ["include_granted_scopes"] = "true"
            };

            var authorizationUrl = AuthEndpoint + "?" +
                                   string.Join("&", query.Select(kv =>
                                       $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            AppLog.Shared.Info("正在打开浏览器进行 Google 照片授权…");
            try
            {
                Process.Start(new ProcessStartInfo(authorizationUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                throw new GoogleAuthException("无法打开浏览器，请手动访问授权链接。", ex);
            }

            var callback = await WaitForCallbackAsync(listener, cancellationToken).ConfigureAwait(false);
            var error = callback.GetValueOrDefault("error");
            var code = callback.GetValueOrDefault("code");
            var returnedState = callback.GetValueOrDefault("state");

            if (!string.Equals(returnedState, state, StringComparison.Ordinal))
                throw new GoogleAuthException("授权回调的 state 不匹配，已中止（可能是安全风险）。");
            if (!string.IsNullOrEmpty(error))
                throw new GoogleAuthException(error == "access_denied"
                    ? "你取消了 Google 授权。"
                    : $"Google 授权失败：{error}");
            if (string.IsNullOrWhiteSpace(code))
                throw new GoogleAuthException("Google 没有返回授权码，请重试。");

            var form = new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = clientId,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = verifier
            };
            if (!string.IsNullOrWhiteSpace(clientSecret)) form["client_secret"] = clientSecret;

            return await PostTokenAsync(form, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                listener.Stop();
            }
            catch
            {
            }
        }
    }

    public async Task<GoogleAuthResult> RefreshAsync(string clientId, string? clientSecret,
        string refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new GoogleAuthException("还没有 Google 授权，请先点击“连接 Google 照片”。");

        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token"
        };
        if (!string.IsNullOrWhiteSpace(clientSecret)) form["client_secret"] = clientSecret;

        return await PostTokenAsync(form, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GoogleAuthResult> PostTokenAsync(Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(TimeSpan.FromMinutes(2));

        using var response = await _http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form),
            requestTimeout.Token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(requestTimeout.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var message = ParseErrorMessage(body, out var invalidGrant);
            throw new GoogleAuthException(message, invalidGrant);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var accessToken = root.GetProperty("access_token").GetString();
            if (string.IsNullOrWhiteSpace(accessToken))
                throw new GoogleAuthException("Google 没有返回访问令牌。");

            var expiresIn = root.TryGetProperty("expires_in", out var expiresElement)
                ? expiresElement.GetInt32()
                : 3600;

            return new GoogleAuthResult
            {
                AccessToken = accessToken!,
                RefreshToken = root.TryGetProperty("refresh_token", out var refreshElement)
                    ? refreshElement.GetString()
                    : null,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn) - 60),
                Scope = root.TryGetProperty("scope", out var scopeElement) ? scopeElement.GetString() : null
            };
        }
        catch (GoogleAuthException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new GoogleAuthException($"解析 Google 令牌响应失败：{ex.Message}", ex);
        }
    }

    private static string ParseErrorMessage(string body, out bool invalidGrant)
    {
        invalidGrant = false;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var error = root.TryGetProperty("error", out var errorElement)
                ? errorElement.GetString()
                : null;
            var description = root.TryGetProperty("error_description", out var descriptionElement)
                ? descriptionElement.GetString()
                : null;

            if (string.Equals(error, "invalid_grant", StringComparison.OrdinalIgnoreCase))
            {
                invalidGrant = true;
                return "Google 授权已失效，请重新连接 Google 照片。";
            }

            return string.IsNullOrWhiteSpace(description)
                ? $"Google 授权失败（{error ?? "未知错误"}）。"
                : $"{description} ({error})";
        }
        catch
        {
            return body.Length > 300 ? body[..300] : body;
        }
    }

    private static async Task<Dictionary<string, string>> WaitForCallbackAsync(TcpListener listener,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));

        try
        {
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
                client.NoDelay = true;

                var query = await ReadRequestQueryAsync(client, timeout.Token).ConfigureAwait(false);
                if (query == null)
                {
                    // favicon or a stray request; answer politely and keep waiting for the callback
                    await WriteHttpResponseAsync(client, 404, "<h2>Not found</h2>", timeout.Token)
                        .ConfigureAwait(false);
                    continue;
                }

                if (query.ContainsKey("code") || query.ContainsKey("error"))
                {
                    var isError = query.ContainsKey("error");
                    var html = isError
                        ? $"<h2>授权未完成</h2><p>{WebUtility.HtmlEncode(query["error"])}</p>"
                        : "<h2>授权成功</h2><p>可以关闭此窗口，返回 Fotonizer。</p>";
                    await WriteHttpResponseAsync(client, 200, html, timeout.Token).ConfigureAwait(false);
                    return query;
                }

                await WriteHttpResponseAsync(client, 404, "<h2>Not found</h2>", timeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GoogleAuthException("授权超时（5 分钟），请重试。");
        }
    }

    private static async Task<Dictionary<string, string>?> ReadRequestQueryAsync(TcpClient client,
        CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var buffer = new byte[8192];
        var builder = new StringBuilder();

        while (builder.Length < 16 * 1024)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) break;

            builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
            if (builder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }

        var request = builder.ToString();
        var lineEnd = request.IndexOf("\r\n", StringComparison.Ordinal);
        var requestLine = lineEnd >= 0 ? request[..lineEnd] : request;
        var parts = requestLine.Split(' ');
        if (parts.Length < 2 || !string.Equals(parts[0], "GET", StringComparison.OrdinalIgnoreCase))
            return null;

        var target = parts[1];
        if (target.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase)) return null;

        var questionMark = target.IndexOf('?');
        var queryText = questionMark >= 0 ? target[(questionMark + 1)..] : "";
        return ParseQuery(queryText);
    }

    private static Dictionary<string, string> ParseQuery(string queryText)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in queryText.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var rawKey = equals >= 0 ? pair[..equals] : pair;
            var rawValue = equals >= 0 ? pair[(equals + 1)..] : "";

            try
            {
                var key = Uri.UnescapeDataString(rawKey.Replace('+', ' '));
                var value = Uri.UnescapeDataString(rawValue.Replace('+', ' '));
                if (key.Length > 0) result[key] = value;
            }
            catch (UriFormatException)
            {
                // ignore malformed pairs
            }
        }

        return result;
    }

    private static async Task WriteHttpResponseAsync(TcpClient client, int statusCode, string html,
        CancellationToken cancellationToken)
    {
        try
        {
            var page = "<!doctype html><html><head><meta charset=\"utf-8\"><title>Fotonizer</title>" +
                       "<style>body{font-family:Segoe UI,sans-serif;background:#f6f7fb;color:#1b2430;" +
                       "display:flex;align-items:center;justify-content:center;height:100vh;margin:0}" +
                       ".card{background:#fff;border:1px solid #e6e9f0;border-radius:14px;padding:38px 46px;" +
                       "box-shadow:0 10px 30px rgba(20,40,90,.08);text-align:center}</style></head><body>" +
                       $"<div class=\"card\">{html}</div></body></html>";
            var body = Encoding.UTF8.GetBytes(page);
            var statusText = statusCode == 200 ? "OK" : "Not Found";
            var header = $"HTTP/1.1 {statusCode} {statusText}\r\n" +
                         "Content-Type: text/html; charset=utf-8\r\n" +
                         $"Content-Length: {body.Length}\r\n" +
                         "Connection: close\r\n\r\n";

            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // browser may have disconnected early
        }
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
