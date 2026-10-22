using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NgBubbles.Core;

public sealed class Api : IDisposable
{
    private readonly HttpClient http;
    private readonly string password;
    private readonly Diagnostics diagnostics;
    private readonly string primary;
    private readonly string local;
    public string Server { get; private set; }
    public Api(string server, string password, Diagnostics diagnostics, HttpMessageHandler? handler = null, string localServer = "")
    {
        primary = Model.NormalizeServer(server);
        local = string.IsNullOrWhiteSpace(localServer) ? "" : Model.NormalizeServer(localServer);
        Server = local.Length > 0 ? local : primary;
        this.password = password;
        this.diagnostics = diagnostics;
        http = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5), PooledConnectionLifetime = TimeSpan.FromMinutes(1), PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20) }) { Timeout = TimeSpan.FromSeconds(120) };
    }
    public Uri Url(string path, Dictionary<string, string?>? query = null)
    {
        var values = new Dictionary<string, string?>(query ?? []) { ["password"] = password };
        return new(Server + "/api/v1" + path + "?" + string.Join('&', values.Where(p => p.Value != null).Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value!))));
    }
    public async Task<JsonElement> RequestAsync(string path, object? body = null, Dictionary<string, string?>? query = null, CancellationToken token = default, bool post = false)
    {
        // Queries are read-only POSTs. Never replay a send, read mutation or restart after an uncertain response.
        var retry = path is "/chat/query" or "/message/query" || body == null && !post && !path.StartsWith("/server/restart/");
        var candidates = new[] { Server, local, primary }.Where(s => s.Length > 0).Distinct().ToArray();
        var attempts = retry ? candidates.Length : 1;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var server = candidates[attempt];
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(path == "/contact" ? TimeSpan.FromSeconds(120) : TimeSpan.FromSeconds(12));
            var url = new Uri(server + Url(path, query).PathAndQuery);
            using var request = new HttpRequestMessage(body != null || post ? HttpMethod.Post : HttpMethod.Get, url);
            if (body != null) request.Content = JsonContent.Create(body, options: new JsonSerializerOptions(Json.Options) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            var start = Stopwatch.GetTimestamp();
            try
            {
                using var response = await http.SendAsync(request, deadline.Token);
                diagnostics.Log("http", new { route = Route(path), status = (int)response.StatusCode, ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds, connection = server == local ? "local" : "remote" });
                if ((int)response.StatusCode >= 500 && attempt + 1 < attempts) continue;
                Check(response.StatusCode);
                using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(deadline.Token));
                var root = document.RootElement;
                if (root.TryGetProperty("status", out var status) && status.TryGetInt32(out var code) && code >= 400) throw new IOException($"Server request failed ({code}).");
                if (Server != server) diagnostics.Log("connection-switched", new { connection = server == local ? "local" : "remote" });
                Server = server;
                return (root.TryGetProperty("data", out var data) ? data : root).Clone();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
            {
                diagnostics.Log("http-failed", new { route = Route(path), type = e.GetType().Name, connection = server == local ? "local" : "remote",
                    reason = e is HttpRequestException failure ? failure.HttpRequestError.ToString() : e is JsonException ? "InvalidResponse" : "Timeout" });
                if (attempt + 1 < attempts) continue;
                throw new IOException("Cannot reach your Mac. Retry the connection or use the local address on your home network or VPN.");
            }
        }
        throw new IOException("Cannot reach your Mac. Check the local address and tunnel in Settings.");
    }
    public async Task RestartAsync(CancellationToken token = default)
    {
        // Probe both configured addresses before issuing the single, non-retried restart request.
        await RequestAsync("/ping", token: token);
        await RequestAsync("/server/restart/hard", token: token);
        diagnostics.Log("server-restart-requested");
    }
    public async Task<byte[]?> BytesAsync(string path, Dictionary<string, string?>? query = null, bool allowMissing = false, CancellationToken token = default)
    {
        using var response = await http.GetAsync(Url(path, query), HttpCompletionOption.ResponseHeadersRead, token);
        if (allowMissing && response.StatusCode == HttpStatusCode.NotFound) return null;
        Check(response.StatusCode);
        if (response.Content.Headers.ContentLength > 16 * 1024 * 1024) throw new IOException("Preview exceeds 16 MB. Use Save attachment.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) > 0)
        {
            if (output.Length + count > 16 * 1024 * 1024) throw new IOException("Preview exceeds 16 MB. Use Save attachment.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        return output.ToArray();
    }
    public async Task DownloadAsync(string guid, string target, CancellationToken token)
    {
        using var response = await http.GetAsync(Url($"/attachment/{Uri.EscapeDataString(guid)}/download"), HttpCompletionOption.ResponseHeadersRead, token);
        Check(response.StatusCode);
        var temporary = target + ".download";
        try
        {
            await using (var output = File.Create(temporary)) await response.Content.CopyToAsync(output, token);
            File.Move(temporary, target, true);
        }
        catch { if (File.Exists(temporary)) File.Delete(temporary); throw; }
    }
    public async Task<List<T>> PagesAsync<T>(string path, object? where = null, long? after = null, CancellationToken token = default)
    {
        var all = new List<T>();
        for (var offset = 0; offset < 100000; offset += 100)
        {
            var json = await RequestAsync(path, path == "/chat/query" ? new { offset, limit = 100, sort = "lastmessage", with = new[] { "participants", "lastmessage" } } : (object)new { offset, limit = 100, sort = "ASC", with = new[] { "chats", "handle", "attachments" }, where, after }, token: token);
            var page = json.Deserialize<List<T>>(Json.Options) ?? throw new IOException("Unexpected server response.");
            all.AddRange(page);
            if (page.Count < 100) return all;
        }
        throw new IOException("Server returned too many records in one sync.");
    }
    private static void Check(HttpStatusCode code)
    {
        if (code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new IOException("The server rejected the password.");
        if ((int)code < 200 || (int)code >= 300) throw new IOException($"Server request failed ({(int)code}).");
    }
    private static string Route(string path) => path.StartsWith("/chat/") && path != "/chat/query" && path != "/chat/new" ? "/chat/:id/" + path.Split('/').Last() : path.StartsWith("/attachment/") ? "/attachment/:id/download" : path.StartsWith("/message/") && path != "/message/query" && path != "/message/text" ? "/message/:id" : path;
    public void Dispose() => http.Dispose();
}
