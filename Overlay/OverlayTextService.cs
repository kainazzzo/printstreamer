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
    private readonly string _textFileDir;
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
           "Nozzle: {nozzle:0}°C/{nozzleTarget:0}°C | Bed: {bed:0}°C/{bedTarget:0}°C | Layer {layers} | {progress:0}%\nSpd:{speed}mm/s | Flow:{flow} | Fil:{filament}m | ETA:{eta:hh:mm tt}";

        var refreshMs = config.GetValue<int?>("Overlay:RefreshMs") ?? 1000;
        if (refreshMs < 200) refreshMs = 200;
        _interval = TimeSpan.FromMilliseconds(refreshMs);

    // Fix the path to a stable absolute location based on construction-time CWD
    _textFileDir = Path.Combine(Directory.GetCurrentDirectory(), "overlay");
    Directory.CreateDirectory(_textFileDir);
    _textFilePath = Path.Combine(_textFileDir, "overlay.txt");
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
        "&display_status=progress,flow,speed,volumetric_flow&virtual_sdcard=progress,file_position,print_duration" +
        "&gcode_move=speed,speed_factor,extrude_factor&motion_report";
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
        
        // Determine if there's an active print job (state is "printing" or "paused")
        bool isPrinting = state.Equals("printing", StringComparison.OrdinalIgnoreCase);
        bool isPaused = state.Equals("paused", StringComparison.OrdinalIgnoreCase);
        bool isActiveJob = isPrinting || isPaused;
        
        // Try to get layer info for more accurate progress (only if actively printing)
        int? currentLayer = null;
        int? totalLayers = null;
        if (isActiveJob && print.ValueKind != JsonValueKind.Undefined && print.TryGetProperty("info", out var infoElem))
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
            // Get volumetric_flow if available (preferred)
            var vflow = TryDouble(displayStatus, "volumetric_flow");
            if (!double.IsNaN(vflow) && vflow > 0) flowVolume = vflow;
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
        double? extrudeFactor = null;
        
        if (gcodeMove.ValueKind != JsonValueKind.Undefined)
        {
            var spd = TryDouble(gcodeMove, "speed");
            // Empirically: reported speed aligns with mm/min; convert to mm/s
            if (!double.IsNaN(spd) && spd > 0) speedMmS = spd / 60.0;
            
            var spdFactor = TryDouble(gcodeMove, "speed_factor");
            if (!double.IsNaN(spdFactor)) speedFactor = spdFactor * 100.0; // Convert to percentage
            
            var extFactor = TryDouble(gcodeMove, "extrude_factor");
            if (!double.IsNaN(extFactor)) extrudeFactor = extFactor;
        }
        // Fallback: try display_status.speed if gcode_move.speed missing
        if ((!speedMmS.HasValue || speedMmS.Value <= 0) && displayStatus.ValueKind != JsonValueKind.Undefined)
        {
            var dspSpd = TryDouble(displayStatus, "speed");
            // Convert fallback speed to mm/s as well
            if (!double.IsNaN(dspSpd) && dspSpd > 0) speedMmS = dspSpd / 60.0;
        }
        
        // Get motion_report for live toolhead velocity (not historical speed from gcode_move)
        var motionReport = status.TryGetProperty("motion_report", out var mr) ? mr : default;
        double? extruderVelocity = null;
        double? toolheadVelocity = null;
        if (motionReport.ValueKind != JsonValueKind.Undefined)
        {
            var extVel = TryDouble(motionReport, "live_extruder_velocity");
            if (!double.IsNaN(extVel)) extruderVelocity = extVel;
            
            // Get actual live toolhead velocity (live_velocity in mm/s)
            var thVel = TryDouble(motionReport, "live_velocity");
            if (!double.IsNaN(thVel) && thVel >= 0) toolheadVelocity = thVel;
        }

        // Use display_status.progress as primary source (already set from display_status or virtual_sdcard above)
        // Only show progress when there's an active job
        int progress = isActiveJob ? (int)Math.Round(progress01 * 100) : 0;
        string filename = TryString(print, "filename") ?? string.Empty;

        // Get print duration and filament used for ETA calculation (only if actively printing)
        double printDuration = isActiveJob ? TryDouble(print, "print_duration") : double.NaN;
        double filamentUsed = isActiveJob ? TryDouble(print, "filament_used") : double.NaN;

        // Calculate ETA if progress is meaningful and job is active
        DateTime? eta = null;
        if (isActiveJob && progress01 > 0.01 && !double.IsNaN(printDuration) && printDuration > 0)
        {
            var estimatedTotal = printDuration / progress01;
            var remaining = estimatedTotal - printDuration;
            eta = DateTime.Now.AddSeconds(remaining);
        }

        // If we have a timelapse metadata provider and a filename, prefer its cached totals/slicer
        // Only use this when there's an active job
        string? providerSlicer = null;
        double? layerHeight = null;
        double? extrusionWidth = null;
        if (isActiveJob && !string.IsNullOrWhiteSpace(filename) && _tlProvider != null)
        {
            try
            {
                var meta = _tlProvider.GetMetadataForFilename(filename);
                if (meta != null)
                {
                    if (meta.TotalLayersFromMetadata.HasValue)
                        totalLayers = meta.TotalLayersFromMetadata.Value;
                    providerSlicer = meta.Slicer;
                    layerHeight = meta.LayerHeight;
                    extrusionWidth = meta.ExtrusionWidth;
                }
            }
            catch { }
        }
        
        // Calculate volumetric flow if Klipper doesn't provide it
        // Formula: volumetric_flow (mm³/s) = extruder_velocity (mm/s) × π × (filament_diameter/2)²
        // Only calculate when there's an active job
        if (isActiveJob && !flowVolume.HasValue && extruderVelocity.HasValue && extruderVelocity.Value > 0.01)
        {
            // Use 1.75mm as default filament diameter (most common)
            // Could be made configurable if needed
            var filDiam = 1.75;
            var radius = filDiam / 2.0;
            var crossSectionArea = Math.PI * radius * radius;
            
            // Apply extrude_factor if available (flow rate multiplier from M221)
            var flowMultiplier = extrudeFactor ?? 1.0;
            flowVolume = extruderVelocity.Value * crossSectionArea * flowMultiplier;
        }
        
        // Clear flow if no active job
        if (!isActiveJob)
        {
            flowVolume = null;
        }

        // For speed, use live toolhead velocity from motion_report when available
        // Only show speed when there's an active print job AND toolhead is actually moving
        double? displaySpeed = null;
        if (isActiveJob && toolheadVelocity.HasValue && toolheadVelocity.Value > 0.1)
        {
            // Use actual live toolhead velocity (already in mm/s)
            displaySpeed = toolheadVelocity.Value;
        }
        else if (isActiveJob)
        {
            // Job is active but toolhead not moving - show 0
            displaySpeed = 0.0;
        }
        // If no active job, displaySpeed stays null and will show as "-"

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
            Speed = displaySpeed, // Use live toolhead velocity (only when job is active)
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
    // Template contains units (e.g. "mm/s"); only insert the numeric value here to avoid duplicating units.
    s = ReplaceStr(s, "speed", d.Speed.HasValue ? d.Speed.Value.ToString("0") : "0");
    s = ReplaceStr(s, "speedfactor", d.SpeedFactor.HasValue ? d.SpeedFactor.Value.ToString("0") + "%" : "-");
    // Flow is volumetric (mm^3/s) from Moonraker display_status.flow
    s = ReplaceStr(s, "flow", d.Flow.HasValue ? d.Flow.Value.ToString("0.0") + " mm^3/s" : "0 mm^3/s");
    s = ReplaceStr(s, "filament", d.Filament.HasValue && !double.IsNaN(d.Filament.Value) ? (d.Filament.Value / 1000.0).ToString("0.00") + "m" : "0.00m");
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

    private async Task SafeWriteAsync(string content, CancellationToken ct)
    {
        // Always write to the same absolute file path computed at construction time
        try
        {
            Directory.CreateDirectory(_textFileDir);
        }
        catch { }

        var path = _textFilePath;
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
