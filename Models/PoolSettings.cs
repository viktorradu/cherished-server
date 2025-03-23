public class PoolSettings
{
    public string PoolFolder { get; set; }
    public string PoolFilter { get; set; }
    public int SlideshowIntervalMs { get; set; }
    
    public PoolSettings()
    {
        PoolFolder = "";
        PoolFilter = "";
        SlideshowIntervalMs = 3000;
    }
}