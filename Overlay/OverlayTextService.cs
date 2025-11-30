using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using PrintStreamer.Services;
using Microsoft.Extensions.Logging;

namespace PrintStreamer.Overlay;

/// <summary>
/// Periodically queries Moonraker for printer stats and writes a formatted text file
/// that ffmpeg drawtext=textfile=...:reload=1 can read to overlay live information.
/// </summary>
public sealed class OverlayTextService : IDisposable
{
    private readonly HttpClient _http;
    private readonly MoonrakerClient _moonrakerClient;
    private readonly string _moonrakerBase;
    private readonly string? _apiKey;
    private readonly string? _authHeader;
    private readonly string _template;
    private readonly int _padSpeedWidth;
    private readonly int _padFlowWidth;
    private readonly TimeSpan _interval;
    private readonly string _textFilePath;
    private readonly string _textFileDir;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loopTask;
    private readonly ITimelapseMetadataProvider? _tlProvider;
    private readonly Func<string?>? _audioProvider;
    private readonly ILogger<OverlayTextService> _logger;
    private readonly Dictionary<string, (DateTime fetchedAt, FilamentMeta meta)> _filamentCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _filamentCacheSeconds;
    private readonly bool _showFilamentInOverlay;
    private readonly TimeZoneInfo _displayTimeZone;

    public string TextFilePath => _textFilePath;

    public OverlayTextService(IConfiguration config, ITimelapseMetadataProvider? timelapseProvider, AudioService audioService, ILogger<OverlayTextService> logger, MoonrakerClient moonrakerClient)
    {
        _tlProvider = timelapseProvider;
        _audioProvider = () => audioService.Current;
        _logger = logger;
        _moonrakerClient = moonrakerClient;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };

        _moonrakerBase = (config.GetValue<string>("Moonraker:BaseUrl") ?? "http://localhost:7125").TrimEnd('/');
        _apiKey = config.GetValue<string>("Moonraker:ApiKey");
        _authHeader = config.GetValue<string>("Moonraker:AuthHeader");

        // Configure display timezone (optional). Fall back to system local on error.
        var tzName = config.GetValue<string>("Overlay:Timezone");
        TimeZoneInfo tz = TimeZoneInfo.Local;
        if (!string.IsNullOrWhiteSpace(tzName))
        {
            try
            {
                tz = TimeZoneInfo.FindSystemTimeZoneById(tzName);
            }
            catch (Exception ex)
            {
                try { _logger.LogWarning(ex, "[Overlay] Failed to find timezone '{Tz}', falling back to system local", tzName); } catch { }
                tz = TimeZoneInfo.Local;
            }
        }
        _displayTimeZone = tz;

        _template = config.GetValue<string>("Overlay:Template") ??
            "Nozzle: {nozzle:0}°C/{nozzleTarget:0}°C | Bed: {bed:0}°C/{bedTarget:0}°C | Layer {layers} | {progress:0}%\nSpeed:{speed}mm/s | Flow:{flow} | Fil:{filament}m | ETA:{eta:hh:mm tt}";

        _padSpeedWidth = config.GetValue<int?>("Overlay:PadSpeedWidth") ?? 3;
        _padFlowWidth = config.GetValue<int?>("Overlay:PadFlowWidth") ?? 5;

        var refreshMs = config.GetValue<int?>("Overlay:RefreshMs") ?? 1000;
        if (refreshMs < 200) refreshMs = 200;
        _interval = TimeSpan.FromMilliseconds(refreshMs);

        _filamentCacheSeconds = config.GetValue<int?>("Overlay:FilamentCacheSeconds") ?? 60;
        _showFilamentInOverlay = config.GetValue<bool?>("Overlay:ShowFilamentInOverlay") ?? true;

