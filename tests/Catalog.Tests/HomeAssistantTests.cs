using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

internal static class HomeAssistantTests
{
    private const string MockToken = "mock-token-not-a-real-credential";
    private static readonly string[] Ids = ["sensor.inside_temperature", "sensor.inside_humidity",
        "sensor.outside_temperature", "sensor.outside_humidity"];
    private static readonly string[] Keys = ["insideTemperature", "insideHumidity", "outsideTemperature", "outsideHumidity"];

    public static async Task Run()
    {
        Configuration();
        Cache();
        await DisabledAndInvalid();
        await Connection();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static object Sample(string id, string value, int second = 10, string? unit = "C") => new
    {
        entity_id = id, state = value, attributes = new { unit_of_measurement = unit, private_attribute = MockToken },
        last_updated = $"2026-11-18T12:00:{second:00}Z",
        last_reported = $"2026-11-18T12:00:{second:00}Z"
    };
    private static object Change(string id, object? sample, int second = 30) => new
    {
        id = 1, type = "event", @event = new
        {
            event_type = "state_changed", time_fired = $"2026-11-18T12:00:{second:00}Z",
            data = new { entity_id = id, new_state = sample }
        }
    };

    private static Dictionary<string, string?> Settings(string url = "http://127.0.0.1:8123") => new()
    {
        ["HomeAssistant:Enabled"] = "true", ["HomeAssistant:Url"] = url,
        ["HomeAssistant:AccessToken"] = MockToken,
        ["HomeAssistant:InsideTemperature"] = Ids[0], ["HomeAssistant:InsideHumidity"] = Ids[1],
        ["HomeAssistant:OutsideTemperature"] = Ids[2], ["HomeAssistant:OutsideHumidity"] = Ids[3],
        ["HomeAssistant:RequestTimeoutSeconds"] = "1", ["HomeAssistant:HeartbeatIntervalSeconds"] = "1"
    };

    private static void Configuration()
    {
        var root = Path.Combine(Path.GetTempPath(), "ha-app");
        var settings = new ConfigurationBuilder().AddInMemoryCollection(Settings()).Build()
            .GetSection("HomeAssistant").Get<HomeAssistantSettings>()!;
        Check(settings.ValidationError(root) is null, "valid optional configuration");
        settings.Url = "https://example.invalid/ha/";
        Check(settings.WebSocketUri().AbsoluteUri == "wss://example.invalid/ha/api/websocket", "proxy path and TLS scheme preserved");
        foreach (var url in new[] { "https://user:password@example.invalid", "https://example.invalid/?token=x", "ws://example.invalid" })
        {
            settings.Url = url;
            Check(settings.ValidationError(root) is not null, "unsafe URL rejected");
        }
        settings.Url = "http://example.invalid";
        settings.TokenFile = Path.Combine(root, "wwwroot", "secret.txt");
        Check(settings.ValidationError(root) is not null, "public/content-directory secret file rejected");
        settings.TokenFile = "";
        settings.RequestTimeoutSeconds = 0;
        Check(settings.ValidationError(root) is not null, "zero scheduling/deadline values rejected");
        settings.RequestTimeoutSeconds = 15;
        settings.InsideTemperature = settings.OutsideTemperature;
        Check(settings.ValidationError(root) is not null, "duplicate mappings rejected");
        settings.InsideTemperature = "light.room";
        Check(settings.ValidationError(root) is not null, "only sensor entities accepted");
        settings.Enabled = false;
        Check(settings.ValidationError(root) is null, "disabled setup needs no entities, URL or token");
    }

    private static void Cache()
    {
        var readings = new HomeAssistantReadings(Keys.Zip(Ids).ToDictionary(pair => pair.First, pair => pair.Second));
        readings.LoadSnapshot(Json(new[] { Sample(Ids[0], "21.5"), Sample(Ids[1], "0", unit: "%") }));
        Check(readings.Snapshot()[Keys[0]].Value == 21.5 && readings.Snapshot()[Keys[1]].Value == 0, "numbers including zero preserved");
        Check(!readings.Snapshot()[Keys[2]].Available, "missing entity unavailable");
        readings.ApplyEvent(Json(Change(Ids[0], Sample(Ids[0], "19", 5))));
        Check(readings.Snapshot()[Keys[0]].Value == 21.5, "older buffered event cannot override newer snapshot");
        readings.ApplyEvent(Json(Change(Ids[0], Sample(Ids[0], "72.4", 20, "F"))));
        var current = readings.Snapshot()[Keys[0]];
        Check(current.Value == 72.4 && current.Unit == "F" && current.LastReported?.Second == 20 && current.LastUpdated?.Second == 20,
            "live state, unit and HA timestamps replaced together");
        foreach (var invalid in new[] { "unknown", "unavailable", "NaN", "Infinity", "1e9999", "not a number", "" })
        {
            readings.ApplyEvent(Json(Change(Ids[0], Sample(Ids[0], invalid, 25))));
            Check(!readings.Snapshot()[Keys[0]].Available && readings.Snapshot()[Keys[0]].Value is null, "invalid state must not become zero");
        }
        readings.ApplyEvent(Json(Change(Ids[1], null)));
        Check(!readings.Snapshot()[Keys[1]].Available, "removed entity unavailable");
        readings.LoadSnapshot(Json(Array.Empty<object>()));
        Check(readings.Snapshot().Values.All(value => !value.Available), "full reconnect refresh clears missing old entities");

        var cache = new HomeAssistantState();
        var subscription = cache.Subscribe()!;
        cache.Publish(true, true, "connected", readings.Snapshot());
        cache.Publish(true, false, "disconnected");
        Check(subscription.Reader.TryRead(out var latest) && latest.Revision == cache.Snapshot.Revision &&
            latest.Status == "disconnected" && !subscription.Reader.TryRead(out _), "slow clients keep only latest complete snapshot");
        cache.Unsubscribe(subscription);
        Parallel.For(0, 100, _ => cache.Publish(true, true, "connected"));
        var late = cache.Subscribe()!;
        Check(late.Reader.TryRead(out latest) && latest.Revision == cache.Snapshot.Revision, "reconnecting browser gets atomic initial snapshot");
        cache.Unsubscribe(late);
        var clients = Enumerable.Range(0, 32).Select(_ => cache.Subscribe()!).ToArray();
        Check(cache.Subscribe() is null, "SSE client count bounded");
        foreach (var client in clients) cache.Unsubscribe(client);
        Check(!JsonSerializer.Serialize(cache.Snapshot).Contains(MockToken), "no raw HA attributes or token in public state");
    }

    private static async Task Eventually(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!predicate()) await Task.Delay(20, timeout.Token);
    }

