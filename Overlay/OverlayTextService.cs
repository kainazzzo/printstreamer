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
           "Nozzle: {nozzle:0.0}°C/{nozzleTarget:0.0}°C  |  Bed: {bed:0.0}°C/{bedTarget:0.0}°C  |  {state}  |  {progress:0}%  |  {time:HH:mm:ss}  |  {layers}  |  Speed: {speed}  |  Flow: {flow}  |  Filament: {filament}  |  Slicer: {slicer}";

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
        // Minimal, fast Moonraker query for temps and print status
    var url = _moonrakerBase + "/printer/objects/query" +
          "?extruder=temperature,target&heater_bed=temperature,target&print_stats=state,progress,filename,info" +
          "&display_status=speed,flow,filament";
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

        // Additional display_status fields (speed, flow, filament) may be in display_status
        double? speed = null;
        double? flow = null;
        string? filament = null;
        if (root.GetProperty("result").TryGetProperty("status", out var statusElem) && statusElem.ValueKind == JsonValueKind.Object)
        {
            if (statusElem.TryGetProperty("display_status", out var ds) && ds.ValueKind == JsonValueKind.Object)
            {
                if (ds.TryGetProperty("speed", out var sp) && sp.ValueKind == JsonValueKind.Number && sp.TryGetDouble(out var spd)) speed = spd;
                if (ds.TryGetProperty("flow", out var fl) && fl.ValueKind == JsonValueKind.Number && fl.TryGetDouble(out var fld)) flow = fld;
                if (ds.TryGetProperty("filament", out var fil) && fil.ValueKind == JsonValueKind.String) filament = fil.GetString();
            }
        }

        // Determine progress. Moonraker often exposes progress as either a number or an object:
        // - progress: 0.523 (number between 0..1)
        // - progress: { completion: 0.523 }
        double progress01 = 0.0;
        if (print.ValueKind != JsonValueKind.Undefined && print.TryGetProperty("progress", out var progElem))
        {
            if (progElem.ValueKind == JsonValueKind.Number && progElem.TryGetDouble(out var pnum))
            {
                progress01 = pnum;
            }
            else if (progElem.ValueKind == JsonValueKind.Object)
            {
                if (progElem.TryGetProperty("completion", out var comp) && comp.ValueKind == JsonValueKind.Number && comp.TryGetDouble(out var cnum))
                {
                    progress01 = cnum;
                }
            }
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
            Speed = speed,
            Flow = flow,
            Filament = filament,
            Slicer = providerSlicer
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
                m => value.ToString(m.Groups[1].Success ? m.Groups[1].Value : "HH:mm:ss"),
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
        }

        string ReplaceStr(string input, string name, string value)
        {
            return System.Text.RegularExpressions.Regex.Replace(
                input,
                @"\{" + System.Text.RegularExpressions.Regex.Escape(name) + @"\}",
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
    s = ReplaceStr(s, "flow", d.Flow.HasValue ? d.Flow.Value.ToString("0.0") : "-");
    s = ReplaceStr(s, "filament", d.Filament ?? "-");

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
        public double? Speed { get; init; }
        public double? Flow { get; init; }
        public string? Filament { get; init; }
        public string? Slicer { get; init; }
    }
}
