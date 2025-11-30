namespace PrintStreamer.Services
{
    internal class MoonrakerHostedService : IHostedService, IDisposable
    {
        private readonly MoonrakerPoller _pollerService;
        private readonly ILogger<MoonrakerHostedService> _logger;
        private Task? _executingTask;
        private CancellationTokenSource? _cts;

        public MoonrakerHostedService(MoonrakerPoller pollerService, ILogger<MoonrakerHostedService> logger)
        {
            _pollerService = pollerService ?? throw new ArgumentNullException(nameof(pollerService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;
            _executingTask = Task.Run(async () =>
            {
                try
                {
                    _logger.LogInformation("MoonrakerHostedService starting poller");
                    await _pollerService.PollAndStreamJobsAsync(token);;
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