    private static async Task DisabledAndInvalid()
    {
        foreach (var entries in new[]
        {
            new Dictionary<string, string?>(),
            new Dictionary<string, string?> { ["HomeAssistant:Enabled"] = "true" },
            new Dictionary<string, string?> { ["HomeAssistant:Enabled"] = "not-a-boolean" }
        })
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(entries).Build();
            var state = new HomeAssistantState();
            var log = new TestLog<HomeAssistantBridge>();
            using var bridge = new HomeAssistantBridge(configuration, new TestEnvironment(), state, log);
            await bridge.StartAsync(CancellationToken.None);
            await Eventually(() => state.Snapshot.Status != "starting");
            Check(state.Snapshot.Status == (entries.Count == 0 ? "disabled" : "configuration-error"), "disabled/invalid config explicit and isolated");
            Check(bridge.ExecuteTask is { IsFaulted: false }, "bad optional configuration cannot crash photos host");
            if (entries.Count > 0) Check(log.Messages.Count > 0, "invalid setup logged");
            await bridge.StopAsync(CancellationToken.None);
        }
        var missing = Settings();
        missing.Remove("HomeAssistant:AccessToken");
        var missingState = new HomeAssistantState();
        var missingLog = new TestLog<HomeAssistantBridge>();
        using var missingBridge = new HomeAssistantBridge(new ConfigurationBuilder().AddInMemoryCollection(missing).Build(),
            new TestEnvironment(), missingState, missingLog);
        await missingBridge.StartAsync(CancellationToken.None);
        await Eventually(() => missingState.Snapshot.Status == "disconnected");
        Check(missingLog.Messages.Any(message => message.Contains("token", StringComparison.OrdinalIgnoreCase)), "missing secret has actionable redacted log");
        await missingBridge.StopAsync(CancellationToken.None);
    }

    private static async Task Connection()
    {
        var state = new HomeAssistantState();
        var log = new TestLog<HomeAssistantBridge>();
        var sessions = Channel.CreateUnbounded<MockSession>();
        var errors = new ConcurrentQueue<Exception>();
        var connections = 0;
        var respond = true;
        var pings = 0;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(state);
        builder.Services.AddControllers().AddApplicationPart(typeof(Sensors).Assembly);
        await using var app = builder.Build();
        app.UseWebSockets();
        app.MapControllers();
        app.Map("/api/websocket", async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            var session = new MockSession(socket);
            var number = Interlocked.Increment(ref connections);
            try
            {
                await session.Send(new { type = "auth_required" });
                var auth = await Receive(socket, context.RequestAborted);
                Check(auth.GetProperty("access_token").GetString() == MockToken, "server-side auth token");
                if (number == 1)
                {
                    await session.Send(new { type = "auth_invalid", message = MockToken });
                    return;
                }
                if (number == 2)
                {
                    await Receive(socket, context.RequestAborted);
                    throw new Exception("Client sent a command before authentication completed.");
                }
                await session.Send(new { type = "auth_ok" });
                var subscribe = await Receive(socket, context.RequestAborted);
                Check(subscribe.GetProperty("type").GetString() == "subscribe_events" &&
                    subscribe.GetProperty("event_type").GetString() == "state_changed", "subscribe before snapshot");
                await session.Send(new { id = 1, type = "result", success = true, result = (object?)null });
                var snapshot = await Receive(socket, context.RequestAborted);
                Check(snapshot.GetProperty("type").GetString() == "get_states", "initial full snapshot requested");
                if (number == 3)
                    await session.Send(Change(Ids[0], Sample(Ids[0], "35", 20)));
                await session.Send(new
                {
                    id = 2, type = "result", success = true,
                    result = number == 3 ? new[] { Sample(Ids[0], "21.5") } : new[] { Sample(Ids[2], "44", 40) }
                });
                sessions.Writer.TryWrite(session);
                while (true)
                {
                    var message = await Receive(socket, context.RequestAborted);
                    if (message.GetProperty("type").GetString() == "ping")
                    {
                        Interlocked.Increment(ref pings);
                        if (Volatile.Read(ref respond))
                            await session.Send(new { id = message.GetProperty("id").GetInt32(), type = "pong" });
                    }
                }
            }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException or IOException) { }
            catch (Exception error) { errors.Enqueue(error); }
        });
        await app.StartAsync();
        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var settings = Settings(url);
        var tokenFile = Path.Combine(Path.GetTempPath(), "cherished-ha-mock-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(tokenFile, MockToken + Environment.NewLine);
        settings.Remove("HomeAssistant:AccessToken");
        settings["HomeAssistant:TokenFile"] = tokenFile;
        using var bridge = new HomeAssistantBridge(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            new TestEnvironment(), state, log);
        try
        {
            await bridge.StartAsync(CancellationToken.None);
            await Eventually(() => state.Snapshot.Connected && state.Snapshot.Readings[Keys[0]].Value == 35);
            var session = await sessions.Reader.ReadAsync();
            Check(log.Messages.Any(message => message.Contains("UnauthorizedAccessException")), "authentication failure recovered and logged safely");
            Check(connections >= 3, "hanging authentication has a deadline and recovers");
            Check(log.Messages.All(message => !message.Contains(MockToken)), "HA-controlled auth errors do not leak secrets into logs");
            using var client = new HttpClient { BaseAddress = new Uri(url) };
            var publicJson = await client.GetStringAsync("/sensors/state");
            Check(!publicJson.Contains(MockToken) && !publicJson.Contains("sensor.inside_") && !publicJson.Contains("AccessToken"), "public API exposes only safe readings");
            using var requestTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var stream = await client.GetStreamAsync("/sensors/events", requestTimeout.Token);
            using var reader = new StreamReader(stream);
            string? line;
            do { line = await reader.ReadLineAsync(requestTimeout.Token); } while (line is not null && !line.StartsWith("data: "));
            Check(line?.Contains("\"value\":35") == true && !line.Contains(MockToken), "SSE starts with cached full state");
            await session.Send(Change(Ids[0], Sample(Ids[0], "72", 30, "F")));
            await Eventually(() => state.Snapshot.Readings[Keys[0]].Unit == "F");
            await Eventually(() => Volatile.Read(ref pings) >= 2);
            Check(state.Snapshot.Connected && state.Snapshot.Readings[Keys[0]].LastReported?.Second == 30,
                "quiet sensors remain connected via pong, timestamps remain HA-reported");
            await session.Send(Change(Ids[1], Sample(Ids[1], "unavailable", 30, "%")));
            await Eventually(() => state.Snapshot.Readings[Keys[1]].Unit == "%");
            Check(!state.Snapshot.Readings[Keys[1]].Available, "unavailable updates streamed");
            Volatile.Write(ref respond, false);
            await Eventually(() => !state.Snapshot.Connected);
            Check(state.Snapshot.Readings[Keys[0]].Value == 72, "heartbeat deadline disconnect retains last good readings");
            Volatile.Write(ref respond, true);
            await Eventually(() => state.Snapshot.Connected && state.Snapshot.Readings[Keys[2]].Value == 44);
            Check(!state.Snapshot.Readings[Keys[0]].Available && connections >= 4, "reconnect resubscribes and fully refreshes missing entities");
            Check(bridge.ExecuteTask is { IsFaulted: false } && errors.IsEmpty, "faults isolated and mock protocol assertions passed");
        }
        finally
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await bridge.StopAsync(deadline.Token);
            await app.StopAsync(deadline.Token);
            File.Delete(tokenFile);
        }
        Check(!state.Snapshot.Connected, "service cancellation releases HA connection");
    }

    private static async Task<JsonElement> Receive(WebSocket socket, CancellationToken cancellation)
    {
        using var message = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellation);
            if (result.MessageType == WebSocketMessageType.Close) throw new IOException("closed");
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        using var json = JsonDocument.Parse(message.ToArray());
        return json.RootElement.Clone();
    }

    private sealed class MockSession(WebSocket socket)
    {
        private readonly SemaphoreSlim _send = new(1);
        public async Task Send(object message)
        {
            await _send.WaitAsync();
            try { await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, CancellationToken.None); }
            finally { _send.Release(); }
        }
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "tests";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public string WebRootPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TestLog<T> : ILogger<T>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState value, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Enqueue(formatter(value, exception));
    }
}
