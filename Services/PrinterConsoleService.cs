using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;
using System.Net.WebSockets;
using System.Text;

namespace PrintStreamer.Services
{
    // Lightweight console line model
    public class ConsoleLine
    {
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public string Text { get; set; } = string.Empty;
        public string Level { get; set; } = "info";
        public bool FromLocal { get; set; } = false;
    }

    // Simple send result returned to callers
    public class SendResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? SentCommand { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// PrinterConsoleService - maintains a live console feed from Moonraker and provides
    /// helpers to send commands safely. Uses a WebSocket subscription with HTTP polling
    /// fallback, keeps an in-memory ring buffer, and exposes simple quick-action helpers.
    /// </summary>
    public class PrinterConsoleService : IHostedService, IDisposable
    {
        private readonly ILogger<PrinterConsoleService> _log;
        private readonly IConfiguration _cfg;
        private readonly MoonrakerClient _moonrakerClient;
        private readonly List<ConsoleLine> _buffer = new List<ConsoleLine>();
        private readonly object _lock = new object();
        private DateTime _lastSent = DateTime.MinValue;
        private CancellationTokenSource? _cts;
        private Task? _runner;
        private string? _lastDisplayMessage;
        private double? _lastToolTemp;
        private double? _lastBedTemp;

        // Command buffer/queue for throttling and batching
        private readonly Queue<Func<CancellationToken, Task<SendResult>>> _commandQueue = new();
        private readonly SemaphoreSlim _commandQueueSemaphore = new SemaphoreSlim(0);
        private Task? _commandProcessorTask;
        private readonly int _minCommandIntervalMs;

        // Public event for new lines (simple Action-based subscription)
        public event Action<ConsoleLine>? OnNewLine;

        public PrinterConsoleService(IConfiguration cfg, ILogger<PrinterConsoleService> log, MoonrakerClient moonrakerClient)
        {
            _cfg = cfg;
            _log = log;
            _moonrakerClient = moonrakerClient;
            _minCommandIntervalMs = cfg.GetValue<int?>("Stream:Console:MinCommandIntervalMs") ?? 100;
            // Seed a small startup line
            AddLine(new ConsoleLine { Text = "PrinterConsoleService initialized (skeleton)", Level = "info", FromLocal = true });
        }

        private void AddLine(ConsoleLine l)
        {
            lock (_lock)
            {
                var max = _cfg.GetValue<int?>("Stream:Console:MaxLines") ?? 500;
                _buffer.Add(l);
                if (_buffer.Count > max)
                {
                    _buffer.RemoveRange(0, _buffer.Count - max);
                }
            }
            try { OnNewLine?.Invoke(l); } catch { }
        }

        public IReadOnlyList<ConsoleLine> GetLatestLines(int max = 100)
        {
            lock (_lock)
            {
                if (max <= 0) return Array.Empty<ConsoleLine>();
                return _buffer.Skip(Math.Max(0, _buffer.Count - max)).ToList();
            }
        }

        /// <summary>
        /// Get current temperatures from the last status update received from Moonraker
        /// </summary>
        public (double? ToolTemp, double? BedTemp) GetCurrentTemperatures()
        {
            lock (_lock)
            {
                return (_lastToolTemp, _lastBedTemp);
            }
        }

        public Task StartAsync(CancellationToken ct = default)
        {
            // IHostedService start
            var enabled = _cfg.GetValue<bool?>("Stream:Console:Enabled") ?? true;
            if (!enabled)
            {
                _log.LogInformation("PrinterConsoleService disabled via configuration");
                return Task.CompletedTask;
            }

            if (_runner != null) return Task.CompletedTask;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _runner = Task.Run(() => RunAsync(_cts.Token));
            _commandProcessorTask = Task.Run(() => ProcessCommandQueueAsync(_cts.Token));
            _log.LogInformation("PrinterConsoleService background runner started");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct = default)
        {
            try { _cts?.Cancel(); } catch { }
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _cts?.Dispose(); } catch { }
            
            // Clear all event subscriptions to prevent lingering callbacks
            lock (_lock)
            {
                OnNewLine = null;
            }
        }

