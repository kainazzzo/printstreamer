using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
            // Only start background poll/streaming based on configured mode
            var mode = _config.GetValue<string>("Mode")?.ToLowerInvariant() ?? "serve";

            // For serve mode, optionally start streaming when configured (YouTube:StartInServe)
            var startInServe = _config.GetValue<bool?>("YouTube:StartInServe") ?? false;

            if (mode == "poll" || mode == "stream" || (mode == "serve" && startInServe))
            {
                _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var token = _cts.Token;
                _executingTask = Task.Run(async () =>
                {
                    try
                    {
                        if (mode == "poll")
                        {
                            _logger.LogInformation("MoonrakerHostedService starting in poll mode");
                            await MoonrakerPoller.PollAndStreamJobsAsync(_config, token);
                        }
                        else
                        {
                            _logger.LogInformation("MoonrakerHostedService starting stream mode");
                            var source = _config.GetValue<string>("Stream:Source");
                            var key = _config.GetValue<string>("YouTube:Key");
                            // StartYouTubeStreamAsync now reads Stream:Source and YouTube:Key from IConfiguration internally
                            await MoonrakerPoller.StartYouTubeStreamAsync(_config, token, enableTimelapse: true, timelapseProvider: null);
                        }
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

            _logger.LogInformation("MoonrakerHostedService not started (mode={mode})", mode);
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
