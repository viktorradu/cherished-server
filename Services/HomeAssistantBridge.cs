using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;

public sealed class HomeAssistantBridge(
    IConfiguration configuration, IWebHostEnvironment environment, HomeAssistantState state,
    ILogger<HomeAssistantBridge> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        HomeAssistantSettings settings;
        try
        {
            var enabled = configuration["HomeAssistant:Enabled"];
            if (string.IsNullOrEmpty(enabled) || bool.TryParse(enabled, out var isEnabled) && !isEnabled)
            {
                state.Publish(false, false, "disabled");
                return;
            }
            settings = configuration.GetSection("HomeAssistant").Get<HomeAssistantSettings>() ?? new();
            if (settings.ValidationError(environment.ContentRootPath) is { } error)
            {
                logger.LogError("{ConfigurationError} Photos remain available.", error);
                state.Publish(true, false, "configuration-error");
                return;
            }
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            logger.LogError("Invalid Home Assistant configuration ({FailureType}); photos remain available.", error.GetType().Name);
            state.Publish(true, false, "configuration-error");
            return;
        }
        if (!settings.Enabled)
        {
            state.Publish(false, false, "disabled");
            return;
        }
        var failures = 0;
        state.Publish(true, false, "connecting");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var token = configuration["HomeAssistant:AccessToken"];
                    if (string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(settings.TokenFile))
                    {
                        await using var file = File.OpenRead(settings.TokenFile);
                        if (file.Length > 16384) throw new InvalidDataException("HA token file is too large.");
                        using var reader = new StreamReader(file);
                        var characters = new char[8193];
                        var length = await reader.ReadBlockAsync(characters.AsMemory(), stoppingToken);
                        token = new string(characters, 0, length).Trim();
                    }
                    if (string.IsNullOrWhiteSpace(token) || token.Length > 8192 || token.Any(char.IsWhiteSpace))
                    {
                        logger.LogError("Home Assistant needs HomeAssistant__AccessToken or a readable protected TokenFile containing a token.");
                        throw new InvalidDataException("A valid server-side HA token is required.");
                    }
                    using var socket = new ClientWebSocket();
                    var readings = new HomeAssistantReadings(settings.Entities);
                    using (var setup = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                    {
                        setup.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
                        await socket.ConnectAsync(settings.WebSocketUri(), setup.Token);
                        RequireType(await Receive(socket, setup.Token), "auth_required");
                        await Send(socket, new { type = "auth", access_token = token }, setup.Token);
                        RequireType(await Receive(socket, setup.Token), "auth_ok");
                        await Send(socket, new { id = 1, type = "subscribe_events", event_type = "state_changed" }, setup.Token);
                        var pending = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                        await Result(socket, 1, pending, settings, setup.Token);
                        await Send(socket, new { id = 2, type = "get_states" }, setup.Token);
                        var initial = await Result(socket, 2, pending, settings, setup.Token);
                        readings.LoadSnapshot(initial.GetProperty("result"));
                        foreach (var item in pending.Values) readings.ApplyEvent(item);
                        state.Publish(true, true, "connected", readings.Snapshot(), DateTimeOffset.UtcNow);
                        failures = 0;
                        logger.LogInformation("Home Assistant connected; four sensor states synchronized.");
                    }
                    await Monitor(socket, readings, settings, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    // Fault isolation must not leak HA payloads, endpoints or authentication material to logs.
                    logger.LogWarning("Home Assistant connection failed ({FailureType}); retaining readings and reconnecting. Check HA availability, token permissions and configuration.", error.GetType().Name);
                    state.Publish(true, false, "disconnected");
                }
                var delay = Math.Min(60000, 1000 * Math.Pow(2, Math.Min(failures++, 6)) * (1 + Random.Shared.NextDouble() * 0.2));
                await Task.Delay(TimeSpan.FromMilliseconds(delay), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { state.Publish(true, false, "disconnected"); }
    }

    private async Task Monitor(ClientWebSocket socket, HomeAssistantReadings readings,
        HomeAssistantSettings settings, CancellationToken stoppingToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var pongs = Channel.CreateBounded<int>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        var expectedPing = 0;
        async Task Updates()
        {
            while (true)
            {
                var message = await Receive(socket, session.Token);
                if (Type(message) == "pong" && message.GetProperty("id").GetInt32() == Volatile.Read(ref expectedPing))
                    pongs.Writer.TryWrite(expectedPing);
                else if (Type(message) == "event" && message.GetProperty("id").GetInt32() == 1 && readings.ApplyEvent(message))
                    state.Publish(true, true, "connected", readings.Snapshot(), DateTimeOffset.UtcNow);
            }
        }
        async Task Heartbeat()
        {
            var id = 2;
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.HeartbeatIntervalSeconds), session.Token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
                Volatile.Write(ref expectedPing, ++id);
                await Send(socket, new { id, type = "ping" }, deadline.Token);
                while (await pongs.Reader.ReadAsync(deadline.Token) != id) { }
                state.Publish(true, true, "connected", lastContact: DateTimeOffset.UtcNow);
            }
        }
        var updates = Updates();
        var heartbeat = Heartbeat();
        await Task.WhenAny(updates, heartbeat);
        session.Cancel();
        socket.Abort();
        await Task.WhenAll(updates, heartbeat);
    }

    private static async Task<JsonElement> Result(ClientWebSocket socket, int id,
        Dictionary<string, JsonElement> pending, HomeAssistantSettings settings, CancellationToken cancellation)
    {
        while (true)
        {
            var message = await Receive(socket, cancellation);
            if (Type(message) == "result" && message.GetProperty("id").GetInt32() == id)
            {
                if (!message.GetProperty("success").GetBoolean()) throw new InvalidDataException("HA command rejected.");
                return message;
            }
            if (Type(message) == "event" && message.GetProperty("id").GetInt32() == 1 &&
                HomeAssistantReadings.EventEntity(message) is { } entity && settings.Entities.Values.Contains(entity))
                pending[entity] = message;
        }
    }

    private static string? Type(JsonElement message) => message.GetProperty("type").GetString();
    private static void RequireType(JsonElement message, string expected)
    {
        if (Type(message) == "auth_invalid") throw new UnauthorizedAccessException("HA authentication rejected.");
        if (Type(message) != expected) throw new InvalidDataException("Unexpected HA authentication response.");
    }

    private static Task Send(ClientWebSocket socket, object message, CancellationToken cancellation) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message).AsMemory(), WebSocketMessageType.Text, true, cancellation).AsTask();

    private static async Task<JsonElement> Receive(ClientWebSocket socket, CancellationToken cancellation)
    {
        using var message = new MemoryStream();
        var buffer = new byte[8192];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), cancellation);
            if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("HA WebSocket closed or sent a non-text message.");
            if (message.Length + result.Count > 8 * 1024 * 1024) throw new InvalidDataException("HA message exceeded its limit.");
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        using var json = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
        return json.RootElement.Clone();
    }
}
