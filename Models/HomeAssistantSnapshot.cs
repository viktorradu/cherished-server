using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;

public sealed record SensorReading(double? Value, string? Unit, bool Available,
    DateTimeOffset? LastReported, DateTimeOffset? LastUpdated);

public sealed record HomeAssistantSnapshot(bool Enabled, bool Connected, string Status,
    long Revision, DateTimeOffset? LastContact, IReadOnlyDictionary<string, SensorReading> Readings);

public sealed class HomeAssistantState
{
    private readonly object _gate = new();
    private readonly HashSet<Channel<HomeAssistantSnapshot>> _listeners = [];
    private HomeAssistantSnapshot _snapshot = new(false, false, "starting", 0, null,
        new Dictionary<string, SensorReading>().ToFrozenDictionary());

    public HomeAssistantSnapshot Snapshot { get { lock (_gate) return _snapshot; } }

    public void Publish(bool enabled, bool connected, string status,
        IReadOnlyDictionary<string, SensorReading>? readings = null, DateTimeOffset? lastContact = null)
    {
        lock (_gate)
        {
            _snapshot = new(enabled, connected, status, _snapshot.Revision + 1,
                lastContact ?? _snapshot.LastContact, readings?.ToFrozenDictionary() ?? _snapshot.Readings);
            foreach (var listener in _listeners) listener.Writer.TryWrite(_snapshot);
        }
    }

    public Channel<HomeAssistantSnapshot>? Subscribe()
    {
        lock (_gate)
        {
            if (_listeners.Count >= 32) return null;
            var channel = Channel.CreateBounded<HomeAssistantSnapshot>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true
            });
            _listeners.Add(channel);
            // Register and enqueue the initial state under the publication lock: no snapshot/subscription race.
            channel.Writer.TryWrite(_snapshot);
            return channel;
        }
    }

    public void Unsubscribe(Channel<HomeAssistantSnapshot> channel)
    {
        lock (_gate) _listeners.Remove(channel);
        channel.Writer.TryComplete();
    }
}

public sealed class HomeAssistantReadings
{
    private sealed record Entry(SensorReading Reading, DateTimeOffset? Order);
    private readonly IReadOnlyDictionary<string, string> _entities;
    private readonly Dictionary<string, Entry> _values = new(StringComparer.Ordinal);
    private static readonly SensorReading Unavailable = new(null, null, false, null, null);

    public HomeAssistantReadings(IReadOnlyDictionary<string, string> entities) => _entities = entities;

    public IReadOnlyDictionary<string, SensorReading> Snapshot() =>
        _entities.ToDictionary(pair => pair.Key,
            pair => _values.TryGetValue(pair.Value, out var entry) ? entry.Reading : Unavailable);

    public void LoadSnapshot(JsonElement states)
    {
        if (states.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid HA state snapshot.");
        _values.Clear();
        foreach (var state in states.EnumerateArray())
        {
            var id = Text(state, "entity_id");
            if (id is not null && _entities.Values.Contains(id)) _values[id] = Parse(state);
        }
    }

    public bool ApplyEvent(JsonElement message)
    {
        if (!message.TryGetProperty("event", out var item) || Text(item, "event_type") != "state_changed" ||
            !item.TryGetProperty("data", out var data)) return false;
        var id = Text(data, "entity_id");
        if (id is null || !_entities.Values.Contains(id)) return false;
        if (!data.TryGetProperty("new_state", out var state)) throw new InvalidDataException("Invalid HA state event.");
        var next = state.ValueKind == JsonValueKind.Null
            ? new Entry(Unavailable, Timestamp(item, "time_fired") ??
                throw new InvalidDataException("HA removal timestamp is missing.")) : Parse(state);
        // Buffered events may predate get_states. Never replay an older value over the newer snapshot.
        if (_values.TryGetValue(id, out var current) && next.Order is not null &&
            current.Order is not null && next.Order < current.Order) return false;
        _values[id] = next;
        return true;
    }

    public static string? EventEntity(JsonElement message)
    {
        if (!message.TryGetProperty("event", out var item) ||
            !item.TryGetProperty("data", out var data)) return null;
        return Text(data, "entity_id");
    }

    private static Entry Parse(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid HA sensor state.");
        var raw = Text(state, "state");
        var valid = double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value);
        var unit = state.TryGetProperty("attributes", out var attributes) ? Text(attributes, "unit_of_measurement") : null;
        if (unit?.Length > 64) throw new InvalidDataException("HA sensor unit exceeded its limit.");
        var updated = Timestamp(state, "last_updated") ??
            throw new InvalidDataException("HA state timestamp is missing.");
        return new(new(valid ? value : null, unit, valid, Timestamp(state, "last_reported"), updated), updated);
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? Timestamp(JsonElement element, string property) =>
        DateTimeOffset.TryParse(Text(element, property), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var value) ? value : null;
}
