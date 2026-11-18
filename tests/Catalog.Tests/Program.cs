using cherished_server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;

var tests = new (string Name, Func<Task> Run)[]
{
    ("nested catalog, rebuild counts and stable identities", Catalog),
    ("hidden/flagged persistence and concurrent refresh", Marks),
    ("failed and cancelled refresh preserve snapshot", FailedRefresh),
    ("recoverable service refresh and cancellation", Service),
    ("explicit API availability and original bytes", Api),
    ("persistence errors and timing validation", Configuration),
    ("configured writable state directory", StateDirectory),
    ("cloud reparse tags remain distinct from directory links", ReparseDirectories),
    ("Home Assistant synchronization, recovery, isolation and secret-safe streaming", HomeAssistantTests.Run)
};
foreach (var (name, run) in tests)
{
    await run();
    Console.WriteLine($"PASS {name}");
}
Console.WriteLine($"{tests.Length} catalog regression groups passed.");

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task WithFolder(Func<string, string, Task> test)
{
    var temporary = Path.Combine(Path.GetTempPath(), "cherished-tests-" + Guid.NewGuid().ToString("N"));
    var root = Path.Combine(temporary, "photos");
    var state = Path.Combine(temporary, "state");
    Directory.CreateDirectory(root);
    Directory.CreateDirectory(state);
    try { await test(root, state); }
    finally { Directory.Delete(temporary, true); }
}

static Pool CreatePool(string root, string state, int refresh = 1000) => new(
    Options.Create(new PoolSettings
    {
        PoolFolder = root, PoolFilter = ".jpg, .jpeg", RefreshIntervalMs = refresh
    }), state);

static async Task ReparseDirectories() => await WithFolder(async (root, state) =>
{
    for (uint variant = 0; variant < 16; variant++)
        Check(DirectoryReparsePoints.IsCloudTag(0x9000001Au | (variant << 12)), "all documented cloud variants are traversable");
    foreach (var tag in new uint[] { 0, 0xA0000003u, 0xA000000Cu, 0xB000001Au, 0x90000001u, 0x80000021u })
        Check(!DirectoryReparsePoints.IsCloudTag(tag), "links, name surrogates and unknown tags must not be treated as cloud");
    Check(DirectoryReparsePoints.CanTraverse(root), "ordinary directories remain traversable");
    if (OperatingSystem.IsWindows())
        Check(DirectoryReparsePoints.GetTag(root) == 0, "native tag reader must inspect ordinary directories without hydration");
    File.WriteAllText(Path.Combine(root, "image.jpg"), "image");
    var pool = CreatePool(root, state);
    await pool.BuildPool();
    Check(pool.PoolSize == 1, "tag-aware scan retains normal directory behavior");
});

static async Task Catalog() => await WithFolder(async (root, state) =>
{
    var child = Path.Combine(root, "child");
    var nested = Path.Combine(child, "nested");
    Directory.CreateDirectory(nested);
    File.WriteAllText(Path.Combine(root, "root.jpg"), "root");
    File.WriteAllText(Path.Combine(child, "child.JPG"), "child");
    File.WriteAllText(Path.Combine(nested, "nested.jpeg"), "nested");
    File.WriteAllText(Path.Combine(nested, "ignored.txt"), "not an image");
    var pool = CreatePool(root, state);
    Check(!pool.Snapshot.IsReady, "must start not ready");
    await pool.BuildPool();
    Check(pool.PoolSize == 3, "nested directories must not be double counted");
    var before = pool.Snapshot;
    Check(before.Files.Values.Select(file => file.Path).Distinct().Count() == 3, "each image occurs once");
    Check(before.Images.Single(image => image.Label == Path.Combine("child", "nested")) is not null,
        "labels preserve relative directories");
    foreach (var image in before.Images)
        Check(pool.GetFilePath(image.Id) == before.Files[image.Id].Path, "every boundary/position resolves correctly");
    await pool.BuildPool();
    Check(pool.PoolSize == 3, "rebuild must not accumulate counts");
    var rootId = before.Files.Values.Single(file => file.Path == Path.Combine(root, "root.jpg")).Id;
    File.Delete(Path.Combine(child, "child.JPG"));
    File.WriteAllText(Path.Combine(root, "added.jpg"), "added");
    await pool.BuildPool();
    Check(pool.PoolSize == 3 && before.Images.Count == 3, "old snapshots remain immutable");
    Check(pool.GetFilePath(rootId) == Path.Combine(root, "root.jpg"), "IDs survive ordering changes");
    var removedId = before.Files.Values.Single(file => file.Path == Path.Combine(child, "child.JPG")).Id;
    Check(pool.GetFilePath(removedId) is null, "removed IDs never resolve to replacement images");
});