        private async Task RunAsync(CancellationToken ct)
        {
            // Minimal resilient websocket subscription to display_status for message updates
            while (!ct.IsCancellationRequested)
            {
                ClientWebSocket? ws = null;
                try
                {
                    var baseUrl = _cfg.GetValue<string>("Moonraker:BaseUrl");
                    if (string.IsNullOrWhiteSpace(baseUrl))
                    {
                        await Task.Delay(2000, ct);
                        continue;
                    }
                    var ub = new UriBuilder(baseUrl);
                    if (string.Equals(ub.Scheme, "http", StringComparison.OrdinalIgnoreCase)) ub.Scheme = "ws";
                    else if (string.Equals(ub.Scheme, "https", StringComparison.OrdinalIgnoreCase)) ub.Scheme = "wss";
                    ub.Path = ub.Path.TrimEnd('/') + "/websocket";
                    var wsUri = ub.Uri;

                    ws = new ClientWebSocket();
                    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                    var apiKey = _cfg.GetValue<string>("Moonraker:ApiKey");
                    var authHeader = _cfg.GetValue<string>("Moonraker:AuthHeader") ?? "X-Api-Key";
                    if (!string.IsNullOrWhiteSpace(apiKey))
                    {
                        try { ws.Options.SetRequestHeader(authHeader, apiKey); } catch { }
                    }

                    _log.LogInformation("[Console] Connecting Moonraker WS: {Uri}", wsUri);
                    await ws.ConnectAsync(wsUri, ct);
                    AddLine(new ConsoleLine { Text = "Connected to Moonraker (WS)", Level = "info", FromLocal = true });

                    // Subscribe to display_status, gcode_response, and console messages
                    var sub = new
                    {
                        jsonrpc = "2.0",
                        method = "printer.objects.subscribe",
                        @params = new { objects = new { display_status = (object?)null, extruder = (object?)null, heater_bed = (object?)null } },
                        id = 1
                    };
                    var subJson = System.Text.Json.JsonSerializer.Serialize(sub);
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(subJson)), WebSocketMessageType.Text, true, ct);

                    // Subscribe to gcode responses (console output)
                    var gcodeNotify = new
                    {
                        jsonrpc = "2.0",
                        method = "server.gcode_store",
                        @params = new { count = 50 },
                        id = 2
                    };
                    var gcodeJson = System.Text.Json.JsonSerializer.Serialize(gcodeNotify);
                    await ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(gcodeJson)), WebSocketMessageType.Text, true, ct);

