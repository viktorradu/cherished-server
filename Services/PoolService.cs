
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
            if(_Pool.PoolSize == 0){
                _logger.LogInformation("Indexing");
                await _Pool.BuildPool();
                _logger.LogInformation("Indexing complete. {} files included in the pool", _Pool.PoolSize);
            } 
           await Task.Delay(10000, stoppingToken);
        }

        _logger.LogInformation("Indexer Service is stopping.");
    }
}