static async Task Marks() => await WithFolder(async (root, state) =>
{
    for (var index = 0; index < 12; index++)
        File.WriteAllText(Path.Combine(root, $"{index}.jpg"), "image");
    var pool = CreatePool(root, state);
    await pool.BuildPool();
    var before = pool.Snapshot;
    var ids = before.Images.Select(image => image.Id).ToArray();
    await Task.WhenAll(Enumerable.Range(0, 4).SelectMany(_ => ids.Select(id =>
        Task.Run(() => Check(pool.SetFileFlagged(id), "flag must succeed")))));
    Check(File.ReadAllLines(Path.Combine(state, ".flagged")).Length == ids.Length,
        "concurrent flags must not lose writes or add duplicates");
    await Task.WhenAll(
        Task.Run(async () => { for (var i = 0; i < 10; i++) await pool.BuildPool(); }),
        Task.Run(() =>
        {
            foreach (var id in ids.Take(6))
                Check(pool.SetFileHidden(id), "hide must succeed");
        }));
    Check(pool.PoolSize == 6, "refresh must not resurrect concurrently hidden images");
    Check(ids.Take(6).All(id => pool.GetFilePath(id) is null), "hidden images are unavailable immediately");
    Check(before.Images.Count == 12, "hiding must not mutate published snapshots");
    var reloaded = CreatePool(root, state);
    await reloaded.BuildPool();
    Check(reloaded.PoolSize == 6, "hidden entries persist across restart");
    Check(!pool.SetFileFlagged("missing"), "unknown IDs must not produce marks");
});

static async Task FailedRefresh() => await WithFolder(async (root, state) =>
{
    File.WriteAllText(Path.Combine(root, "good.jpg"), "image");
    var pool = CreatePool(root, state);
    await pool.BuildPool();
    var before = pool.Snapshot;
    var moved = root + "-moved";
    Directory.Move(root, moved);
    try
    {
        try { await pool.BuildPool(); throw new Exception("expected scan failure"); }
        catch (DirectoryNotFoundException) { }
        Check(ReferenceEquals(before, pool.Snapshot), "failed refresh must preserve last good snapshot");
    }
    finally { Directory.Move(moved, root); }
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    try { await pool.BuildPool(cancellation.Token); throw new Exception("expected cancellation"); }
    catch (OperationCanceledException) { }
    Check(ReferenceEquals(before, pool.Snapshot), "cancellation must not publish");
    File.Delete(Path.Combine(root, "good.jpg"));
    await pool.BuildPool();
    Check(pool.Snapshot.IsReady && pool.PoolSize == 0, "a successful empty scan replaces old catalog");
});

static async Task WaitFor(Func<bool> condition)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    while (!condition()) await Task.Delay(20, deadline.Token);
}

static async Task Service() => await WithFolder(async (root, state) =>
{
    Directory.Delete(root);
    var pool = CreatePool(root, state);
    var logger = new RecordingLogger<PoolService>();
    using var service = new PoolService(logger, pool);
    await service.StartAsync(CancellationToken.None);
    await WaitFor(() => logger.Errors.Count > 0);
    Check(!pool.Snapshot.IsReady && !service.ExecuteTask!.IsCompleted, "startup failure must not terminate service");
    Directory.CreateDirectory(root);
    File.WriteAllText(Path.Combine(root, "first.jpg"), "image");
    await WaitFor(() => pool.PoolSize == 1);
    File.WriteAllText(Path.Combine(root, "second.jpg"), "image");
    await WaitFor(() => pool.PoolSize == 2);
    var before = pool.Snapshot;
    var moved = root + "-offline";
    Directory.Move(root, moved);
    try
    {
        var previousErrors = logger.Errors.Count;
        await WaitFor(() => logger.Errors.Count > previousErrors);
        Check(ReferenceEquals(before, pool.Snapshot), "service retains a nonempty snapshot after failure");
    }
    finally { Directory.Move(moved, root); }
    using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    await service.StopAsync(stopDeadline.Token);
    Check(service.ExecuteTask!.IsCompletedSuccessfully, "service cancellation must end cleanly");
});

