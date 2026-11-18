using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

public sealed record PoolImage(string Id, string Label);

public sealed class PoolSnapshot
{
    public bool IsReady { get; }
    public FrozenDictionary<string, PoolItem> Files { get; }
    public IReadOnlyList<PoolImage> Images { get; }

    public PoolSnapshot(bool isReady, IEnumerable<PoolItem> files, string root)
    {
        IsReady = isReady;
        Files = files.ToFrozenDictionary(file => file.Id, StringComparer.Ordinal);
        Images = Array.AsReadOnly(Files.Values
            .OrderBy(file => file.Id, StringComparer.Ordinal)
            .Select(file =>
            {
                var label = Path.GetRelativePath(root, Path.GetDirectoryName(file.Path) ?? root);
                return new PoolImage(file.Id, label == "." ? "" : label);
            })
            .ToArray());
    }
}

public class Pool
{
    private readonly string _folder;
    private readonly HashSet<string> _extensions;
    private readonly string _stateFolder;
    private readonly object _publishLock = new();
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private PoolSnapshot _snapshot;

    public PoolSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public int PoolSize => Snapshot.Images.Count;
    public int SlideshowIntervalMs { get; }
    public int RefreshIntervalMs { get; }

    public Pool(IOptions<PoolSettings> settings)
        : this(settings, string.IsNullOrWhiteSpace(settings.Value.StateFolder)
            ? AppDomain.CurrentDomain.BaseDirectory : settings.Value.StateFolder) { }

    public Pool(IOptions<PoolSettings> settings, string stateFolder)
    {
        var value = settings.Value;
        if (!PoolSettings.IsValid(value))
            throw new ArgumentException("Invalid pool folder, filter, or timing settings.", nameof(settings));
        _folder = Path.GetFullPath(value.PoolFolder);
        _stateFolder = Path.GetFullPath(stateFolder);
        Directory.CreateDirectory(_stateFolder);
        _extensions = value.PoolFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        SlideshowIntervalMs = value.SlideshowIntervalMs;
        RefreshIntervalMs = value.RefreshIntervalMs;
        _snapshot = new PoolSnapshot(false, [], _folder);
    }

    public async Task BuildPool(CancellationToken cancellationToken = default)
    {
        await _buildLock.WaitAsync(cancellationToken);
        try
        {
            var files = await Task.Run(() => Scan(cancellationToken), cancellationToken);
            lock (_publishLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hidden = ReadMarks(".hidden");
                var snapshot = new PoolSnapshot(true, files.Where(file => !hidden.Contains(file.Path)), _folder);
                cancellationToken.ThrowIfCancellationRequested();
                Volatile.Write(ref _snapshot, snapshot);
            }
        }
        finally
        {
            _buildLock.Release();
        }
    }

    private List<PoolItem> Scan(CancellationToken cancellationToken)
    {
        var files = new List<PoolItem>();
        var pending = new Stack<string>();
        pending.Push(_folder);
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!DirectoryReparsePoints.CanTraverse(directory))
                throw new IOException($"Image directory is a link or unsupported reparse point: {directory}");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (DirectoryReparsePoints.CanTraverse(entry))
                        pending.Push(entry);
                }
                else if (_extensions.Contains(Path.GetExtension(entry)))
                {
                    var identity = OperatingSystem.IsWindows() ? entry.ToUpperInvariant() : entry;
                    var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
                    files.Add(new PoolItem(id, entry));
                }
            }
        }
        return files;
    }

    public string? GetFilePath(string id) =>
        Snapshot.Files.TryGetValue(id, out var item) ? item.Path : null;

    public bool SetFileHidden(string id) => WriteMark(id, ".hidden");
    public bool SetFileFlagged(string id) => WriteMark(id, ".flagged");

    private HashSet<string> ReadMarks(string name)
    {
        var path = Path.Combine(_stateFolder, name);
        try
        {
            return File.ReadAllLines(path).ToHashSet(PathComparer);
        }
        catch (FileNotFoundException)
        {
            return new HashSet<string>(PathComparer);
        }
    }

    private bool WriteMark(string id, string name)
    {
        lock (_publishLock)
        {
            var current = Snapshot;
            if (!current.Files.TryGetValue(id, out var item))
                return false;
            var marks = ReadMarks(name);
            if (marks.Add(item.Path))
            {
                var destination = Path.Combine(_stateFolder, name);
                var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllLines(temporary, marks);
                    File.Move(temporary, destination, true);
                }
                finally
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
            }
            if (name == ".hidden")
                Volatile.Write(ref _snapshot, new PoolSnapshot(current.IsReady,
                    current.Files.Values.Where(file => file.Id != id), _folder));
            return true;
        }
    }
}
