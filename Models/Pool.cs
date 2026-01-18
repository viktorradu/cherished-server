using Microsoft.Extensions.Options;

public class Pool{
    private readonly List<PoolItem> _Pool = new List<PoolItem>();
    private string PoolFolder = "";
    private string PoolFilter = "";
    public int PoolSize { get; set; }
    public int SlideshowIntervalMs { get; set; }

    public Pool(IOptions<PoolSettings> settings)
    {
        PoolFolder = settings.Value.PoolFolder;
        PoolFilter = settings.Value.PoolFilter;
        SlideshowIntervalMs = settings.Value.SlideshowIntervalMs;
    }

    public async Task BuildPool()
    {
        _Pool.Clear();
        await BuildPoolRecursive(PoolFolder);
    }

    private async Task BuildPoolRecursive(string startDir)
    {
        var dirs = new List<string>
        {
            startDir
        };
        dirs.AddRange(Directory.GetDirectories(startDir));
        foreach (var dir in dirs)
        {
            var fileCount = GetFileList(dir).Length;
            if (fileCount > 0)
            {
                _Pool.Add(new PoolItem(dir, PoolSize, PoolSize + fileCount - 1));
                PoolSize += fileCount;
            }
            if(dir != startDir){
                await BuildPoolRecursive(dir);
            }
        }
    }

    public string GetFilePath(int position)
    {
        var from = 0;
        var to = _Pool.Count - 1;
        var safety = 0;
        while (from < to && safety < PoolSize)
        {
            var test = (int)Math.Floor((to + from) / 2.0);
            if (_Pool[test].FirstFilePosition > position)
            {
                to = test;
            }
            if (_Pool[test].LastFilePosition > position)
            {
                to = test;
            }
            if (_Pool[test].LastFilePosition < position)
            {
                from = test + 1;
            }
            safety++;
        }
        return GetFileList(_Pool[from].Folder)[position - _Pool[from].FirstFilePosition];
    }

    public void SetFileHidden(int position)
    {
        WritePositionToFile(position, ".hidden");
    }

    public void SetFileFlagged(int position)
    {
        WritePositionToFile(position, ".flagged");
    }

    public List<string> GetHiddenFiles()
    {
        var hiddenFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".hidden");
        return File.Exists(hiddenFilePath) ? File.ReadAllLines(hiddenFilePath).ToList() : new List<string>();
    }

    private void WritePositionToFile(int position, string fileName)
    {
        var path = GetFilePath(position);
        var filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
        var lines = File.Exists(filePath) ? File.ReadAllLines(filePath).ToList() : new List<string>();
        if (!lines.Contains(path))
        {
            lines.Add(path);
            File.WriteAllLines(filePath, lines);
        }
    }

    public string GetFileLocationFromPath(string path){
        var pathPart = path.Replace(PoolFolder, "");
        return pathPart.StartsWith(Path.DirectorySeparatorChar.ToString()) ? pathPart.Substring(1) : pathPart;
    }

    private string[] GetFileList(string dir)
    {
        var extensions = PoolFilter.Split(",".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
        return Directory.GetFiles(dir)
            .Where(f=>extensions.Any(e=> f.ToLower().EndsWith(e)))
            .ToArray();
    }
}