public class PoolSettings
{
    public string PoolFolder { get; set; }
    public string PoolFilter { get; set; }
    public int SlideshowIntervalMs { get; set; }
    public int RefreshIntervalMs { get; set; } = 10000;
    public string StateFolder { get; set; } = "";

    public static bool IsValid(PoolSettings value) =>
        !string.IsNullOrWhiteSpace(value.PoolFolder) &&
        !string.IsNullOrWhiteSpace(value.PoolFilter) &&
        value.PoolFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            is { Length: > 0 } extensions &&
        extensions.All(extension => extension.StartsWith('.') && extension.Length > 1) &&
        value.SlideshowIntervalMs is >= 250 and <= 86400000 &&
        value.RefreshIntervalMs is >= 1000 and <= 86400000;
    
    public PoolSettings()
    {
        PoolFolder = "";
        PoolFilter = "";
        SlideshowIntervalMs = 3000;
    }
}