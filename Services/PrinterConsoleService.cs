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
        private readonly List<ConsoleLine> _buffer = new List<ConsoleLine>();
        private readonly object _lock = new object();
        private DateTime _lastSent = DateTime.MinValue;
        private CancellationTokenSource? _cts;
        private Task? _runner;
        private string? _lastDisplayMessage;

        // Public event for new lines (simple Action-based subscription)
        public event Action<ConsoleLine>? OnNewLine;

        public PrinterConsoleService(IConfiguration cfg, ILogger<PrinterConsoleService> log)
        {
            _cfg = cfg;
            _log = log;
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
                        @params = new { objects = new { display_status = (object?)null, gcode_move = (object?)null } },
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
                    try { if (ws != null && ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
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

            // Rate limiting (simple cooldown based on CommandsPerMinute)
            var cpm = _cfg.GetValue<int?>("Stream:Console:RateLimit:CommandsPerMinute") ?? 0;
            if (cpm > 0)
            {
                var minInterval = TimeSpan.FromSeconds(60.0 / cpm);
                var now = DateTime.UtcNow;
                if (now - _lastSent < minInterval)
                {
                    var wait = (minInterval - (now - _lastSent)).TotalSeconds;
                    var msg = $"Rate limited: try again in {wait:F1}s";
                    AddLine(new ConsoleLine { Text = msg, Level = "warn", FromLocal = true });
                    return new SendResult { Ok = false, Message = msg };
                }
                _lastSent = now;
            }

            // Attempt to resolve Moonraker base URI
            Uri? baseUri = null;
            var cfgBase = _cfg.GetValue<string>("Moonraker:BaseUrl");
            if (!string.IsNullOrWhiteSpace(cfgBase) && Uri.TryCreate(cfgBase, UriKind.Absolute, out var parsed)) baseUri = parsed;
            if (baseUri == null)
            {
                var streamSource = _cfg.GetValue<string>("Stream:Source");
                if (!string.IsNullOrWhiteSpace(streamSource)) baseUri = MoonrakerClient.GetPrinterBaseUriFromStreamSource(streamSource);
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
                var resp = await MoonrakerClient.SendGcodeScriptAsync(baseUri, cmd, apiKey, authHeader, ct);
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
            SendResult? last = null;
            if (toolTemp.HasValue)
            {
                last = await SetToolTemperatureAsync(toolIndex, toolTemp.Value, ct);
                if (!last.Ok) return last;
            }
            if (bedTemp.HasValue)
            {
                last = await SetBedTemperatureAsync(bedTemp.Value, ct);
            }
            return last ?? new SendResult { Ok = true, Message = "No-op" };
        }
    }
}
