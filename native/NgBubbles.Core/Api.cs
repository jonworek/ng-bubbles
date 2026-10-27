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
    private string primary;
    private readonly string local;
    private string computerId;
    private string previousRemote;
    private bool localAvailable;
    private bool remoteManaged;
    private long checkedAt;
    private long discoveredAt;
    private readonly SemaphoreSlim profileGate = new(1, 1);
    public string Server { get; private set; }
    public bool UsingLocal => local.Length > 0 && Server == local;
    public ConnectionProfile Profile => new(primary, local, computerId, localAvailable, remoteManaged) { PreviousRemote = previousRemote };
    public Api(string server, string password, Diagnostics diagnostics, HttpMessageHandler? handler = null, string localServer = "", string computerId = "")
    {
        local = string.IsNullOrWhiteSpace(localServer) ? "" : Model.NormalizeServer(localServer);
        primary = string.IsNullOrWhiteSpace(server) ? "" : Model.NormalizeServer(server);
        if (primary.Length == 0 && local.Length == 0) throw new ArgumentException("Enter a local or remote server URL.");
        this.computerId = computerId; previousRemote = primary;
        Server = local.Length > 0 ? local : primary;
        this.password = password;
        this.diagnostics = diagnostics;
        http = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5), PooledConnectionLifetime = TimeSpan.FromMinutes(1), PooledConnectionIdleTimeout = TimeSpan.FromSeconds(20) }) { Timeout = TimeSpan.FromSeconds(120) };
    }
    internal void UseSavedFallback(Preferences settings)
    {
        if (settings.LocalServer != local || computerId.Length == 0 || settings.ServerComputerId != computerId || settings.Server.Length == 0) return;
        var saved = Model.NormalizeServer(settings.Server);
        if (saved == primary) return;
        var remoteInUse = Server == primary;
        previousRemote = primary; primary = saved;
        if (remoteInUse) Server = saved;
    }
    public async Task<ConnectionProfile> RefreshConnectionAsync(bool force = false, IProgress<ConnectionProfile>? progress = null, CancellationToken token = default)
    {
        await profileGate.WaitAsync(token);
        try
        {
            if (!force && checkedAt != 0 && Environment.TickCount64 - checkedAt < 20000) return Profile;
            checkedAt = Environment.TickCount64;
            if (local.Length == 0) return Profile;
            var wasLocal = localAvailable;
            JsonElement info;
            try { info = await DirectAsync(local, "/server/info", TimeSpan.FromSeconds(3), token); }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException)
            {
                token.ThrowIfCancellationRequested(); localAvailable = false; remoteManaged = false;
                diagnostics.Log("local-connection-checked", new { available = false, type = e.GetType().Name });
                if (Server == local && primary.Length > 0) Server = primary;
                progress?.Report(Profile); return Profile;
            }
            CheckIdentity(info);
            localAvailable = true; Server = local;
            diagnostics.Log("local-connection-checked", new { available = true, verified = computerId.Length > 0 });
            progress?.Report(Profile);
            if (!force && wasLocal && discoveredAt != 0 && Environment.TickCount64 - discoveredAt < 60000) return Profile;
            discoveredAt = Environment.TickCount64; remoteManaged = false;
            try
            {
                var config = await DirectAsync(local, "/fcm/client", TimeSpan.FromSeconds(5), token);
                var project = config.GetProperty("project_info").GetProperty("project_id").GetString() ?? "";
                if (project.Length is < 4 or > 64 || project.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) return Profile;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(5));
                var url = "https://firestore.googleapis.com/v1/projects/" + Uri.EscapeDataString(project) + "/databases/(default)/documents/server/config";
                using var response = await http.GetAsync(url, deadline.Token); response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(deadline.Token));
                var candidate = Model.NormalizeServer(document.RootElement.GetProperty("fields").GetProperty("serverUrl").GetProperty("stringValue").GetString() ?? "");
                if (new Uri(candidate).Scheme != "https" || computerId.Length == 0) return Profile;
                var remote = await DirectAsync(candidate, "/server/info", TimeSpan.FromSeconds(5), token);
                if (Identity(remote) != computerId) throw new IOException("The discovered remote address belongs to a different Mac.");
                if (primary != candidate) previousRemote = primary;
                primary = candidate; remoteManaged = true;
                diagnostics.Log("remote-address-maintained", new { verified = true });
            }
            catch (Exception e) when (e is HttpRequestException or IOException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or OperationCanceledException)
            {
                token.ThrowIfCancellationRequested();
                diagnostics.Log("remote-discovery-unavailable", new { type = e.GetType().Name });
            }
            progress?.Report(Profile); return Profile;
        }
        finally { profileGate.Release(); }
    }
    public async Task<ConnectionProfile> ValidateConnectionAsync(CancellationToken token = default)
    {
        await RefreshConnectionAsync(true, token: token);
        if (!localAvailable) CheckIdentity(await RequestAsync("/server/info", token: token));
        return Profile;
    }
    private static string Identity(JsonElement info) => info.ValueKind == JsonValueKind.Object && info.TryGetProperty("computer_id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() ?? "" : "";
    private void CheckIdentity(JsonElement info)
    {
        var id = Identity(info);
        if (computerId.Length > 0 && id != computerId) throw new IOException("This address does not identify your saved Mac. Check the server URL before connecting.");
        if (id.Length > 0) computerId = id;
    }
    private async Task<JsonElement> DirectAsync(string origin, string path, TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(timeout);
        using var response = await http.GetAsync(new Uri(origin + Url(path).PathAndQuery), deadline.Token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) Check(response.StatusCode);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(deadline.Token));
        var root = document.RootElement;
        return (root.TryGetProperty("data", out var data) ? data : root).Clone();
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
        await ValidateConnectionAsync(token);
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
            List<T> page;
            try { page = json.Deserialize<List<T>>(Json.Options) ?? throw new JsonException("Unexpected server response."); }
            catch (JsonException e)
            {
                var field = typeof(Message).GetProperties().Concat(typeof(Chat).GetProperties()).FirstOrDefault(property => string.Equals(property.Name, e.Path?.Split('.').Last(), StringComparison.OrdinalIgnoreCase))?.Name ?? "unknown";
                diagnostics.Log("response-format-failed", new { route = Route(path), model = typeof(T).Name, field });
                throw;
            }
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
