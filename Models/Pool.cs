public class Pool{
    private readonly List<PoolItem> _Pool = new List<PoolItem>();
    private string PoolFolder = "";
    private string PoolFilter = "";
    private int PoolSize { get; set; }


    public async Task BuildPool(CancellationToken stoppingToken)
    {
        _Pool.Clear();
        await BuildPoolRecursive(PoolFolder);
    }

    private async Task BuildPoolRecursive(string startDir)
    {
        foreach (var dir in Directory.GetDirectories(startDir))
        {
            var fileCount = GetFileList(dir).Length;
            if (fileCount > 0)
            {
                _Pool.Add(new PoolItem(dir, PoolSize, PoolSize + fileCount - 1));
                PoolSize += fileCount;
            }
            await BuildPoolRecursive(dir);
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

    private string[] GetFileList(string dir)
    {
        var extensions = PoolFilter.Split(",".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
        return Directory.GetFiles(dir)
            .Where(f=>extensions.Any(e=> f.ToLower().EndsWith(e)))
            .ToArray();
    }
}