        // Choose writable directory for overlay file
        string[] candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "overlay"),
            Path.Combine(Directory.GetCurrentDirectory(), "overlay"),
            Path.Combine(Path.GetTempPath(), "printstreamer", "overlay")
        };

        _textFileDir = candidates[0];
        foreach (var dir in candidates)
        {
            try
            {
                Directory.CreateDirectory(dir);
                _textFileDir = dir;
                break;
            }
            catch
            {
                // try next candidate
            }
        }

        _textFilePath = Path.Combine(_textFileDir, "overlay.txt");
        try { _logger.LogInformation("[Overlay] Text file path: {Path}", _textFilePath); } catch { }
    }

    public void Start()
    {
        if (_loopTask != null) return;
        _loopTask = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // Initial placeholder: use NaN for numeric fields so ReplaceNum shows em-dash
        try
        {
            var placeholder = new OverlayData
            {
                Nozzle = double.NaN,
                NozzleTarget = double.NaN,
                Bed = double.NaN,
                BedTarget = double.NaN,
                Filament = double.NaN,
                Time = DateTime.UtcNow,
                Progress = 0
            };
            await SafeWriteAsync(Render(placeholder), ct);
        }
        catch { }

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
                _logger.LogError(ex, "[Overlay] Error while updating overlay text");
            }

            try { await Task.Delay(_interval, ct); } catch { }
        }
    }

    private async Task<OverlayData> QueryAsync(CancellationToken ct)
    {
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

        bool isPrinting = state.Equals("printing", StringComparison.OrdinalIgnoreCase);
        bool isPaused = state.Equals("paused", StringComparison.OrdinalIgnoreCase);
        bool isActiveJob = isPrinting || isPaused;

        int? currentLayer = null;
        int? totalLayers = null;
        string? filamentType = null;
        string? filamentBrand = null;
        string? filamentColor = null;
        string? filamentName = null;
        double? filamentUsedMm = null;
        double? filamentTotalMm = null;
        if (isActiveJob && print.ValueKind != JsonValueKind.Undefined && print.TryGetProperty("info", out var infoElem))
        {
            if (infoElem.TryGetProperty("current_layer", out var clElem) && clElem.ValueKind == JsonValueKind.Number && clElem.TryGetInt32(out var cl))
                currentLayer = cl;
            if (infoElem.TryGetProperty("total_layer", out var tlElem) && tlElem.ValueKind == JsonValueKind.Number && tlElem.TryGetInt32(out var tl))
                totalLayers = tl;

            if (infoElem.TryGetProperty("filament_type", out var ft) && ft.ValueKind != JsonValueKind.Undefined)
                filamentType = ft.ToString();
            else if (infoElem.TryGetProperty("FILAMENT_TYPE", out var ftU) && ftU.ValueKind != JsonValueKind.Undefined)
                filamentType = ftU.ToString();

            if (infoElem.TryGetProperty("filament_brand", out var fb) && fb.ValueKind != JsonValueKind.Undefined)
                filamentBrand = fb.ToString();
            else if (infoElem.TryGetProperty("FILAMENT_BRAND", out var fbU) && fbU.ValueKind != JsonValueKind.Undefined)
                filamentBrand = fbU.ToString();

            if (infoElem.TryGetProperty("filament_color", out var fc) && fc.ValueKind != JsonValueKind.Undefined)
                filamentColor = fc.ToString();
            else if (infoElem.TryGetProperty("FILAMENT_COLOR", out var fcU) && fcU.ValueKind != JsonValueKind.Undefined)
                filamentColor = fcU.ToString();

            if (infoElem.TryGetProperty("filament_used_mm", out var fum) && fum.ValueKind == JsonValueKind.Number && fum.TryGetDouble(out var usedMm))
                filamentUsedMm = usedMm;
            else if (infoElem.TryGetProperty("FILAMENT_USED_MM", out var fumU) && fumU.ValueKind == JsonValueKind.Number && fumU.TryGetDouble(out var usedMmU))
                filamentUsedMm = usedMmU;

            if (infoElem.TryGetProperty("filament_total_mm", out var ftm) && ftm.ValueKind == JsonValueKind.Number && ftm.TryGetDouble(out var totalMm))
                filamentTotalMm = totalMm;
            else if (infoElem.TryGetProperty("FILAMENT_TOTAL_MM", out var ftmU) && ftmU.ValueKind == JsonValueKind.Number && ftmU.TryGetDouble(out var totalMmU))
                filamentTotalMm = totalMmU;
        }

        var displayStatus = status.TryGetProperty("display_status", out var ds) ? ds : default;
        double progress01 = TryDouble(displayStatus, "progress");
        double? flowVolume = null;
        if (displayStatus.ValueKind != JsonValueKind.Undefined)
        {
            var vflow = TryDouble(displayStatus, "volumetric_flow");
            if (!double.IsNaN(vflow) && vflow > 0) flowVolume = vflow;
        }

        var vsd = status.TryGetProperty("virtual_sdcard", out var vsdElem) ? vsdElem : default;
        if (vsd.ValueKind != JsonValueKind.Undefined)
        {
            var vsdProgress = TryDouble(vsd, "progress");
            if (!double.IsNaN(vsdProgress) && vsdProgress > 0)
            {
                progress01 = vsdProgress;
            }
        }

        var gcodeMove = status.TryGetProperty("gcode_move", out var gm) ? gm : default;
        double? speedMmS = null;
        double? speedFactor = null;
        double? extrudeFactor = null;

        if (gcodeMove.ValueKind != JsonValueKind.Undefined)
        {
            var spd = TryDouble(gcodeMove, "speed");
            if (!double.IsNaN(spd) && spd > 0) speedMmS = spd / 60.0;

            var spdFactor = TryDouble(gcodeMove, "speed_factor");
            if (!double.IsNaN(spdFactor)) speedFactor = spdFactor * 100.0;

            var extFactor = TryDouble(gcodeMove, "extrude_factor");
            if (!double.IsNaN(extFactor)) extrudeFactor = extFactor;
        }

        if ((!speedMmS.HasValue || speedMmS.Value <= 0) && displayStatus.ValueKind != JsonValueKind.Undefined)
        {
            var dspSpd = TryDouble(displayStatus, "speed");
            if (!double.IsNaN(dspSpd) && dspSpd > 0) speedMmS = dspSpd / 60.0;
        }

        var motionReport = status.TryGetProperty("motion_report", out var mr) ? mr : default;
        double? extruderVelocity = null;
        double? toolheadVelocity = null;
        if (motionReport.ValueKind != JsonValueKind.Undefined)
        {
            var extVel = TryDouble(motionReport, "live_extruder_velocity");
            if (!double.IsNaN(extVel)) extruderVelocity = extVel;

            var thVel = TryDouble(motionReport, "live_velocity");
            if (!double.IsNaN(thVel) && thVel >= 0) toolheadVelocity = thVel;
        }

        int progress = isActiveJob ? (int)Math.Round(progress01 * 100) : 0;
        string filename = TryString(print, "filename") ?? string.Empty;

        double printDuration = isActiveJob ? TryDouble(print, "print_duration") : double.NaN;
        double filamentUsed = isActiveJob ? TryDouble(print, "filament_used") : double.NaN;

        if (_showFilamentInOverlay && isActiveJob && !string.IsNullOrWhiteSpace(filename))
        {
            var needs = string.IsNullOrWhiteSpace(filamentType) || string.IsNullOrWhiteSpace(filamentBrand) || string.IsNullOrWhiteSpace(filamentColor) || !filamentTotalMm.HasValue;
            var now = DateTime.UtcNow;
            FilamentMeta? cached = null;
            if (_filamentCache.TryGetValue(filename, out var entry))
            {
                if ((now - entry.fetchedAt).TotalSeconds < _filamentCacheSeconds)
                {
                    cached = entry.meta;
                }
            }

            if (cached == null && needs)
            {
                try
                {
                    var baseUri = new Uri(_moonrakerBase);
                    var node = await _moonrakerClient.GetFileMetadataAsync(baseUri, filename, _apiKey, _authHeader, ct);
                    var resultObj = node?["result"]?.AsObject();
                    if (resultObj != null)
                    {
                        cached = new FilamentMeta
                        {
                            Type = TryGetStringCaseInsensitive(resultObj, "filament_type"),
                            Brand = TryGetStringCaseInsensitive(resultObj, "filament_brand") ?? TryGetStringCaseInsensitive(resultObj, "filament_name"),
                            Color = TryGetStringCaseInsensitive(resultObj, "filament_color"),
                            Name = TryGetStringCaseInsensitive(resultObj, "filament_name"),
                            UsedMm = TryGetDoubleCaseInsensitive(resultObj, "filament_used_mm")
                                     ?? TryGetDoubleCaseInsensitive(resultObj, "filament_used")
                                     ?? (TryGetDoubleCaseInsensitive(resultObj, "filament_used_m") * 1000.0),
                            TotalMm = TryGetDoubleCaseInsensitive(resultObj, "filament_total_mm")
                                      ?? TryGetDoubleCaseInsensitive(resultObj, "filament_total")
                                      ?? (TryGetDoubleCaseInsensitive(resultObj, "filament_total_m") * 1000.0)
                        };
                        _filamentCache[filename] = (now, cached);
                    }
                }
                catch (Exception e1)
                {
                    _logger.LogDebug(e1, "[Overlay] Failed to fetch file metadata for filament info");
                }
            }

            if (cached != null)
            {
                filamentType ??= cached.Type;
                filamentBrand ??= cached.Brand;
                filamentColor ??= cached.Color;
                filamentName ??= cached.Name;
                filamentUsedMm ??= cached.UsedMm;
                filamentTotalMm ??= cached.TotalMm;
            }
        }

        DateTime? eta = null;
        if (isActiveJob && progress01 > 0.01 && !double.IsNaN(printDuration) && printDuration > 0)
        {
            var estimatedTotal = printDuration / progress01;
            var remaining = estimatedTotal - printDuration;
            eta = DateTime.UtcNow.AddSeconds(remaining);
        }

        string? providerSlicer = null;
        double? layerHeight = null;
        double? extrusionWidth = null;
        double? providerFilamentTotalMm = null;
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
                    providerFilamentTotalMm = meta.FilamentTotalMm;
                }
            }
            catch { }
        }

        if (isActiveJob && !flowVolume.HasValue && extruderVelocity.HasValue && extruderVelocity.Value > 0.01)
        {
            var filDiam = 1.75;
            var radius = filDiam / 2.0;
            var crossSectionArea = Math.PI * radius * radius;
            var flowMultiplier = extrudeFactor ?? 1.0;
            flowVolume = extruderVelocity.Value * crossSectionArea * flowMultiplier;
        }

        if (!isActiveJob)
        {
            flowVolume = null;
        }

        double? displaySpeed = null;
        if (isActiveJob && toolheadVelocity.HasValue && toolheadVelocity.Value > 0.1)
        {
            displaySpeed = toolheadVelocity.Value;
        }
        else if (isActiveJob)
        {
            displaySpeed = 0.0;
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
            Time = DateTime.UtcNow,
            Filename = filename,
            Speed = displaySpeed,
            SpeedFactor = speedFactor,
            Flow = flowVolume,
            Filament = filamentUsed,
            FilamentType = filamentType,
            FilamentBrand = filamentBrand,
            FilamentColor = filamentColor,
            FilamentName = filamentName,
            FilamentUsedMm = double.IsNaN(filamentUsed) ? filamentUsedMm : filamentUsed,
            FilamentTotalMm = filamentTotalMm ?? providerFilamentTotalMm,
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
                m =>
                {
                    var fmt = m.Groups[1].Success ? m.Groups[1].Value : "HH:mm:ss";
                    try
                    {
                        // Treat the incoming value as UTC (it is produced as UTC) then convert to display timezone.
                        var utc = DateTime.SpecifyKind(value, DateTimeKind.Utc);
                        var display = TimeZoneInfo.ConvertTimeFromUtc(utc, _displayTimeZone);
                        return display.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
                    }
                    catch
                    {
                        return value.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
                    }
                },
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
        s = ReplaceNullableInt(s, "layer", d.Layer);
        s = ReplaceNullableInt(s, "layermax", d.LayerMax);
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
        s = ReplaceStr(s, "filament_type", d.FilamentType ?? string.Empty);
        s = ReplaceStr(s, "filament_brand", d.FilamentBrand ?? string.Empty);
        s = ReplaceStr(s, "filament_color", d.FilamentColor ?? string.Empty);
        s = ReplaceStr(s, "filament_name", d.FilamentName ?? string.Empty);

        {
            var speedStr = d.Speed.HasValue ? d.Speed.Value.ToString("0") : "-";
            if (_padSpeedWidth > 0)
            {
                try { speedStr = speedStr.PadLeft(_padSpeedWidth); } catch { }
            }
            s = ReplaceStr(s, "speed", speedStr);
        }
        s = ReplaceStr(s, "speedfactor", d.SpeedFactor.HasValue ? d.SpeedFactor.Value.ToString("0") + "%" : "-");

        {
            var flowStr = d.Flow.HasValue ? d.Flow.Value.ToString("0.0") : "-";
            if (_padFlowWidth > 0)
            {
                try { flowStr = flowStr.PadLeft(_padFlowWidth); } catch { }
            }
            s = ReplaceStr(s, "flow", flowStr);
        }

        s = ReplaceStr(s, "filament", d.Filament.HasValue && !double.IsNaN(d.Filament.Value) ? (d.Filament.Value / 1000.0).ToString("0.00") : "0.00");
        s = ReplaceNum(s, "filament_used_mm", d.FilamentUsedMm.HasValue ? d.FilamentUsedMm.Value : double.NaN, "0");
        s = ReplaceNum(s, "filament_total_mm", d.FilamentTotalMm.HasValue ? d.FilamentTotalMm.Value : double.NaN, "0");

        if (d.ETA.HasValue)
        {
            s = ReplaceDate(s, "eta", d.ETA.Value);
        }
        else
        {
            s = ReplaceStr(s, "eta", "-");
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(_template, @"\{(?:layer|layermax|layers)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var left = d.Layer.HasValue ? d.Layer.Value.ToString() : "-";
            var right = d.LayerMax.HasValue ? d.LayerMax.Value.ToString() : "-";
            s = s + $"  |  Layers: {left}/{right}";
        }

        try
        {
            var audioName = _audioProvider?.Invoke();
            if (!string.IsNullOrWhiteSpace(audioName))
            {
                s = s + "\n" + "Song: " + audioName;
            }
        }
        catch { }

        s = s.Replace("\r", string.Empty);
        s = s.Replace("%", "%%");
        return s;
    }

    private async Task SafeWriteAsync(string content, CancellationToken ct)
    {
        var path = _textFilePath;
        try { Directory.CreateDirectory(_textFileDir); } catch { }

        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            try { var parent = Path.GetDirectoryName(tmp); if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent); } catch { }
            await File.WriteAllTextAsync(tmp, content, Encoding.UTF8, ct);
            try
            {
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                try { File.Replace(tmp, path, null, ignoreMetadataErrors: true); }
                catch { File.Copy(tmp, path, overwrite: true); }
                finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Overlay] SafeWrite failed");
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
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
        public double? Filament { get; init; } // Filament used in mm (legacy field for {filament} token in meters)
        public string? FilamentType { get; init; }
        public string? FilamentBrand { get; init; }
        public string? FilamentColor { get; init; }
        public string? FilamentName { get; init; }
        public double? FilamentUsedMm { get; init; }
        public double? FilamentTotalMm { get; init; }
        public string? Slicer { get; init; }
        public DateTime? ETA { get; init; }
    }

    private sealed class FilamentMeta
    {
        public string? Type { get; init; }
        public string? Brand { get; init; }
        public string? Color { get; init; }
        public string? Name { get; init; }
        public double? UsedMm { get; init; }
        public double? TotalMm { get; init; }
    }

    private static string? TryGetStringCaseInsensitive(System.Text.Json.Nodes.JsonObject obj, string key)
    {
        try
        {
            if (obj.TryGetPropertyValue(key, out var node) && node != null) return node.ToString();
            var upper = key.ToUpperInvariant();
            if (obj.TryGetPropertyValue(upper, out var nodeU) && nodeU != null) return nodeU.ToString();
            foreach (var kv in obj)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value?.ToString();
            }
        }
        catch { }
        return null;
    }

    private static double? TryGetDoubleCaseInsensitive(System.Text.Json.Nodes.JsonObject obj, string key)
    {
        try
        {
            if (obj.TryGetPropertyValue(key, out var node) && node != null)
            {
                if (node is System.Text.Json.Nodes.JsonValue v)
                {
                    if (v.TryGetValue<double>(out var d)) return d;
                    if (double.TryParse(v.ToString(), out var d2)) return d2;
                }
            }
            var upper = key.ToUpperInvariant();
            if (obj.TryGetPropertyValue(upper, out var nodeU) && nodeU != null)
            {
                if (nodeU is System.Text.Json.Nodes.JsonValue v2)
                {
                    if (v2.TryGetValue<double>(out var d)) return d;
                    if (double.TryParse(v2.ToString(), out var d2)) return d2;
                }
            }
            foreach (var kv in obj)
            {
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    var n = kv.Value;
                    if (n is System.Text.Json.Nodes.JsonValue v3)
                    {
                        if (v3.TryGetValue<double>(out var d)) return d;
                        if (double.TryParse(v3.ToString(), out var d2)) return d2;
                    }
                }
            }
        }
        catch { }
        return null;
    }
}
