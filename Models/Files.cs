class Files{

    class PoolItem{
        public int FirstFilePosition;
        public int LastFilePosition;
        public string Folder;
        
    }
    List<PoolItem> _Pool = new List<PoolItem>();
    int PoolSize;
    public string PoolFilter;

private string GetFilePath(int position)
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