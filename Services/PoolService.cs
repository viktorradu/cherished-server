public class PoolService : BackgroundService
{
    private readonly Pool _pool;
    private readonly ILogger<PoolService> _logger;

    public PoolService(ILogger<PoolService> logger, Pool pool)
    {
        _logger = logger;
        _pool = pool;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Indexer service is starting.");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _pool.BuildPool(stoppingToken);
                    _logger.LogInformation("Indexing complete. {Count} files included in the pool.", _pool.PoolSize);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    _logger.LogError(error, "Catalog refresh failed; retaining the last successful snapshot.");
                }
                await Task.Delay(_pool.RefreshIntervalMs, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            _logger.LogInformation("Indexer service is stopping.");
        }
    }
}
