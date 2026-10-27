using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace NgBubbles.Core;

// Engine.IO v4 text transport: open, namespace connect, heartbeat, and Socket.IO events.
public sealed class LiveConnection(string server, string password, Diagnostics diagnostics, Func<string>? resolveServer = null)
{
    private ClientWebSocket? activeSocket;
    public bool Connected { get; private set; }
    public void Reconnect() { try { Volatile.Read(ref activeSocket)?.Abort(); } catch (ObjectDisposedException) { } }
    public async Task RunAsync(Func<string, JsonElement, Task> onEvent, Action changed, CancellationToken token)
    {
        var delay = 1000;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                Volatile.Write(ref activeSocket, socket);
                socket.Options.CollectHttpResponseDetails = false;
                var origin = new Uri(resolveServer?.Invoke() ?? server);
                var url = new UriBuilder(origin) { Scheme = origin.Scheme == "https" ? "wss" : "ws", Path = "/socket.io/", Query = "EIO=4&transport=websocket&guid=" + Uri.EscapeDataString(password) };
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(token);
                connect.CancelAfter(TimeSpan.FromSeconds(15));
                await socket.ConnectAsync(url.Uri, connect.Token);
                var buffer = new byte[65536];
                while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    using var frame = new MemoryStream();
                    WebSocketReceiveResult result;
                    using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(token);
                    heartbeat.CancelAfter(TimeSpan.FromSeconds(75));
                    do
                    {
                        result = await socket.ReceiveAsync(buffer, heartbeat.Token);
                        if (result.MessageType == WebSocketMessageType.Close) throw new IOException("Socket closed.");
                        if (frame.Length + result.Count > 16 * 1024 * 1024) throw new IOException("Socket frame exceeds limit.");
                        frame.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    if (result.MessageType != WebSocketMessageType.Text) continue;
                    var packet = Encoding.UTF8.GetString(frame.ToArray());
                    if (packet.StartsWith('0')) await SendAsync(socket, "40", token);
                    else if (packet.StartsWith('2')) await SendAsync(socket, "3" + packet[1..], token);
                    else if (packet.StartsWith("40", StringComparison.Ordinal)) { Connected = true; delay = 1000; changed(); }
                    else if (packet.StartsWith("42", StringComparison.Ordinal))
                    {
                        var start = packet.IndexOf('[');
                        if (start < 0) continue;
                        using var json = JsonDocument.Parse(packet[start..]);
                        var array = json.RootElement;
                        if (array.GetArrayLength() > 1 && array[0].GetString() is { } name) await onEvent(name, array[1].Clone());
                    }
                    else if (packet.StartsWith("41", StringComparison.Ordinal) || packet.StartsWith("44", StringComparison.Ordinal)) throw new IOException("Socket disconnected.");
                }
            }
            catch (Exception e) when (e is WebSocketException or IOException or OperationCanceledException or JsonException)
            {
                diagnostics.Log("live-reconnect", new { type = e.GetType().Name });
            }
            finally { Volatile.Write(ref activeSocket, null); Connected = false; changed(); }
            try { await Task.Delay(delay, token); } catch (OperationCanceledException) { return; }
            delay = Math.Min(15000, delay * 2);
        }
    }
    private static Task SendAsync(ClientWebSocket socket, string text, CancellationToken token) => socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, token);
}
