using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using PrintStreamer.Services;
using PrintStreamer.Timelapse;
using PrintStreamer.Overlay;
using PrintStreamer.Interfaces;

namespace PrintStreamer.Services;

/// <summary>
/// Hosted service responsible for application startup orchestration and service wiring.
/// Handles the coordination between various services that need to be connected at startup.
/// </summary>
internal class ApplicationStartupHostedService : IHostedService
{
    private readonly ILogger<ApplicationStartupHostedService> _logger;
    private readonly IConfiguration _config;
    private readonly IStreamOrchestrator _orchestrator;
    private readonly AudioBroadcastService _audioBroadcast;
    private readonly PrintStreamOrchestrator _printStreamOrchestrator;
    private readonly OverlayTextService _overlayTextSvc;
    private readonly StreamService _streamService;
    private readonly ITimelapseManager _timelapseManager;
    private readonly IMoonrakerPoller _moonrakerPoller;
    private readonly PrinterConsoleService _printerConsoleService;
    private readonly IHostApplicationLifetime _lifetime;

    // Background tasks
    private Task? _moonrakerPollingTask;
    private CancellationTokenSource? _moonrakerCts;

    public ApplicationStartupHostedService(
        ILogger<ApplicationStartupHostedService> logger,
        IConfiguration config,
        IStreamOrchestrator orchestrator,
        AudioBroadcastService audioBroadcast,
        PrintStreamOrchestrator printStreamOrchestrator,
        OverlayTextService overlayTextSvc,
        StreamService streamService,
        TimelapseManager timelapseManager,
        IMoonrakerPoller moonrakerPoller,
        PrinterConsoleService printerConsoleService,
        IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _config = config;
        _orchestrator = orchestrator;
        _audioBroadcast = audioBroadcast;
        _printStreamOrchestrator = printStreamOrchestrator;
        _overlayTextSvc = overlayTextSvc;
        _streamService = streamService;
        _timelapseManager = timelapseManager;
        _moonrakerPoller = moonrakerPoller;
        _printerConsoleService = printerConsoleService;
        _lifetime = lifetime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ApplicationStartupHostedService starting...");

        // Wire up service callbacks and event handlers
        WireUpServices();

        // Start dependent services
        StartDependentServices();

        // Register application lifecycle handlers
        RegisterLifecycleHandlers();

        _logger.LogInformation("ApplicationStartupHostedService started");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("ApplicationStartupHostedService stopping...");

        // Stop Moonraker polling
        try
        {
            StopMoonrakerPolling();
            _logger.LogDebug("Moonraker polling stopped");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping Moonraker polling");
        }

        // Stop dependent services
        try
        {
            await _printerConsoleService.StopAsync(cancellationToken);
            _logger.LogDebug("Printer console service stopped");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping printer console service");
        }

        // Cleanup will be handled by the lifecycle handlers
        _logger.LogInformation("ApplicationStartupHostedService stopped");
    }

    private void WireUpServices()
    {
        _logger.LogDebug("Wiring up service callbacks and event handlers...");

        // Wire up audio track completion callback to orchestrator
        _audioBroadcast.SetTrackFinishedCallback(() => _orchestrator.OnAudioTrackFinishedAsync());

        // Wire up PrintStreamOrchestrator to subscribe to PrinterState events from MoonrakerPoller
        MoonrakerPoller.PrintStateChanged += (prev, curr) =>
            _ = _printStreamOrchestrator.HandlePrinterStateChangedAsync(prev, curr, CancellationToken.None);

        _logger.LogDebug("Service wiring completed");
    }

    private void StartDependentServices()
    {
        _logger.LogDebug("Starting dependent services...");

        // Start Moonraker polling service
        try
        {
            StartMoonrakerPolling();
            _logger.LogDebug("Moonraker polling started");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start Moonraker polling");
        }

        // Start printer console service
        try
        {
            _printerConsoleService.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            _logger.LogDebug("Printer console service started");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start printer console service");
        }

        // Ensure overlay text writer is running
        try
        {
            _overlayTextSvc.Start();
            _logger.LogDebug("Overlay text service started");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start overlay text service");
        }
    }

    private void RegisterLifecycleHandlers()
    {
        _logger.LogDebug("Registering application lifecycle handlers...");

        // Handle graceful shutdown
        _lifetime.ApplicationStopping.Register(() =>
        {
            _logger.LogInformation("Application shutting down...");
            // Cleanup will be handled by individual services' disposal
        });

        // Start local stream AFTER the web server is listening (to avoid race condition)
        _lifetime.ApplicationStarted.Register(() =>
        {
            if (_config.GetValue<bool?>("Stream:Local:Enabled") ?? false)
            {
                _logger.LogInformation("Web server ready, starting local preview stream...");

                // Small delay to give the audio feed a moment to start before ffmpeg connects
                Task.Delay(500).ContinueWith(async _ =>
                {
                    try
                    {
                        if (!_streamService.IsStreaming)
                        {
                            _logger.LogInformation("Starting local preview stream");
                            await _streamService.StartStreamAsync(null, CancellationToken.None);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to start local preview stream");
                    }
                });
            }
        });

        _logger.LogDebug("Lifecycle handlers registered");
    }

    private void StartMoonrakerPolling()
    {
        _moonrakerCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping);
        var token = _moonrakerCts.Token;
        _moonrakerPollingTask = Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("ApplicationStartupHostedService starting Moonraker poller");
                await _moonrakerPoller.PollAndStreamJobsAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _logger.LogInformation("ApplicationStartupHostedService Moonraker polling canceled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ApplicationStartupHostedService Moonraker polling error");
            }
        }, token);
    }

    private void StopMoonrakerPolling()
    {
        if (_moonrakerPollingTask == null) return;

        try
        {
            _moonrakerCts?.Cancel();
        }
        catch { }

        try
        {
            _moonrakerPollingTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error waiting for Moonraker polling task to stop");
        }
    }
}