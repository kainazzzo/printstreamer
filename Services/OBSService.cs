using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OBSWebsocketDotNet;
using OBSWebsocketDotNet.Types;
using System.Collections.ObjectModel;

namespace PrintStreamer.Services;

public record ActiveOutput(
    string Name,
    OutputType Type,
    bool Active,
    TimeSpan? Duration = null,
    long? Bytes = null);

public enum OutputType
{
    Stream,
    Record,
    VirtualCam,
    ReplayBuffer
}

public interface IOBSService
{
    Task ConnectAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ActiveOutput>> GetActiveOutputsAsync(CancellationToken ct = default);
    Task StopCurrentStreamAsync(CancellationToken ct = default);
    Task StopStreamAsync(string outputName = "adv_stream", CancellationToken ct = default);
}

public class OBSService : IOBSService, IDisposable
{
    private readonly ILogger<OBSService> _logger;
    private readonly IConfiguration _config;
    private readonly OBSWebsocket _obs = new();

    private readonly string _url;
    private readonly string _password;

    public OBSService(ILogger<OBSService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _config = configuration;

        // Bind from configuration with fallbacks
        _url = _config["OBS:Url"]?.TrimEnd('/') ?? "ws://127.0.0.1:4455";
        _password = _config["OBS:Password"] ?? string.Empty;

        _logger.LogInformation("OBS WebSocket configured for {Url} (password set: {HasPassword})",
            _url, !string.IsNullOrEmpty(_password));

        // Optional event logging
        _obs.Connected += (_, __) => _logger.LogInformation("Connected to OBS WebSocket");
        _obs.Disconnected += (_, reason) => _logger.LogWarning("OBS WebSocket disconnected: {Reason}", reason?.DisconnectReason);
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_obs.IsConnected)
            return;

        _logger.LogInformation("Connecting to OBS at {Url}...", _url);

        for (int i = 0; i < 5; i++)
        {
            try
            {
                _obs.ConnectAsync(_url, _password);
                // ConnectAsync may be void in this wrapper; wait for IsConnected to ensure connection established.
                var wait = 0;
                while (!_obs.IsConnected && wait++ < 50)
                {
                    await Task.Delay(100, ct);
                }
                if (!_obs.IsConnected)
                {
                    throw new InvalidOperationException("OBS Websocket failed to connect within the expected time");
                }
                _logger.LogInformation("Successfully connected to OBS");
                return;
            }
            catch (Exception ex) when (ex is AuthFailureException)
            {
                _logger.LogError("OBS authentication failed. Check OBS:Password in configuration.");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Connection attempt {Attempt} failed. Retrying...", i + 1);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }

        throw new InvalidOperationException($"Failed to connect to OBS at {_url} after multiple attempts");
    }

    public async Task<IReadOnlyList<ActiveOutput>> GetActiveOutputsAsync(CancellationToken ct = default)
    {
        await EnsureConnected(ct);

        var outputs = new List<ActiveOutput>();

        var streamStatus = _obs.GetStreamStatus();
        var recordStatus = _obs.GetRecordStatus();
        var virtualCamStatus = _obs.GetVirtualCamStatus();

        if (streamStatus.IsActive)
        {
            outputs.Add(new ActiveOutput(
                Name: "adv_stream",
                Type: OutputType.Stream,
                Active: true,
            Duration: streamStatus.Duration > 0 ? TimeSpan.FromMilliseconds(streamStatus.Duration) : null,
            Bytes: streamStatus.BytesSent));
        }

        if (recordStatus.IsRecording)
        {
            outputs.Add(new ActiveOutput(
                Name: "adv_file_output",
                Type: OutputType.Record,
                Active: true,
            Duration: recordStatus.RecordingDuration > 0 ? TimeSpan.FromMilliseconds(recordStatus.RecordingDuration) : null,
            Bytes: recordStatus.RecordingBytes));
        }

        if (virtualCamStatus.IsActive)
        {
            outputs.Add(new ActiveOutput("Virtual Camera", OutputType.VirtualCam, Active: true));
        }

        return outputs.AsReadOnly();
    }

    public async Task StopCurrentStreamAsync(CancellationToken ct = default)
    {
        await EnsureConnected(ct);

        var status = _obs.GetStreamStatus();
        if (!status.IsActive)
        {
            _logger.LogInformation("No active stream to stop");
            return;
        }

        _logger.LogInformation("Stopping current stream...");
        _obs.StopStream();
        _logger.LogInformation("Stream stopped successfully");
    }

    public async Task StopStreamAsync(string outputName = "adv_stream", CancellationToken ct = default)
    {
        await EnsureConnected(ct);

        // The client does not provide a generic StopOutput. Translate common output names into concrete calls
        switch (outputName?.ToLowerInvariant())
        {
            case "adv_stream":
            case "stream":
            case "rtmp":
                _obs.StopStream();
                _logger.LogInformation("Stopped stream output '{OutputName}'", outputName);
                break;
            case "adv_file_output":
            case "record":
                _obs.StopRecord();
                _logger.LogInformation("Stopped recording output '{OutputName}'", outputName);
                break;
            default:
                _logger.LogWarning("Unknown output '{OutputName}'; attempting to stop stream as fallback", outputName);
                _obs.StopStream();
                break;
        }
    }

    private async Task EnsureConnected(CancellationToken ct)
    {
        if (!_obs.IsConnected)
            await ConnectAsync(ct);
    }

    public void Dispose()
    {
        if (_obs.IsConnected)
        {
            try { _obs.Disconnect(); }
            catch { /* ignore */ }
        }
    }
}