                    var buffer = new byte[8192];
                    var sb = new StringBuilder();
                    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        var res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                        if (res.MessageType == WebSocketMessageType.Close) break;
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
                        if (res.EndOfMessage)
                        {
                            var msg = sb.ToString();
                            sb.Clear();
                            TryHandleWsMessage(msg);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[Console] WS loop error");
                    AddLine(new ConsoleLine { Text = "Console WS error: " + ex.Message, Level = "warn", FromLocal = true });
                }
                finally
                {
                    try 
                    { 
                        if (ws != null && ws.State == WebSocketState.Open)
                        {
                            // Use a short timeout for closing to prevent hanging on shutdown
                            using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeTimeout.Token);
                        }
                    } 
                    catch { }
                    finally
                    {
                        try { ws?.Dispose(); } catch { }
                    }
                }

                if (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), ct); // backoff
                }
            }
        }

        private void TryHandleWsMessage(string json)
        {
            try
            {
                var root = System.Text.Json.Nodes.JsonNode.Parse(json) as JsonObject;
                if (root == null) return;
                
                // Handle notify_status_update (display_status changes)
                var method = root["method"]?.ToString();
                if (!string.IsNullOrEmpty(method) && method.Equals("notify_status_update", StringComparison.OrdinalIgnoreCase))
                {
                    var arr = root["params"] as JsonArray;
                    if (arr == null || arr.Count == 0) return;
                    var obj = arr[0] as JsonObject;
                    var ds = obj?["display_status"] as JsonObject;
                    var message = ds?["message"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        if (!string.Equals(message, _lastDisplayMessage, StringComparison.Ordinal))
                        {
                            _lastDisplayMessage = message;
                            AddLine(new ConsoleLine { Text = message!, Level = "info", FromLocal = false });
                        }
                    }

                    // Extract temperatures from extruder and heater_bed objects
                    var extruder = obj?["extruder"] as JsonObject;
                    if (extruder != null)
                    {
                        if (extruder.TryGetPropertyValue("temperature", out var tempNode) && tempNode?.GetValueKind() != System.Text.Json.JsonValueKind.Null)
                        {
                            try
                            {
                                lock (_lock)
                                {
                                    _lastToolTemp = tempNode!.GetValue<double>();
                                }
                            }
                            catch { }
                        }
                    }

                    var heaterBed = obj?["heater_bed"] as JsonObject;
                    if (heaterBed != null)
                    {
                        if (heaterBed.TryGetPropertyValue("temperature", out var bedTempNode) && bedTempNode?.GetValueKind() != System.Text.Json.JsonValueKind.Null)
                        {
                            try
                            {
                                lock (_lock)
                                {
                                    _lastBedTemp = bedTempNode!.GetValue<double>();
                                }
                            }
                            catch { }
                        }
                    }
                }
                // Handle notify_gcode_response (console output from gcode commands)
                else if (!string.IsNullOrEmpty(method) && method.Equals("notify_gcode_response", StringComparison.OrdinalIgnoreCase))
                {
                    var arr = root["params"] as JsonArray;
                    if (arr == null || arr.Count == 0) return;
                    var responseText = arr[0]?.ToString();
                    if (!string.IsNullOrWhiteSpace(responseText))
                    {
                        // Parse level from prefix if present (// echo:, // !!, etc)
                        var level = "info";
                        var text = responseText!;
                        if (text.StartsWith("// !!"))
                        {
                            level = "error";
                            text = text.Substring(5).Trim();
                        }
                        else if (text.StartsWith("// Error:") || text.StartsWith("Error:"))
                        {
                            level = "error";
                        }
                        else if (text.StartsWith("// "))
                        {
                            text = text.Substring(3).Trim();
                        }
                        
                        AddLine(new ConsoleLine { Text = text, Level = level, FromLocal = false });
                    }
                }
                // Handle result from gcode_store request (initial batch)
                else if (root["result"] != null && root["id"]?.GetValue<int>() == 2)
                {
                    var result = root["result"] as JsonObject;
                    var gcode_store = result?["gcode_store"] as JsonArray;
                    if (gcode_store != null)
                    {
                        foreach (var item in gcode_store)
                        {
                            var obj = item as JsonObject;
                            var message = obj?["message"]?.ToString();
                            var time = obj?["time"]?.GetValue<double>();
                            var type = obj?["type"]?.ToString();
                            
                            if (!string.IsNullOrWhiteSpace(message))
                            {
                                var level = type?.ToLowerInvariant() switch
                                {
                                    "response" => "info",
                                    "command" => "info",
                                    _ => "info"
                                };
                                
                                var ts = time.HasValue ? DateTimeOffset.FromUnixTimeSeconds((long)time.Value).UtcDateTime : DateTime.UtcNow;
                                AddLine(new ConsoleLine { Text = message, Level = level, FromLocal = false, Timestamp = ts });
                            }
                        }
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Send a single-line G-code command to the printer. Attempts to locate a Moonraker base URL
        /// from configuration and forwards the command via the MoonrakerClient helper. Returns a SendResult
        /// describing the outcome. When the command matches a prefix listed in RequireConfirmation and the
        /// caller did not set confirmed=true, the method returns Ok=false with Message="confirmation-required".
        /// Commands are queued and rate-limited to avoid overwhelming Moonraker.
        /// </summary>
        public async Task<SendResult> SendCommandAsync(string cmd, bool confirmed = false, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return new SendResult { Ok = false, Message = "Empty command" };

            // Simple disallowed-prefix check (config may provide array of prefixes)
            var disallowed = _cfg.GetSection("Stream:Console:DisallowedCommands").Get<string[]>() ?? Array.Empty<string>();
            foreach (var p in disallowed)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (cmd.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                {
                    var m = $"Command blocked by disallowed prefix: {p}";
                    AddLine(new ConsoleLine { Text = m, Level = "warn", FromLocal = true });
                    return new SendResult { Ok = false, Message = m };
                }
            }

            // Confirmation required prefixes
            var requireConf = _cfg.GetSection("Stream:Console:RequireConfirmation").Get<string[]>() ?? Array.Empty<string>();
            foreach (var p in requireConf)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                if (cmd.StartsWith(p, StringComparison.OrdinalIgnoreCase) && !confirmed)
                {
                    var m = $"Confirmation required for: {p}";
                    AddLine(new ConsoleLine { Text = m, Level = "warn", FromLocal = true });
                    return new SendResult { Ok = false, Message = "confirmation-required", SentCommand = cmd };
                }
            }

            // Enqueue the command for processing with rate limiting
            var tcs = new TaskCompletionSource<SendResult>();
            EnqueueCommand(async (innerCt) =>
            {
                try
                {
                    var result = await SendCommandInternalAsync(cmd, innerCt);
                    tcs.SetResult(result);
                    return result;
                }
                catch (Exception ex)
                {
                    var result = new SendResult { Ok = false, Message = ex.Message, SentCommand = cmd };
                    tcs.SetResult(result);
                    return result;
                }
            });

            return await tcs.Task;
        }

        /// <summary>
        /// Internal method that actually sends the command (called from the command queue)
        /// </summary>
        private async Task<SendResult> SendCommandInternalAsync(string cmd, CancellationToken ct)
        {
            // Attempt to resolve Moonraker base URI
            Uri? baseUri = null;
            var cfgBase = _cfg.GetValue<string>("Moonraker:BaseUrl");
            if (!string.IsNullOrWhiteSpace(cfgBase) && Uri.TryCreate(cfgBase, UriKind.Absolute, out var parsed)) baseUri = parsed;
            if (baseUri == null)
            {
                var streamSource = _cfg.GetValue<string>("Stream:Source");
                if (!string.IsNullOrWhiteSpace(streamSource)) baseUri = _moonrakerClient.GetPrinterBaseUriFromStreamSource(streamSource);
            }

            if (baseUri == null)
            {
                var msg = "Moonraker base URL not configured";
                AddLine(new ConsoleLine { Text = msg, Level = "error", FromLocal = true });
                return new SendResult { Ok = false, Message = msg };
            }

            // Try sending using MoonrakerClient helper
            try
            {
                var apiKey = _cfg.GetValue<string>("Moonraker:ApiKey");
                var authHeader = _cfg.GetValue<string>("Moonraker:AuthHeader");
                var resp = await _moonrakerClient.SendGcodeScriptAsync(baseUri, cmd, apiKey, authHeader, ct);
                if (resp == null)
                {
                    var msg = "Moonraker did not accept command (no response)";
                    AddLine(new ConsoleLine { Text = msg, Level = "error", FromLocal = true });
                    return new SendResult { Ok = false, Message = msg, SentCommand = cmd };
                }

                AddLine(new ConsoleLine { Text = $"Sent: {cmd}", Level = "info", FromLocal = true });
                return new SendResult { Ok = true, Message = "Sent", SentCommand = cmd };
            }
            catch (Exception ex)
            {
                var msg = "SendCommandAsync error: " + ex.Message;
                AddLine(new ConsoleLine { Text = msg, Level = "error", FromLocal = true });
                return new SendResult { Ok = false, Message = msg, SentCommand = cmd };
            }
        }

        // Temperature helpers (safe ranges enforced). toolIndex default 0.
        public async Task<SendResult> SetToolTemperatureAsync(int toolIndex, int temperature, CancellationToken ct = default)
        {
            var max = _cfg.GetValue<int?>("Stream:Console:ToolMaxTemp") ?? 350;
            if (temperature < 0 || temperature > max)
            {
                return new SendResult { Ok = false, Message = $"Tool temp out of range (0..{max})" };
            }
            var cmd = toolIndex <= 0 ? $"M104 S{temperature}" : $"M104 T{toolIndex} S{temperature}";
            return await SendCommandAsync(cmd, confirmed: false, ct);
        }

        public async Task<SendResult> SetBedTemperatureAsync(int temperature, CancellationToken ct = default)
        {
            var max = _cfg.GetValue<int?>("Stream:Console:BedMaxTemp") ?? 120;
            if (temperature < 0 || temperature > max)
            {
                return new SendResult { Ok = false, Message = $"Bed temp out of range (0..{max})" };
            }
            return await SendCommandAsync($"M140 S{temperature}", confirmed: false, ct);
        }

        public async Task<SendResult> SetTemperaturesAsync(int? toolTemp, int? bedTemp, int toolIndex = 0, CancellationToken ct = default)
        {
            // Validate ranges
            var toolMax = _cfg.GetValue<int?>("Stream:Console:ToolMaxTemp") ?? 350;
            var bedMax = _cfg.GetValue<int?>("Stream:Console:BedMaxTemp") ?? 120;
            
            if (toolTemp.HasValue && (toolTemp.Value < 0 || toolTemp.Value > toolMax))
            {
                return new SendResult { Ok = false, Message = $"Tool temp out of range (0..{toolMax})" };
            }
            if (bedTemp.HasValue && (bedTemp.Value < 0 || bedTemp.Value > bedMax))
            {
                return new SendResult { Ok = false, Message = $"Bed temp out of range (0..{bedMax})" };
            }

            // Build command lines
            var commands = new List<string>();
            if (toolTemp.HasValue)
            {
                commands.Add(toolIndex <= 0 ? $"M104 S{toolTemp.Value}" : $"M104 T{toolIndex} S{toolTemp.Value}");
            }
            if (bedTemp.HasValue)
            {
                commands.Add($"M140 S{bedTemp.Value}");
            }

            if (commands.Count == 0)
            {
                return new SendResult { Ok = true, Message = "No-op" };
            }

            // If only one command, send it directly
            if (commands.Count == 1)
            {
                return await SendCommandAsync(commands[0], confirmed: false, ct);
            }

            // If both commands, use gcode_script endpoint (Moonraker's proper way to send multi-line scripts)
            // This avoids rate limiting by treating them as a single script request
            try
            {
                var baseUri = new Uri(_cfg.GetValue<string>("Moonraker:BaseUrl") ?? "http://localhost:7125");
                var apiKey = _cfg.GetValue<string>("Moonraker:ApiKey");
                var authHeader = _cfg.GetValue<string>("Moonraker:AuthHeader");
                var script = string.Join("\n", commands);
                
                var result = await _moonrakerClient.SendGcodeScriptAsync(baseUri, script, apiKey, authHeader, ct);
                if (result != null)
                {
                    var msg = $"Temperatures set: Tool {toolTemp}°C, Bed {bedTemp}°C";
                    AddLine(new ConsoleLine { Text = msg, Level = "info", FromLocal = true });
                    return new SendResult { Ok = true, Message = msg, SentCommand = script };
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[Console] Error sending gcode script for temperatures");
            }

            // Fallback: send them separately with a small delay
            var last = await SendCommandAsync(commands[0], confirmed: false, ct);
            if (!last.Ok) return last;
            await Task.Delay(150, ct);
            return await SendCommandAsync(commands[1], confirmed: false, ct);
        }

        /// <summary>
        /// Background task that processes queued commands with rate limiting
        /// </summary>
        private async Task ProcessCommandQueueAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // Wait for a command to be queued
                    await _commandQueueSemaphore.WaitAsync(ct);
                    
                    if (ct.IsCancellationRequested) break;

                    // Get next command from queue
                    Func<CancellationToken, Task<SendResult>>? command = null;
                    lock (_lock)
                    {
                        if (_commandQueue.Count > 0)
                        {
                            command = _commandQueue.Dequeue();
                        }
                    }

                    if (command != null)
                    {
                        // Enforce minimum interval between commands
                        var timeSinceLastSend = DateTime.UtcNow - _lastSent;
                        if (timeSinceLastSend.TotalMilliseconds < _minCommandIntervalMs)
                        {
                            var delay = _minCommandIntervalMs - (int)timeSinceLastSend.TotalMilliseconds;
                            await Task.Delay(delay, ct);
                        }

                        try
                        {
                            await command(ct);
                            _lastSent = DateTime.UtcNow;
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "[Console] Error processing queued command");
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "[Console] Command queue processor error");
            }
        }

        /// <summary>
        /// Enqueue a command to be sent with rate limiting
        /// </summary>
        private void EnqueueCommand(Func<CancellationToken, Task<SendResult>> commandFunc)
        {
            lock (_lock)
            {
                _commandQueue.Enqueue(commandFunc);
            }
            _commandQueueSemaphore.Release();
        }
    }
}
