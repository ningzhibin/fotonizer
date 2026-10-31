using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Fotonizer.Core.Upload;

namespace Fotonizer.Tests;

public class GoogleCallbackServerTests
{
    [Fact]
    public async Task AcceptsBrowserCallbackAndReturnsDecodedQuery()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var method = typeof(GooglePhotosAuth).GetMethod("WaitForCallbackAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var task = (Task<Dictionary<string, string>>)method!.Invoke(null,
            new object[] { listener, cts.Token })!;

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        var request = "GET /?code=abc%2F123&state=st%201&scope=photoslibrary HTTP/1.1\r\n" +
                      "Host: 127.0.0.1\r\nConnection: close\r\n\r\n";
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request), cts.Token);

        var query = await task;

        Assert.Equal("abc/123", query["code"]);
        Assert.Equal("st 1", query["state"]);

        using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
        var response = await reader.ReadToEndAsync();
        Assert.Contains("200 OK", response);
        Assert.Contains("授权成功", response);

        listener.Stop();
    }

    [Fact]
    public async Task IgnoresFaviconAndWaitsForRealCallback()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var method = typeof(GooglePhotosAuth).GetMethod("WaitForCallbackAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var task = (Task<Dictionary<string, string>>)method!.Invoke(null,
            new object[] { listener, cts.Token })!;

        using (var favicon = new TcpClient())
        {
            await favicon.ConnectAsync(IPAddress.Loopback, port, cts.Token);
            var request = "GET /favicon.ico HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n";
            await favicon.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request), cts.Token);
            using var faviconReader = new StreamReader(favicon.GetStream(), Encoding.UTF8);
            var faviconResponse = await faviconReader.ReadToEndAsync();
            Assert.Contains("404 Not Found", faviconResponse);
        }

        using var callback = new TcpClient();
        await callback.ConnectAsync(IPAddress.Loopback, port, cts.Token);
        var callbackRequest = "GET /?code=xyz HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n";
        await callback.GetStream().WriteAsync(Encoding.ASCII.GetBytes(callbackRequest), cts.Token);

        var query = await task;
        Assert.Equal("xyz", query["code"]);
        listener.Stop();
    }
}
