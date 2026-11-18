using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

public static class DirectoryReparsePoints
{
    public static bool IsCloudTag(uint tag) =>
        (tag & 0xFFFF0FFFu) == 0x9000001Au;

    public static bool CanTraverse(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException error)
        {
            throw new DirectoryNotFoundException($"Image directory is unavailable: {path}", error);
        }
        if ((attributes & FileAttributes.ReparsePoint) == 0)
            return true;
        return OperatingSystem.IsWindows() && IsCloudTag(GetTag(path));
    }

    [SupportedOSPlatform("windows")]
    public static uint GetTag(string path)
    {
        var handle = FindFirstFile(Path.TrimEndingDirectorySeparator(path), out var data);
        if (handle == new IntPtr(-1))
            throw new IOException($"Could not inspect directory reparse tag: {path}",
                new Win32Exception(Marshal.GetLastWin32Error()));
        try { return data.ReparseTag; }
        finally { FindClose(handle); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindData
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint ReparseTag;
        public uint Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateFileName;
    }

    [DllImport("kernel32.dll", EntryPoint = "FindFirstFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFile(string path, out FindData data);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);
}
