using System.Text;
using System.Text.Json;

namespace PrintStreamer.Overlay;

/// <summary>
/// Periodically queries Moonraker for printer stats and writes a formatted text file
/// that ffmpeg drawtext=textfile=...:reload=1 can read to overlay live information.
/// </summary>
public sealed class OverlayTextService : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _moonrakerBase;
    private readonly string? _apiKey;
    private readonly string? _authHeader;
    private readonly string _template;
    private readonly TimeSpan _interval;
    private readonly string _textFilePath;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;
    // Optional provider to get cached timelapse metadata (filename -> session data)
    private readonly ITimelapseMetadataProvider? _tlProvider;

    public string TextFilePath => _textFilePath;

    public OverlayTextService(IConfiguration config, ITimelapseMetadataProvider? timelapseProvider = null)
    {
        _tlProvider = timelapseProvider;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };

        _moonrakerBase = (config.GetValue<string>("Moonraker:BaseUrl") ?? "http://localhost:7125").TrimEnd('/');
        _apiKey = config.GetValue<string>("Moonraker:ApiKey");
        _authHeader = config.GetValue<string>("Moonraker:AuthHeader");

    _template = config.GetValue<string>("Overlay:Template") ??
           "Nozzle: {nozzle:0}°C/{nozzleTarget:0}°C | Bed: {bed:0}°C/{bedTarget:0}°C | Layer {layers} \n{progress:0}% | Spd:{speed}mm/s | Flow:{flow} | Fil:{filament}m | ETA:{eta:hh:mm tt}";

        var refreshMs = config.GetValue<int?>("Overlay:RefreshMs") ?? 1000;
        if (refreshMs < 200) refreshMs = 200;
        _interval = TimeSpan.FromMilliseconds(refreshMs);

        var workDir = Path.Combine(Directory.GetCurrentDirectory(), "overlay");
        Directory.CreateDirectory(workDir);
        _textFilePath = Path.Combine(workDir, "overlay.txt");
    }

    public void Start()
    {
        if (_loopTask != null) return;
        _loopTask = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // Ensure file exists with placeholder
        try { await SafeWriteAsync(Render(new OverlayData()), ct); } catch { }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var data = await QueryAsync(ct);
                var text = Render(data);
                await SafeWriteAsync(text, ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"[Overlay] Error: {ex.Message}");
            }

            try { await Task.Delay(_interval, ct); } catch { }
        }
    }

    private async Task<OverlayData> QueryAsync(CancellationToken ct)
    {
        // Query Moonraker for temps, print status, and time estimates
    var url = _moonrakerBase + "/printer/objects/query" +
        "?extruder=temperature,target&heater_bed=temperature,target&print_stats=state,filename,info,print_duration,filament_used,total_duration" +
        "&display_status=progress,flow,speed&virtual_sdcard=progress,file_position,print_duration" +
        "&gcode_move=speed,speed_factor,extrude_factor";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            var header = string.IsNullOrWhiteSpace(_authHeader) ? "X-Api-Key" : _authHeader!;
            var parts = header.Split(':', 2);
            if (parts.Length == 2)
                req.Headers.TryAddWithoutValidation(parts[0].Trim(), parts[1].Trim());
            else
                req.Headers.TryAddWithoutValidation(header, _apiKey);
        }

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var root = doc.RootElement;
        var status = root.GetProperty("result").GetProperty("status");
        var extruder = status.TryGetProperty("extruder", out var ex) ? ex : default;
        var bed = status.TryGetProperty("heater_bed", out var hb) ? hb : default;
        var print = status.TryGetProperty("print_stats", out var ps) ? ps : default;

    double nozzle = TryDouble(extruder, "temperature");
        double nozzleTarget = TryDouble(extruder, "target");
        double bedTemp = TryDouble(bed, "temperature");
        double bedTarget = TryDouble(bed, "target");

        string state = TryString(print, "state") ?? string.Empty;
        // Try to get layer info for more accurate progress
        int? currentLayer = null;
        int? totalLayers = null;
        if (print.ValueKind != JsonValueKind.Undefined && print.TryGetProperty("info", out var infoElem))
        {
            if (infoElem.TryGetProperty("current_layer", out var clElem) && clElem.ValueKind == JsonValueKind.Number && clElem.TryGetInt32(out var cl))
                currentLayer = cl;
            if (infoElem.TryGetProperty("total_layer", out var tlElem) && tlElem.ValueKind == JsonValueKind.Number && tlElem.TryGetInt32(out var tl))
                totalLayers = tl;
        }

        // Get progress from display_status (0-1 range) and volumetric flow (mm^3/s)
        var displayStatus = status.TryGetProperty("display_status", out var ds) ? ds : default;
        double progress01 = TryDouble(displayStatus, "progress");
        double? flowVolume = null; // mm^3/s from Moonraker
        if (displayStatus.ValueKind != JsonValueKind.Undefined)
        {
            // Prefer explicit volumetric flow if available; otherwise accept 'flow'
            var vflow = TryDouble(displayStatus, "volumetric_flow");
            if (!double.IsNaN(vflow)) flowVolume = vflow;
            else
            {
                var flowVal = TryDouble(displayStatus, "flow");
                if (!double.IsNaN(flowVal)) flowVolume = flowVal;
            }
        }
        
        // Get virtual_sdcard for more reliable progress
        var vsd = status.TryGetProperty("virtual_sdcard", out var vsdElem) ? vsdElem : default;
        if (vsd.ValueKind != JsonValueKind.Undefined)
        {
            var vsdProgress = TryDouble(vsd, "progress");
            if (!double.IsNaN(vsdProgress) && vsdProgress > 0)
            {
                progress01 = vsdProgress;
            }
        }

        // Get actual speed in mm/s and factors from gcode_move
        var gcodeMove = status.TryGetProperty("gcode_move", out var gm) ? gm : default;
        double? speedMmS = null;
    double? speedFactor = null;
        
        if (gcodeMove.ValueKind != JsonValueKind.Undefined)
        {
            var spd = TryDouble(gcodeMove, "speed");
            // Empirically: reported speed aligns with mm/min; convert to mm/s
            if (!double.IsNaN(spd) && spd > 0) speedMmS = spd / 60.0;
            
            var spdFactor = TryDouble(gcodeMove, "speed_factor");
            if (!double.IsNaN(spdFactor)) speedFactor = spdFactor * 100.0; // Convert to percentage
            
            // extrude_factor is a percentage factor; we no longer use it for display
            // keep it available via SpeedFactor if needed elsewhere
        }
        // Fallback: try display_status.speed if gcode_move.speed missing
        if ((!speedMmS.HasValue || speedMmS.Value <= 0) && displayStatus.ValueKind != JsonValueKind.Undefined)
        {
            var dspSpd = TryDouble(displayStatus, "speed");
            // Convert fallback speed to mm/s as well
            if (!double.IsNaN(dspSpd) && dspSpd > 0) speedMmS = dspSpd / 60.0;
        }

        int progress = 0;
        if (currentLayer.HasValue && totalLayers.HasValue && totalLayers.Value > 0)
        {
            // Prefer layer-based progress when available
            progress = (int)Math.Round((double)currentLayer.Value / totalLayers.Value * 100.0);
        }
        else
        {
            progress = (int)Math.Round(progress01 * 100);
        }
        string filename = TryString(print, "filename") ?? string.Empty;

        // Get print duration and filament used for ETA calculation
        double printDuration = TryDouble(print, "print_duration");
        double filamentUsed = TryDouble(print, "filament_used");

        // Calculate ETA if progress is meaningful
        DateTime? eta = null;
        if (progress01 > 0.01 && !double.IsNaN(printDuration) && printDuration > 0)
        {
            var estimatedTotal = printDuration / progress01;
            var remaining = estimatedTotal - printDuration;
            eta = DateTime.Now.AddSeconds(remaining);
        }

        // If we have a timelapse metadata provider and a filename, prefer its cached totals/slicer
        string? providerSlicer = null;
        if (!string.IsNullOrWhiteSpace(filename) && _tlProvider != null)
        {
            try
            {
                var meta = _tlProvider.GetMetadataForFilename(filename);
                if (meta != null)
                {
                    if (meta.TotalLayersFromGcode.HasValue)
                        totalLayers = meta.TotalLayersFromGcode.Value;
                    else if (meta.TotalLayersFromMetadata.HasValue)
                        totalLayers = meta.TotalLayersFromMetadata.Value;
                    providerSlicer = meta.Slicer;
                }
            }
            catch { }
        }

        return new OverlayData
        {
            Nozzle = nozzle,
            NozzleTarget = nozzleTarget,
            Bed = bedTemp,
            BedTarget = bedTarget,
            State = state,
            Progress = progress,
            Layer = currentLayer,
            LayerMax = totalLayers,
            Time = DateTime.Now,
            Filename = filename,
            Speed = speedMmS,
            SpeedFactor = speedFactor,
            Flow = flowVolume,
            Filament = filamentUsed,
            Slicer = providerSlicer,
            ETA = eta
        };

        static double TryDouble(JsonElement elem, string name, double defaultValue = double.NaN)
        {
            if (elem.ValueKind != JsonValueKind.Undefined && elem.TryGetProperty(name, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
                if (double.TryParse(v.ToString(), out var d2)) return d2;
            }
            return defaultValue;
        }
        static string? TryString(JsonElement elem, string name)
        {
            if (elem.ValueKind != JsonValueKind.Undefined && elem.TryGetProperty(name, out var v))
            {
                return v.ToString();
            }
            return null;
        }
    }

    private string Render(OverlayData d)
    {
        var s = _template;

        string ReplaceNum(string input, string name, double value, string defaultFmt = "0.0")
        {
            return System.Text.RegularExpressions.Regex.Replace(
                input,
                @"\{" + System.Text.RegularExpressions.Regex.Escape(name) + @"(?::([^}]+))?\}",
                m => double.IsNaN(value)
                        ? "—"
                        : value.ToString(m.Groups[1].Success ? m.Groups[1].Value : defaultFmt),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
        }

        string ReplaceInt(string input, string name, int value, string defaultFmt = "0")
        {
            return System.Text.RegularExpressions.Regex.Replace(
                input,
                @"\{" + System.Text.RegularExpressions.Regex.Escape(name) + @"(?::([^}]+))?\}",
                m => value.ToString(m.Groups[1].Success ? m.Groups[1].Value : defaultFmt),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
        }

        string ReplaceNullableInt(string input, string name, int? value, string defaultFmt = "0")
        {
            return System.Text.RegularExpressions.Regex.Replace(
                input,
                @"\{" + System.Text.RegularExpressions.Regex.Escape(name) + @"(?::([^}]+))?\}",
                m => value.HasValue ? value.Value.ToString(m.Groups[1].Success ? m.Groups[1].Value : defaultFmt) : "-",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
        }

        string ReplaceDate(string input, string name, DateTime value)
        {
            return System.Text.RegularExpressions.Regex.Replace(
                input,
                @"\{" + System.Text.RegularExpressions.Regex.Escape(name) + @"(?::([^}]+))?\}",
                m => value.ToString(m.Groups[1].Success ? m.Groups[1].Value : "HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
        }

        string ReplaceStr(string input, string name, string value)
        {
            return System.Text.RegularExpressions.Regex.Replace(
                input,
                @"\{" + System.Text.RegularExpressions.Regex.Escape(name) + @"(?::[^}]+)?\}",
                _ => value ?? string.Empty,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
        }

        s = ReplaceNum(s, "nozzle", d.Nozzle);
        s = ReplaceNum(s, "nozzleTarget", d.NozzleTarget);
        s = ReplaceNum(s, "bed", d.Bed);
        s = ReplaceNum(s, "bedTarget", d.BedTarget);
        s = ReplaceInt(s, "progress", d.Progress);
        // Layer replacements (use '-' when unknown)
        s = ReplaceNullableInt(s, "layer", d.Layer);
        s = ReplaceNullableInt(s, "layermax", d.LayerMax);
        // {layers} -> "current/total" with '-' when unknown
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\{layers\}", m =>
        {
            var left = d.Layer.HasValue ? d.Layer.Value.ToString() : "-";
            var right = d.LayerMax.HasValue ? d.LayerMax.Value.ToString() : "-";
            return $"{left}/{right}";
        }, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = ReplaceDate(s, "time", d.Time);
        s = ReplaceStr(s, "state", d.State ?? string.Empty);
        s = ReplaceStr(s, "filename", d.Filename ?? string.Empty);
    s = ReplaceStr(s, "slicer", d.Slicer ?? string.Empty);
    s = ReplaceStr(s, "speed", d.Speed.HasValue ? d.Speed.Value.ToString("0") : "-");
    s = ReplaceStr(s, "speedfactor", d.SpeedFactor.HasValue ? d.SpeedFactor.Value.ToString("0") + "%" : "-");
    // Flow is volumetric (mm^3/s) from Moonraker display_status.flow
    s = ReplaceStr(s, "flow", d.Flow.HasValue ? d.Flow.Value.ToString("0.0") + " mm^3/s" : "-");
    s = ReplaceStr(s, "filament", d.Filament.HasValue ? (d.Filament.Value / 1000.0).ToString("0.00") : "-");
        // ETA with time format if available
        if (d.ETA.HasValue)
        {
            s = ReplaceDate(s, "eta", d.ETA.Value);
        }
        else
        {
            s = ReplaceStr(s, "eta", "-");
        }

        // If the template didn't ask for layers explicitly, append them so they're always visible
    if (!System.Text.RegularExpressions.Regex.IsMatch(_template, @"\{(?:layer|layermax|layers)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var left = d.Layer.HasValue ? d.Layer.Value.ToString() : "-";
            var right = d.LayerMax.HasValue ? d.LayerMax.Value.ToString() : "-";
            s = s + $"  |  Layers: {left}/{right}";
        }

        // Escape backslashes and colons minimally for drawtext textfile content safety
        // Newlines are supported; keep as-is
        return s.Replace("\r", string.Empty);
    }

    private static async Task SafeWriteAsync(string content, CancellationToken ct)
    {
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "overlay");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "overlay.txt");
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content, Encoding.UTF8, ct);
        File.Move(tmp, path, overwrite: true);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _loopTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _http.Dispose();
    }

    private sealed class OverlayData
    {
        public double Nozzle { get; init; }
        public double NozzleTarget { get; init; }
        public double Bed { get; init; }
        public double BedTarget { get; init; }
        public string? State { get; init; }
        public int Progress { get; init; }
        public int? Layer { get; init; }
        public int? LayerMax { get; init; }
        public DateTime Time { get; init; }
        public string? Filename { get; init; }
        public double? Speed { get; init; } // Speed in mm/s
        public double? SpeedFactor { get; init; } // Speed factor percentage
        public double? Flow { get; init; } // Flow/extrude factor percentage
        public double? Filament { get; init; } // Filament used in mm
        public string? Slicer { get; init; }
        public DateTime? ETA { get; init; }
    }
}
