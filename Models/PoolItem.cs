internal class PoolItem
    {
        public int FirstFilePosition;
        public string Folder;
        public int LastFilePosition;

        public PoolItem(string folder, int firstFile, int lastFile)
        {
            Folder = folder;
            LastFilePosition = lastFile;
            FirstFilePosition = firstFile;
        }   
    }