static async Task Api() => await WithFolder(async (root, state) =>
{
    var pool = CreatePool(root, state);
    var controller = new ImageController(pool, NullLogger<Pool>.Instance);
    Check(controller.LoadPool() is ObjectResult { StatusCode: 503 }, "not-ready metadata must be explicit");
    Check(controller.GetImage("missing") is ObjectResult { StatusCode: 503 }, "not-ready image must be explicit");
    await pool.BuildPool();
    Check(controller.LoadPool() is OkObjectResult, "empty ready metadata must be available");
    Check(controller.GetImage("missing") is NotFoundObjectResult, "unknown image must return 404");
    var path = Path.Combine(root, "image.jpg");
    var bytes = new byte[] { 1, 2, 3, 4, 5 };
    File.WriteAllBytes(path, bytes);
    await pool.BuildPool();
    var key = pool.Snapshot.Images.Single().Id;
    var image = controller.GetImage(key) as FileStreamResult;
    Check(image is not null, "known image must be served");
    using (image!.FileStream)
    using (var buffer = new MemoryStream())
    {
        await image.FileStream.CopyToAsync(buffer);
        Check(buffer.ToArray().SequenceEqual(bytes), "serving must preserve original bytes");
    }
    Check(controller.SetFlagged(key) is OkResult, "flag endpoint must persist");
    Check(controller.SetHidden(key) is OkResult, "hide endpoint must persist");
    Check(controller.GetImage(key) is NotFoundObjectResult, "hidden image must return 404, never an empty success");
    File.WriteAllBytes(Path.Combine(root, "disappearing.jpg"), bytes);
    await pool.BuildPool();
    var removed = pool.Snapshot.Images.Single().Id;
    File.Delete(Path.Combine(root, "disappearing.jpg"));
    Check(controller.GetImage(removed) is NotFoundObjectResult, "filesystem disappearance must return unavailable");
});

static async Task Configuration() => await WithFolder(async (root, state) =>
{
    foreach (var interval in new[] { -1, 0, 249, 86400001 })
        Check(!PoolSettings.IsValid(new PoolSettings { PoolFolder = root, PoolFilter = ".jpg", SlideshowIntervalMs = interval }),
            "unsafe slideshow timing must be rejected");
    foreach (var interval in new[] { -1, 0, 999, 86400001 })
        Check(!PoolSettings.IsValid(new PoolSettings { PoolFolder = root, PoolFilter = ".jpg", RefreshIntervalMs = interval }),
            "unsafe refresh timing must be rejected");
    Check(!PoolSettings.IsValid(new PoolSettings { PoolFolder = root, PoolFilter = ",, " }), "empty extensions rejected");
    File.WriteAllText(Path.Combine(root, "image.jpg"), "image");
    var pool = CreatePool(root, state);
    await pool.BuildPool();
    Directory.CreateDirectory(Path.Combine(state, ".hidden"));
    var controller = new ImageController(pool, NullLogger<Pool>.Instance);
    var key = pool.Snapshot.Images.Single().Id;
    Check(controller.SetHidden(key) is ObjectResult { StatusCode: 503 }, "failed persistence must not report success");
    Check(pool.PoolSize == 1, "failed persistence must not hide an image only in memory");
    var before = pool.Snapshot;
    try { await pool.BuildPool(); throw new Exception("expected inaccessible marks to fail refresh"); }
    catch (UnauthorizedAccessException) { }
    Check(ReferenceEquals(before, pool.Snapshot), "unreadable hidden state must not silently resurrect hidden files");
});

static async Task StateDirectory() => await WithFolder(async (root, state) =>
{
    var configured = Path.Combine(state, "service-state");
    File.WriteAllText(Path.Combine(root, "image.jpg"), "image");
    var settings = Options.Create(new PoolSettings
    {
        PoolFolder = root, PoolFilter = ".jpg", StateFolder = configured
    });
    var pool = new Pool(settings);
    await pool.BuildPool();
    var key = pool.Snapshot.Images.Single().Id;
    Check(pool.SetFileFlagged(key) && pool.SetFileHidden(key), "state actions must succeed outside the binary folder");
    Check(File.Exists(Path.Combine(configured, ".hidden")) && File.Exists(Path.Combine(configured, ".flagged")),
        "state files must use the configured writable directory");
    var restarted = new Pool(settings);
    await restarted.BuildPool();
    Check(restarted.PoolSize == 0, "configured hidden state must survive service restart");
});

sealed class RecordingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<Exception> Errors { get; } = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (logLevel >= LogLevel.Error && exception is not null) Errors.Enqueue(exception);
    }
}
