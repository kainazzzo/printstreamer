namespace PrintStreamer.Services
{
    public class MoonrakerHostedService : IHostedService, IDisposable
    {
        private readonly IConfiguration _config;
        private readonly ILogger<MoonrakerHostedService> _logger;
        private Task? _executingTask;
        private CancellationTokenSource? _cts;

        public MoonrakerHostedService(IConfiguration config, ILogger<MoonrakerHostedService> logger)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Always start the poller as part of the host lifecycle. The app now always polls Moonraker
            // and serves the UI (serving can be disabled via Serve:Enabled).
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;
            _executingTask = Task.Run(async () =>
            {
                try
                {
                    _logger.LogInformation("MoonrakerHostedService starting poller");
                    await MoonrakerPoller.PollAndStreamJobsAsync(_config, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    _logger.LogInformation("MoonrakerHostedService canceled");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "MoonrakerHostedService error");
                }
            }, token);

            // don't block StartAsync; return completed
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_executingTask == null) return;

            try
            {
                _cts?.Cancel();
            }
            catch { }

            try
            {
                await Task.WhenAny(_executingTask, Task.Delay(Timeout.Infinite, cancellationToken));
            }
            catch (OperationCanceledException) { }
        }

        public void Dispose()
        {
            try { _cts?.Dispose(); } catch { }
        }
    }
}
