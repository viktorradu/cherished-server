public sealed class HomeAssistantSettings
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    public string TokenFile { get; set; } = "";
    public string InsideTemperature { get; set; } = "";
    public string InsideHumidity { get; set; } = "";
    public string OutsideTemperature { get; set; } = "";
    public string OutsideHumidity { get; set; } = "";
    public int RequestTimeoutSeconds { get; set; } = 15;
    public int HeartbeatIntervalSeconds { get; set; } = 30;

    public IReadOnlyDictionary<string, string> Entities => new Dictionary<string, string>
    {
        ["insideTemperature"] = InsideTemperature,
        ["insideHumidity"] = InsideHumidity,
        ["outsideTemperature"] = OutsideTemperature,
        ["outsideHumidity"] = OutsideHumidity
    };

    public string? ValidationError(string contentRoot)
    {
        if (!Enabled) return null;
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return "HomeAssistant.Url must be an HTTP(S) base URL without credentials, query or fragment.";
        if (Entities.Values.Any(id => !System.Text.RegularExpressions.Regex.IsMatch(id ?? "", @"^sensor\.[a-z0-9_]+$")) ||
            Entities.Values.Distinct(StringComparer.Ordinal).Count() != 4)
            return "HomeAssistant requires four distinct sensor entity IDs.";
        if (RequestTimeoutSeconds is < 1 or > 120 || HeartbeatIntervalSeconds is < 1 or > 300)
            return "HomeAssistant request timeout must be 1..120 seconds and heartbeat interval 1..300 seconds.";
        if (!string.IsNullOrEmpty(TokenFile))
        {
            if (!Path.IsPathFullyQualified(TokenFile) || TokenFile.StartsWith(@"\\", StringComparison.Ordinal))
                return "HomeAssistant.TokenFile must be an absolute path outside the application content directory.";
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentRoot));
            var file = Path.GetFullPath(TokenFile);
            if (file.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return "HomeAssistant.TokenFile must be outside the application content directory.";
        }
        return null;
    }

    public Uri WebSocketUri()
    {
        var uri = new UriBuilder(Url);
        uri.Scheme = uri.Scheme == "https" ? "wss" : "ws";
        uri.Path = uri.Path.TrimEnd('/') + "/api/websocket";
        return uri.Uri;
    }
}
