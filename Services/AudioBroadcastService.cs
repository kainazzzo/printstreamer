using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Threading;
using System.Runtime.CompilerServices;
using System.IO;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace PrintStreamer.Services
{
    /// <summary>
    /// Centralized live MP3 broadcaster. Encodes audio (tracks or silence) once and fan-outs
    /// byte chunks to all subscribers. New subscribers pick up at the live edge.
    /// </summary>
    public sealed class AudioBroadcastService : IDisposable
    {
        private readonly AudioService _audio;
        private readonly IConfiguration _config;
        private readonly ILogger<AudioBroadcastService> _logger;
        private readonly object _lock = new();
        private readonly List<Channel<byte[]>> _subscribers = new();
        private long _broadcastedBytes = 0;
        private CancellationTokenSource _cts = new();
        private Task? _loopTask;
        private bool _disposed;
        private DateTime _lastBroadcastAt = DateTime.MinValue;
        private volatile bool _featureEnabled = true;

        // Track completion callback
        private Func<Task>? _onTrackFinished;

    // current ffmpeg process (if streaming via ffmpeg)
    private Process? _ffmpegProc;
        // ffmpeg failure/backoff tracking
        private int _ffmpegConsecutiveFailures = 0;
        private readonly int _ffmpegBaseBackoffMs = 500;
        private readonly int _ffmpegMaxBackoffMs = 10_000;
        // internal HTTP feed for single persistent ffmpeg to pull from
        private System.Net.HttpListener? _feedListener;
        private int _feedPort;
        private Task? _feedTask;

        public AudioBroadcastService(AudioService audio, IConfiguration config, ILogger<AudioBroadcastService> logger)
        {
            _audio = audio;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config;
            _featureEnabled = _config.GetValue<bool?>("Audio:Enabled") ?? true;
            // Start the internal feed and ffmpeg supervisor so live position is continuous
            EnsureFeedStarted();
            EnsureFfmpegSupervisorStarted();
            // Prime playback: if AudioService has no current track, pick one so ffmpeg can start immediately
            try
            {
                if (string.IsNullOrWhiteSpace(_audio.CurrentPath))
                {
                    // First attempt to follow existing playback behavior (queue, repeat rules etc.)
                    if (_audio.TryGetNextTrack(out var p) && !string.IsNullOrWhiteSpace(p))
                    {
                        _audio.Play();
                        _logger.LogInformation("[AudioBroadcast] Preloaded initial track: {File}", System.IO.Path.GetFileName(p));
                    }
                    else
                    {
                        // No explicit next track available; pick a random track to serve as the initial playback
                        if (_audio.TrySelectRandomTrack(out var rp) && !string.IsNullOrWhiteSpace(rp))
                        {
                            _audio.Play();
                            _logger.LogInformation("[AudioBroadcast] Preloaded initial random track: {File}", System.IO.Path.GetFileName(rp));
                        }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Set a callback to be invoked when an audio track finishes playing
        /// </summary>
        public void SetTrackFinishedCallback(Func<Task>? callback)
        {
            lock (_lock)
            {
                _onTrackFinished = callback;
            }
        }

        /// <summary>
        /// Apply a new Audio:Enabled state immediately. When disabled, existing ffmpeg processes and
        /// subscribers are stopped so clients hear silence right away; when enabled, the supervisor
        /// resumes normal operation.
        /// </summary>
        public void ApplyAudioEnabledState(bool enabled)
        {
            _featureEnabled = enabled;
            if (!enabled)
            {
                try { InterruptFfmpeg(); } catch { }

                // Complete and drop all subscribers so any connected clients stop receiving audio
                lock (_lock)
                {
                    foreach (var ch in _subscribers)
                    {
                        try { ch.Writer.TryComplete(); } catch { }
                    }
                    _subscribers.Clear();
                }
            }
            else
            {
                // Ensure pipelines are running when audio comes back on
                EnsureFeedStarted();
                EnsureFfmpegSupervisorStarted();
            }
        }

        private void EnsureFeedStarted()
        {
            if (_feedTask != null && !_feedTask.IsCompleted) return;
            try
            {
                // pick port from config or use 53333 default
                _feedPort = _config.GetValue<int?>("Audio:FeedPort") ?? 53333;
                var prefix = $"http://127.0.0.1:{_feedPort}/feed/";
                _feedListener = new System.Net.HttpListener();
                _feedListener.Prefixes.Add(prefix);
                _feedListener.Start();
                _feedTask = Task.Run(() => FeedLoopAsync(_cts.Token));
                _logger.LogInformation("[AudioBroadcast] Internal feed listening on {Prefix}", prefix);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AudioBroadcast] Failed to start internal feed");
            }
        }

        private void EnsureFfmpegSupervisorStarted()
        {
            if (_loopTask != null && !_loopTask.IsCompleted) return;
            _loopTask = Task.Run(FfmpegSupervisorAsync);
        }

    public ChannelReader<byte[]> Subscribe(CancellationToken ct)
        {
            var ch = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
            {
                SingleWriter = false,
                SingleReader = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });

                // If audio feature is disabled, complete the channel immediately so callers exit
                if (!_featureEnabled)
                {
                    try { ch.Writer.TryComplete(); } catch { }
                    return ch.Reader;
                }

                lock (_lock)
                {
                    _subscribers.Add(ch);
                    _logger.LogInformation("[AudioBroadcast] Subscriber added, total={Count}", _subscribers.Count);
                }

            // Unsubscribe on cancellation
            ct.Register(() =>
            {
                lock (_lock)
                {
                    _subscribers.Remove(ch);
                    _logger.LogInformation("[AudioBroadcast] Subscriber removed, total={Count}", _subscribers.Count);
                }
                try { ch.Writer.TryComplete(); } catch { }
            });

            return ch.Reader;
        }

        /// <summary>
        /// Public async enumerable representing the live stream. Consumers should await
        /// the returned IAsyncEnumerable to receive MP3 chunks from the live edge.
        /// This enumerator is multicast: each consumer receives the same bytes as they
        /// are emitted (live-only). Clients cannot seek within this stream.
        /// </summary>
        public async IAsyncEnumerable<byte[]> Stream([EnumeratorCancellation] CancellationToken ct)
        {
            var reader = Subscribe(ct);
            await foreach (var chunk in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return chunk;
            }
        }

        private void EnsureLoopStarted()
        {
            if (_loopTask != null && !_loopTask.IsCompleted) return;
            _loopTask = Task.Run(RunLoopAsync);
        }

        private async Task RunLoopAsync()
        {
            // This method retained for compatibility but no longer used; supervisor runs in separate task.
            await Task.CompletedTask;
        }

        private async Task FeedLoopAsync(CancellationToken ct)
        {
            var listener = _feedListener;
            if (listener == null) return;
            try
            {
                while (listener.IsListening && !ct.IsCancellationRequested)
                {
                    var ctx = await listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleFeedRequestAsync(ctx, ct));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AudioBroadcast] Feed loop error");
            }
        }

        private async Task HandleFeedRequestAsync(System.Net.HttpListenerContext ctx, CancellationToken ct)
        {
            try
            {
                // Keep the connection open and stream the currently requested file when available
                while (!ct.IsCancellationRequested)
                {
                    var requested = _audio.CurrentPath;
                    if (string.IsNullOrWhiteSpace(requested) || !File.Exists(requested))
                    {
                        // wait a bit and retry (ffmpeg will block waiting for data)
                        await Task.Delay(200, ct).ConfigureAwait(false);
                        continue;
                    }

                    // Stream the file bytes to the response
                    try
                    {
                        using var fs = File.OpenRead(requested);
                        ctx.Response.StatusCode = 200;
                        ctx.Response.ContentType = "application/octet-stream";
                        var buf = new byte[8192];
                        int read;
                        while ((read = await fs.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
                        {
                            // If the requested track changed, break to close response
                            var now = _audio.CurrentPath;
                            if (!string.Equals(now, requested, StringComparison.OrdinalIgnoreCase)) break;
                            await ctx.Response.OutputStream.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (System.Net.HttpListenerException ex) when (ex.Message.Contains("Connection reset") || ex.Message.Contains("reset by peer"))
                    {
                        // Client disconnected - ignore gracefully
                        break;
                    }
                    catch (IOException ex) when (ex.InnerException is System.Net.Sockets.SocketException se && se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionReset)
                    {
                        // Client disconnected - ignore gracefully
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[AudioBroadcast] Feed request ended (client disconnect or cancellation)");
                    }

                    // Close and return after serving; ffmpeg will reconnect to get the new file
                    try { ctx.Response.OutputStream.Close(); } catch { }
                    break;
                }
            }
            finally
            {
                try { ctx.Response.Close(); } catch { }
            }
        }

        private async Task FfmpegSupervisorAsync()
        {
            var appToken = _cts.Token;
            var feedUrl = $"http://127.0.0.1:{_feedPort}/feed/";
            try
            {
                while (!appToken.IsCancellationRequested)
                {
                        // Only run when audio feature enabled
                        if (!_featureEnabled)
                    {
                        await Task.Delay(1000, appToken).ConfigureAwait(false);
                        continue;
                    }

                    // Use -re to read input at native (real) time so ffmpeg does not transcode the whole file as fast as possible
                    // Remove format hints (-f) to let ffmpeg auto-detect the input format
                    var args = $"-re -reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 2 -i \"{feedUrl}\" -vn -f mp3 -b:a 192k -";
                    var psi = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };

                    Process? proc = null;
                    try
                    {
                        proc = Process.Start(psi);
                        if (proc == null)
                        {
                            throw new Exception("ffmpeg process failed to start");
                        }
                        lock (_lock) { _ffmpegProc = proc; }

                        // Drain stderr for logs
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var err = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                                if (!string.IsNullOrWhiteSpace(err))
                                {
                                    var log = err.Length > 2000 ? err.Substring(err.Length - 2000) : err;
                                    _logger.LogInformation("[AudioBroadcast][ffmpeg] {Log}", log);
                                }
                            }
                            catch { }
                        });

                        var buf = new byte[8192];
                        var outStream = proc.StandardOutput.BaseStream;
                        // read until exit or cancellation
                            while (!appToken.IsCancellationRequested && !proc.HasExited)
                        {
                                // If audio feature disabled while ffmpeg is running, stop the process and break
                                if (!_featureEnabled)
                                {
                                    try { if (!proc.HasExited) proc.Kill(true); } catch { }
                                    break;
                                }
                            var read = await outStream.ReadAsync(buf.AsMemory(0, buf.Length), appToken).ConfigureAwait(false);
                            if (read <= 0) break;
                            var chunk = new byte[read];
                            Buffer.BlockCopy(buf, 0, chunk, 0, read);
                            Interlocked.Add(ref _broadcastedBytes, read);
                            Broadcast(chunk);
                        }

                        // process ended normally; attempt to advance the AudioService so playback continues
                        try
                        {
                            if (!_cts.IsCancellationRequested && _audio.IsPlaying)
                            {
                                // Invoke track finished callback before advancing to next track
                                Func<Task>? callback;
                                lock (_lock)
                                {
                                    callback = _onTrackFinished;
                                }
                                if (callback != null)
                                {
                                    try
                                    {
                                        await callback().ConfigureAwait(false);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, "[AudioBroadcast] Error in track finished callback");
                                    }
                                }

                                // Prefer explicit queue items when advancing between tracks
                                if (_audio.TryConsumeQueue(out var queued))
                                {
                                    _logger.LogInformation("[AudioBroadcast] Auto-advanced to next queued track: {File}", System.IO.Path.GetFileName(queued));
                                }
                                else if (_audio.TryGetNextTrack(out var next))
                                {
                                    _logger.LogInformation("[AudioBroadcast] Auto-advanced to next track: {File}", System.IO.Path.GetFileName(next));
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "[AudioBroadcast] Error auto-advancing track");
                        }

                        // reset failure count
                        _ffmpegConsecutiveFailures = 0;
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        _ffmpegConsecutiveFailures++;
                        _logger.LogError(ex, "[AudioBroadcast] ffmpeg supervisor error");
                        // backoff
var backoff = Math.Min(_ffmpegBaseBackoffMs * (1 << Math.Max(0, _ffmpegConsecutiveFailures - 1)), _ffmpegMaxBackoffMs);
var jitter = RandomNumberGenerator.GetInt32(0, 200);
await Task.Delay(backoff + jitter, appToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        try { if (proc != null && !proc.HasExited) proc.Kill(true); } catch { }
                        lock (_lock) { if (_ffmpegProc == proc) _ffmpegProc = null; }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "[AudioBroadcast] Supervisor fatal error");
            }
        }

        private async Task PumpTranscodeAsync(string? path, CancellationToken appToken)
        {
            // If path == null -> stream silence with lavfi, otherwise transcode the file to MP3
            var args = path == null
                ? "-hide_banner -loglevel error -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100 -f mp3 -b:a 192k -"
                : $"-hide_banner -loglevel error -vn -i \"{path}\" -f mp3 -b:a 192k -";

            var psi = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                await Task.Delay(200, appToken).ConfigureAwait(false);
                return;
            }

            lock (_lock) { _ffmpegProc = proc; }

            // Drain stderr for logs
            _ = Task.Run(async () =>
            {
                try
                {
                    var err = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(err))
                    {
                        var log = err.Length > 2000 ? err.Substring(err.Length - 2000) : err;
                        _logger.LogInformation("[AudioBroadcast][ffmpeg] {Log}", log);
                    }
                }
                catch { }
            });

            var buf = new byte[8192];
            try
            {
                var outStream = proc.StandardOutput.BaseStream;
                    while (!appToken.IsCancellationRequested && !proc.HasExited)
                {
                        // If audio feature disabled while ffmpeg is running, stop the process and break
                        if (!_featureEnabled)
                        {
                            try { if (!proc.HasExited) proc.Kill(true); } catch { }
                            break;
                        }
                    var read = await outStream.ReadAsync(buf.AsMemory(0, buf.Length), appToken).ConfigureAwait(false);
                    if (read <= 0) break;
                    var chunk = new byte[read];
                    Buffer.BlockCopy(buf, 0, chunk, 0, read);
                    Interlocked.Add(ref _broadcastedBytes, read);
                    Broadcast(chunk);

                    // If AudioService changed the requested path, stop this transcode
                    var nowRequested = _audio.CurrentPath;
                    if (!string.Equals(path, nowRequested, StringComparison.OrdinalIgnoreCase))
                    {
                        try { if (!proc.HasExited) proc.Kill(true); } catch { }
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AudioBroadcast] Pump error");
            }
            finally
            {
                try { if (!proc.HasExited) proc.Kill(true); } catch { }
                lock (_lock) { if (_ffmpegProc == proc) _ffmpegProc = null; }
            }
        }

        // PumpFfmpeg removed — broadcasting now streams from an in-memory buffer

        private void Broadcast(byte[] chunk)
        {
            Channel<byte[]>[] targets;
            lock (_lock)
            {
                targets = _subscribers.ToArray();
            }
            foreach (var ch in targets)
            {
                // Non-blocking; drop if slow
                if (!ch.Writer.TryWrite(chunk))
                {
                    // If channel is full/closed, drop the subscriber
                    lock (_lock)
                    {
                        _subscribers.Remove(ch);
                    }
                    try { ch.Writer.TryComplete(); } catch { }
                }
            }
        }

public void Dispose()
{
    if (_disposed) return;
    _disposed = true;

    // Signal cancellation to background loops
    try { _cts.Cancel(); } catch { }

    // Stop internal feed listener
    try
    {
        if (_feedListener != null)
        {
            try { _feedListener.Stop(); } catch { }
            try { _feedListener.Close(); } catch { }
        }
    }
    catch (Exception ex)
    {
        try { _logger.LogDebug(ex, "[AudioBroadcast] Error stopping internal feed listener"); } catch { }
    }

    // Kill ffmpeg process if running
    try
    {
        lock (_lock)
        {
            if (_ffmpegProc != null && !_ffmpegProc.HasExited)
            {
                try { _logger.LogInformation("[AudioBroadcast] Killing ffmpeg process (dispose)"); } catch { }
                try { _ffmpegProc.Kill(true); } catch { }
                try { _ffmpegProc.WaitForExit(3000); } catch { }
            }
            _ffmpegProc = null;
        }
    }
    catch (Exception ex)
    {
        try { _logger.LogDebug(ex, "[AudioBroadcast] Error killing ffmpeg in Dispose"); } catch { }
    }

    // Wait for background tasks to finish (with timeout)
    try
    {
        if (_feedTask != null && !_feedTask.IsCompleted)
        {
            try { _feedTask.Wait(5000); } catch { }
        }
    }
    catch { }

    try
    {
        if (_loopTask != null && !_loopTask.IsCompleted)
        {
            try { _loopTask.Wait(5000); } catch { }
        }
    }
    catch { }

    // Complete and clear subscribers
    lock (_lock)
    {
        foreach (var ch in _subscribers) { try { ch.Writer.TryComplete(); } catch { } }
        _subscribers.Clear();
    }

    try { _cts.Dispose(); } catch { }
}

        /// <summary>
        /// Forcefully interrupt the current ffmpeg process so the supervisor will restart
        /// and pick up the currently requested audio path. Useful to implement immediate
        /// skips when the AudioService changes the current track.
        /// </summary>
        public void InterruptFfmpeg()
        {
            lock (_lock)
            {
                try
                {
                    if (_ffmpegProc != null && !_ffmpegProc.HasExited)
                    {
                        try { _ffmpegProc.Kill(true); } catch { }
                    }
                }
                catch { }
            }
        }

        // Diagnostic status used by the API
        public object GetStatus()
        {
            var state = _audio.GetState();
            return new
            {
                IsPlaying = state.IsPlaying,
                Current = state.Current,
                Queue = state.Queue,
                Shuffle = state.Shuffle,
                Repeat = state.Repeat.ToString(),
                Subscribers = _subscribers.Count,
                BroadcastedBytes = Interlocked.Read(ref _broadcastedBytes)
            };
        }
    }
}
