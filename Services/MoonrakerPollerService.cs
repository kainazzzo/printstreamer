using Microsoft.Extensions.Logging;

namespace PrintStreamer.Services
{
    /// <summary>
    /// Service that starts and manages the Moonraker polling loop.
    /// Now uses the event-driven MoonrakerPoller.PollAndStreamJobsAsync for orchestration.
    /// PrintStreamOrchestrator subscribes to PrinterState events from MoonrakerPoller.
    /// </summary>
    public class MoonrakerPollerService
    {
        private readonly IConfiguration _config;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<MoonrakerPollerService> _logger;

        public MoonrakerPollerService(
            IConfiguration config,
            ILoggerFactory loggerFactory,
            ILogger<MoonrakerPollerService> logger)
        {
            _config = config;
            _loggerFactory = loggerFactory;
            _logger = logger;
        }

        /// <summary>
        /// Start polling Moonraker and fire printer state events for subscribers (PrintStreamOrchestrator, etc.)
        /// </summary>
        public async Task StartPollingAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("[MoonrakerPollerService] Starting polling loop via MoonrakerPoller");
            
            try
            {
                // Call the event-driven static polling method
                await MoonrakerPoller.PollAndStreamJobsAsync(_config, _loggerFactory, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[MoonrakerPollerService] Polling cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MoonrakerPollerService] Unexpected error in polling loop");
            }
        }
    }
}
