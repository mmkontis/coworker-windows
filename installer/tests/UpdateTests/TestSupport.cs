using System.Globalization;
using System.Net;
using System.Text;
using Coworker.UpdateSigner;

namespace Coworker.UpdateTests;

/// <summary>A tiny loopback HTTP server standing in for humanlike.co: an appcast plus downloads.</summary>
internal sealed class MockFeedServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, (byte[] Body, int Status)> _routes = new();
    private readonly Dictionary<string, int> _hits = new();
    private readonly Task _loop;

    public MockFeedServer()
    {
        // A free loopback port; HttpListener needs no admin rights for 127.0.0.1 prefixes.
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _loop = Task.Run(Serve);
    }

    public string BaseUrl { get; }

    public string FeedUrl => BaseUrl + "/appcast-windows.xml";

    public void Serve(string path, byte[] body, int status = 200)
    {
        lock (_routes) _routes[path] = (body, status);
    }

    public void Serve(string path, string text, int status = 200) => Serve(path, Encoding.UTF8.GetBytes(text), status);

    public int Hits(string path)
    {
        lock (_hits) return _hits.TryGetValue(path, out var n) ? n : 0;
    }

    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }

            var path = context.Request.Url!.AbsolutePath;
            lock (_hits) _hits[path] = _hits.GetValueOrDefault(path) + 1;

            (byte[] Body, int Status) route;
            bool found;
            lock (_routes) found = _routes.TryGetValue(path, out route);
            if (!found) route = (Encoding.UTF8.GetBytes("not found"), 404);

            try
            {
                context.Response.StatusCode = route.Status;
                context.Response.ContentLength64 = route.Body.Length;
                if (context.Request.HttpMethod != "HEAD")
                    await context.Response.OutputStream.WriteAsync(route.Body);
                context.Response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or InvalidOperationException or IOException)
            {
                // the client went away
            }
        }
    }

    public void Dispose()
    {
        _listener.Close();
        try { _loop.Wait(2000); } catch (AggregateException) { }
    }
}

internal sealed record FeedItem(
    string ShortVersion,
    string Build,
    string Url,
    string? Signature,
    long Length,
    string? Os = "windows-x64",
    bool CriticalElement = false,
    bool CriticalTag = false,
    string NotesHtml = "<h2>Notes</h2><ul><li>Fixed things</li></ul>");

internal static class Feed
{
    public static string Build(params FeedItem[] items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""<?xml version="1.0" encoding="utf-8"?>""");
        sb.AppendLine("""<rss version="2.0" xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle" xmlns:dc="http://purl.org/dc/elements/1.1/">""");
        sb.AppendLine("<channel><title>Coworker Windows Updates</title><link>https://humanlike.co/coworker/appcast-windows.xml</link><language>en</language>");
        foreach (var item in items)
        {
            sb.AppendLine("<item>");
            sb.AppendLine($"<title>Coworker {item.ShortVersion}</title>");
            sb.AppendLine($"<description><![CDATA[{item.NotesHtml}]]></description>");
            sb.AppendLine("<pubDate>Wed, 30 Sep 2026 12:00:00 +0000</pubDate>");
            if (item.CriticalElement) sb.AppendLine("<sparkle:criticalUpdate />");
            if (item.CriticalTag) sb.AppendLine("<sparkle:tag>critical</sparkle:tag>");
            sb.Append("<enclosure url=\"" + item.Url + "\"");
            if (item.Os != null) sb.Append(" sparkle:os=\"" + item.Os + "\"");
            sb.Append(" sparkle:version=\"" + item.Build + "\" sparkle:shortVersionString=\"" + item.ShortVersion + "\"");
            if (item.Signature != null) sb.Append(" sparkle:edSignature=\"" + item.Signature + "\"");
            sb.AppendLine(" length=\"" + item.Length.ToString(CultureInfo.InvariantCulture) + "\" type=\"application/octet-stream\" />");
            sb.AppendLine("<sparkle:minimumSystemVersion>10.0.17763</sparkle:minimumSystemVersion>");
            sb.AppendLine("</item>");
        }
        sb.AppendLine("</channel></rss>");
        return sb.ToString();
    }
}

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "coworker-update-tests-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>One key pair per test class; installers are "signed" with the same code the release script uses.</summary>
internal sealed class KeyPair
{
    public KeyPair()
    {
        (PrivateKey, PublicKey) = Ed25519Tool.GenerateKeyPair();
    }

    public string PrivateKey { get; }

    public string PublicKey { get; }

    public string Sign(string path) => Ed25519Tool.SignFile(path, PrivateKey);
}
