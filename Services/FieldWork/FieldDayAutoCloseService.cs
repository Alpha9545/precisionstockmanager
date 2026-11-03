namespace PlantStockManager.Services.FieldWork
{
    // Field Work Phase 1 (2026-11-03): closes every field day still Open after its IST date has passed ("AutoClosed",
    // flagged for review). It runs every FieldWork:AutoCloseIntervalMinutes (default 5) instead of once at 00:00, so a
    // server that was down or restarting at midnight still catches up on the next tick. Each run is idempotent: the
    // repository only touches rows that are Open at that moment (FieldDayRepository.AutoCloseStaleAsync). Errors are
    // logged and the next tick tries again; nothing runs while 2026-11-03_FieldWorkDays.sql is not applied.
    public sealed class FieldDayAutoCloseService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly FieldWorkOptions _options;
        private readonly ILogger<FieldDayAutoCloseService> _logger;

        public FieldDayAutoCloseService(IServiceScopeFactory scopes, FieldWorkOptions options, ILogger<FieldDayAutoCloseService> logger)
        {
            _scopes = scopes;
            _options = options;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_options.AutoCloseEnabled)
            {
                _logger.LogInformation("Field Work auto-close is disabled (FieldWork:AutoCloseEnabled = false).");
                return;
            }
            var interval = TimeSpan.FromMinutes(Math.Clamp(_options.AutoCloseIntervalMinutes, 1, 60));
            try
            {
                // let the application finish starting first
                await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
                using var timer = new PeriodicTimer(interval);
                do
                {
                    await RunOnceAsync();
                }
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }

        public async Task<int> RunOnceAsync()
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<FieldDayService>();
                if (!await service.SchemaReadyAsync())
                    return 0;
                var closed = await service.AutoCloseStaleAsync();
                if (closed > 0)
                    _logger.LogInformation("Field Work: {Count} open field day(s) of an earlier date auto-closed.", closed);
                return closed;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Field Work auto-close failed; it is retried on the next tick.");
                return 0;
            }
        }
    }
}
