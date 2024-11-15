
public class PoolService : BackgroundService
{
    private readonly Pool _Pool;
    private readonly ILogger<PoolService> _logger;

    public PoolService(ILogger<PoolService> logger, Pool pool)
    {
        _logger = logger;
        _Pool = pool;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Indexer Service is starting.");

        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Indexing");

            await _Pool.BuildPool(stoppingToken); 
        }

        _logger.LogInformation("Indexer Service is stopping.");
    }
}