// Removed duplicate ExtractJobIdFromJobQueue and ExtractFilenameFromJobQueue methods from top of file
using System.Text.Json.Nodes;

internal static class MoonrakerClient
{
    /// <summary>
    /// Try to extract a base printer URI (scheme + host) from the configured Stream:Source URL.
    /// Returns a Uri pointing at the printer host with port 7125 (Moonraker default) when possible.
    /// </summary>
    public static Uri? GetPrinterBaseUriFromStreamSource(string source)
    {
        try
        {
            // If source is a full URL, parse it and replace the port with 7125
            if (Uri.TryCreate(source, UriKind.Absolute, out var srcUri))
            {
                var builder = new UriBuilder(srcUri)
                {
                    Port = 7125,
                    Path = string.Empty,
                    Query = string.Empty
                };
                return builder.Uri;
            }

            // Fallback: try to interpret as host or host:port
            if (!source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var host = source.Split('/')[0];
                if (!host.Contains(":")) host = host + ":7125";
                if (Uri.TryCreate("http://" + host, UriKind.Absolute, out var u)) return u;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Rich print info fetched from Moonraker: filename, progress, times, temperatures, sensors.
    /// </summary>
    public record MoonrakerPrintInfo(
        string? Filename,
        string? State,
        double? ProgressPercent,
        TimeSpan? Elapsed,
        TimeSpan? Remaining,
        double? BedTempActual,
        double? BedTempTarget,
        (double? Actual, double? Target)? Tool0Temp,
        List<SensorInfo>? Sensors,
        JsonNode? RawJson,
        string? JobQueueId
    );

    /// <summary>
    /// Sensor information from Moonraker devices API
    /// </summary>
    public record SensorInfo(
        string Name,
        string? FriendlyName,
        Dictionary<string, object> Measurements
    );

    public static async Task<MoonrakerPrintInfo?> GetPrintInfoAsync(Uri baseUri, string? apiKey = null, string? authHeader = null, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[Moonraker] Querying Moonraker at: {baseUri}");
        using var http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(5) };

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var header = string.IsNullOrWhiteSpace(authHeader) ? "X-Api-Key" : authHeader;
            try { http.DefaultRequestHeaders.Remove(header); } catch { }
            try { http.DefaultRequestHeaders.Add(header, apiKey); } catch { }
            Console.WriteLine($"[Moonraker] Using auth header: {header}");
        }

        // We'll try to assemble a combined view by querying job and print_stats endpoints
        JsonNode? jobNode = null;
        JsonNode? statsNode = null;
        JsonNode? dispNode = null;

        try
        {
            var endpoint = "/printer/objects/query?select=job";
            Console.WriteLine($"[Moonraker] Querying: {endpoint}");
            var jobResp = await http.GetAsync(endpoint, cancellationToken);
            Console.WriteLine($"[Moonraker] Response status: {jobResp.StatusCode}");
            if (jobResp.IsSuccessStatusCode)
            {
                var jt = await jobResp.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"[Moonraker] Job response:\n{jt}");
                jobNode = JsonNode.Parse(jt);
            }
        }
        catch (Exception ex) { Console.WriteLine($"[Moonraker] Job query error: {ex.Message}"); }

        try
        {
            // Query print_stats directly without select to get all fields including state
            var endpoint = "/printer/objects/query?print_stats";
            Console.WriteLine($"[Moonraker] Querying: {endpoint}");
            var statsResp = await http.GetAsync(endpoint, cancellationToken);
            Console.WriteLine($"[Moonraker] Response status: {statsResp.StatusCode}");
            if (statsResp.IsSuccessStatusCode)
            {
                var st = await statsResp.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"[Moonraker] Print_stats response:\n{st}");
                statsNode = JsonNode.Parse(st);
            }
        }
        catch (Exception ex) { Console.WriteLine($"[Moonraker] Print_stats query error: {ex.Message}"); }

        try
        {
            var endpoint = "/printer/objects/query?select=display_status";
            Console.WriteLine($"[Moonraker] Querying: {endpoint}");
            var dispResp = await http.GetAsync(endpoint, cancellationToken);
            Console.WriteLine($"[Moonraker] Response status: {dispResp.StatusCode}");
            if (dispResp.IsSuccessStatusCode)
            {
                var dt = await dispResp.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"[Moonraker] Display_status response:\n{dt}");
                dispNode = JsonNode.Parse(dt);
            }
        }
        catch (Exception ex) { Console.WriteLine($"[Moonraker] Display_status query error: {ex.Message}"); }

        // Try to get job queue status to find the current/queued filename
        JsonNode? queueNode = null;
        try
        {
            var endpoint = "/server/job_queue/status";
            Console.WriteLine($"[Moonraker] Querying: {endpoint}");
            var queueResp = await http.GetAsync(endpoint, cancellationToken);
            Console.WriteLine($"[Moonraker] Response status: {queueResp.StatusCode}");
            if (queueResp.IsSuccessStatusCode)
            {
                var qt = await queueResp.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"[Moonraker] Job queue response:\n{qt}");
                queueNode = JsonNode.Parse(qt);
            }
        }
        catch (Exception ex) { Console.WriteLine($"[Moonraker] Job queue query error: {ex.Message}"); }

        // Try to get current print stats metadata which often includes filename
        JsonNode? metadataNode = null;
        try
        {
            var endpoint = "/server/files/metadata?filename=";
            // First try to get the current file from print_stats
            var currentFile = statsNode?["result"]?["status"]?["print_stats"]?["filename"]?.ToString();
            if (!string.IsNullOrWhiteSpace(currentFile))
            {
                endpoint = $"/server/files/metadata?filename={Uri.EscapeDataString(currentFile)}";
                Console.WriteLine($"[Moonraker] Querying: {endpoint}");
                var metaResp = await http.GetAsync(endpoint, cancellationToken);
                Console.WriteLine($"[Moonraker] Response status: {metaResp.StatusCode}");
                if (metaResp.IsSuccessStatusCode)
                {
                    var mt = await metaResp.Content.ReadAsStringAsync(cancellationToken);
                    Console.WriteLine($"[Moonraker] Metadata response:\n{mt}");
                    metadataNode = JsonNode.Parse(mt);
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"[Moonraker] Metadata query error: {ex.Message}"); }

        // Also try the history endpoint to get last print info
        JsonNode? historyNode = null;
        try
        {
            var endpoint = "/server/history/list?limit=1&order=desc";
            Console.WriteLine($"[Moonraker] Querying: {endpoint}");
            var histResp = await http.GetAsync(endpoint, cancellationToken);
            Console.WriteLine($"[Moonraker] Response status: {histResp.StatusCode}");
            if (histResp.IsSuccessStatusCode)
            {
                var ht = await histResp.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"[Moonraker] History response:\n{ht}");
                historyNode = JsonNode.Parse(ht);
            }
        }
        catch (Exception ex) { Console.WriteLine($"[Moonraker] History query error: {ex.Message}"); }

        // Try to get temperature data directly
        JsonNode? tempNode = null;
        try
        {
            var endpoint = "/printer/objects/query?select=heater_bed&select=extruder";
            Console.WriteLine($"[Moonraker] Querying: {endpoint}");
            var tempResp = await http.GetAsync(endpoint, cancellationToken);
            Console.WriteLine($"[Moonraker] Response status: {tempResp.StatusCode}");
            if (tempResp.IsSuccessStatusCode)
            {
                var tt = await tempResp.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"[Moonraker] Temperature response:\n{tt}");
                tempNode = JsonNode.Parse(tt);
            }
        }
        catch (Exception ex) { Console.WriteLine($"[Moonraker] Temperature query error: {ex.Message}"); }

        // Try to get sensor information (optional, may not be supported on all Moonraker versions)
        List<SensorInfo>? sensors = null;
        try
        {
            // Get the list of sensors with their values
            var listEndpoint = "/server/sensors/list?extended=true";
            Console.WriteLine($"[Moonraker] Querying: {listEndpoint}");
            var listResp = await http.GetAsync(listEndpoint, cancellationToken);
            Console.WriteLine($"[Moonraker] Response status: {listResp.StatusCode}");
            if (listResp.IsSuccessStatusCode)
            {
                var lt = await listResp.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"[Moonraker] Sensor list response:\n{lt}");
                var listNode = JsonNode.Parse(lt);
                sensors = ExtractSensorInfoFromList(listNode);
                if (sensors != null && sensors.Count > 0)
                {
                    Console.WriteLine($"[Moonraker] Found {sensors.Count} sensor(s)");
                }
                else
                {
                    Console.WriteLine($"[Moonraker] No sensors found or configured");
                }
            }
            else
            {
                Console.WriteLine($"[Moonraker] Sensor API not available (this is optional and may not be supported on all Moonraker versions)");
            }
        }
        catch (Exception ex) { Console.WriteLine($"[Moonraker] Sensor query error: {ex.Message}"); }

        // Fallback: try printer/printerinfo
        JsonNode? infoNode = null;
        try
        {
            var endpoint = "/printer/printerinfo";
            Console.WriteLine($"[Moonraker] Querying: {endpoint}");
            var infoResp = await http.GetAsync(endpoint, cancellationToken);
            Console.WriteLine($"[Moonraker] Response status: {infoResp.StatusCode}");
            if (infoResp.IsSuccessStatusCode)
            {
                var it = await infoResp.Content.ReadAsStringAsync(cancellationToken);
                Console.WriteLine($"[Moonraker] Printerinfo response:\n{it}");
                infoNode = JsonNode.Parse(it);
            }
        }
        catch (Exception ex) { Console.WriteLine($"[Moonraker] Printerinfo query error: {ex.Message}"); }

        // Build the MoonrakerPrintInfo
        string? filename = null;
        string? state = null;
        double? progress = null;
        TimeSpan? elapsed = null;
        TimeSpan? remaining = null;
        double? bedActual = null;
        double? bedTarget = null;
        (double? Actual, double? Target)? tool0 = (null, null);

        // Check if the responses actually contain data (not just null values)
        // Moonraker returns {"result":{"status":{"select":{"job":null}}}} when no print is active
        var hasJobData = jobNode != null && !IsNullResponse(jobNode, "job");
        var hasStatsData = statsNode != null && !IsNullResponse(statsNode, "print_stats");
        var hasDisplayData = dispNode != null && !IsNullResponse(dispNode, "display_status");
        var hasTempData = tempNode != null && (!IsNullResponse(tempNode, "heater_bed") || !IsNullResponse(tempNode, "extruder"));

        Console.WriteLine($"[Moonraker] Data available - Job: {hasJobData}, Stats: {hasStatsData}, Display: {hasDisplayData}, Temp: {hasTempData}");

        // Try to extract filename and job id from job queue
        string? jobQueueId = null;
        if (queueNode != null)
        {
            var queueFilename = ExtractFilenameFromJobQueue(queueNode);
            jobQueueId = ExtractJobIdFromJobQueue(queueNode);
            if (!string.IsNullOrWhiteSpace(queueFilename))
            {
                filename = queueFilename;
                Console.WriteLine($"[Moonraker] Extracted from job queue - Filename: {filename}");
            }
            if (!string.IsNullOrWhiteSpace(jobQueueId))
            {
                Console.WriteLine($"[Moonraker] Extracted from job queue - JobQueueId: {jobQueueId}");
            }
        }

        // 2. Current job object
        if (hasJobData && string.IsNullOrWhiteSpace(filename))
        {
            filename = ExtractFilenameFromResponse(jobNode);
            state = ExtractStateFromJob(jobNode) ?? state;
            Console.WriteLine($"[Moonraker] Extracted from job - Filename: {filename}, State: {state}");
        }

        // 3. History (last printed file)
        if (string.IsNullOrWhiteSpace(filename) && historyNode != null)
        {
            filename = ExtractFilenameFromHistory(historyNode);
            if (!string.IsNullOrWhiteSpace(filename))
            {
                Console.WriteLine($"[Moonraker] Extracted from history - Filename: {filename}");
            }
        }

        // 4. Metadata
        if (string.IsNullOrWhiteSpace(filename) && metadataNode != null)
        {
            filename = ExtractFilenameFromResponse(metadataNode);
            if (!string.IsNullOrWhiteSpace(filename))
            {
                Console.WriteLine($"[Moonraker] Extracted from metadata - Filename: {filename}");
            }
        }
        if (hasStatsData)
        {
            var p = ExtractProgressFromStats(statsNode);
            if (p.HasValue) progress = p.Value;
            var times = ExtractTimesFromStats(statsNode);
            if (times.elapsed.HasValue) elapsed = times.elapsed;
            if (times.remaining.HasValue) remaining = times.remaining;
            // Try to extract state from print_stats (common location for Moonraker)
            var extractedState = ExtractStateFromStats(statsNode);
            if (!string.IsNullOrWhiteSpace(extractedState))
            {
                state = extractedState;
                Console.WriteLine($"[Moonraker] Extracted state from print_stats: {state}");
            }
            Console.WriteLine($"[Moonraker] Extracted from stats - Progress: {progress}, Elapsed: {elapsed}, Remaining: {remaining}, State: {state}");
        }
        if (hasDisplayData)
        {
            var temps = ExtractTempsFromDisplay(dispNode);
            if (temps.bedActual.HasValue) bedActual = temps.bedActual;
            if (temps.bedTarget.HasValue) bedTarget = temps.bedTarget;
            if (temps.tool0Actual.HasValue || temps.tool0Target.HasValue) tool0 = (temps.tool0Actual, temps.tool0Target);
            Console.WriteLine($"[Moonraker] Extracted temps from display - Bed: {bedActual}/{bedTarget}, Tool0: {tool0?.Actual}/{tool0?.Target}");
        }

        // Try direct temperature query if display_status didn't have it
        if (hasTempData && (bedActual == null || tool0?.Actual == null))
        {
            var temps = ExtractTempsFromHeaterObjects(tempNode);
            if (temps.bedActual.HasValue && bedActual == null) bedActual = temps.bedActual;
            if (temps.bedTarget.HasValue && bedTarget == null) bedTarget = temps.bedTarget;
            if ((temps.tool0Actual.HasValue || temps.tool0Target.HasValue) && tool0?.Actual == null) tool0 = (temps.tool0Actual, temps.tool0Target);
            Console.WriteLine($"[Moonraker] Extracted temps from heater objects - Bed: {bedActual}/{bedTarget}, Tool0: {tool0?.Actual}/{tool0?.Target}");
        }

        // Try to pick filename again from infoNode
        if (string.IsNullOrWhiteSpace(filename) && infoNode != null) filename = ExtractFilenameFromResponse(infoNode);

        // If nothing was found at all, attempt the simpler fallback filename-only probe
        if (string.IsNullOrWhiteSpace(filename))
        {
            try
            {
                var fallback = await http.GetAsync("/server/objects", cancellationToken);
                if (fallback.IsSuccessStatusCode)
                {
                    var ft = await fallback.Content.ReadAsStringAsync(cancellationToken);
                    var fn = JsonNode.Parse(ft);
                    filename = ExtractFilenameFromResponse(fn);
                    infoNode = fn;
                }
            }
            catch { }
        }

        // Compose a raw JSON snapshot for debugging (combine nodes)
        var raw = new JsonObject();
        if (jobNode != null) raw["job"] = jobNode;
        if (statsNode != null) raw["print_stats"] = statsNode;
        if (dispNode != null) raw["display_status"] = dispNode;
        if (infoNode != null) raw["info"] = infoNode;

    return new MoonrakerPrintInfo(filename, state, progress, elapsed, remaining, bedActual, bedTarget, tool0, sensors, raw, jobQueueId);
    }

    private static bool IsNullResponse(JsonNode? node, string selectKey)
    {
        // Check if the response has the pattern: {"result":{"status":{"select":{selectKey:null}}}}
        // OR {"result":{"status":{selectKey:...}}} (for direct queries like ?print_stats)
        if (node is JsonObject obj &&
            obj.TryGetPropertyValue("result", out var result) && result is JsonObject resObj &&
            resObj.TryGetPropertyValue("status", out var status) && status is JsonObject statObj)
        {
            // First check for select pattern: {"result":{"status":{"select":{selectKey:...}}}}
            if (statObj.TryGetPropertyValue("select", out var select) && select is JsonObject selObj &&
                selObj.TryGetPropertyValue(selectKey, out var value))
            {
                return value == null || value.ToString() == "null";
            }
            
            // Also check for direct pattern: {"result":{"status":{selectKey:...}}}
            if (statObj.TryGetPropertyValue(selectKey, out var directValue))
            {
                return directValue == null || directValue.ToString() == "null";
            }
        }
        return true; // If the structure doesn't match, consider it null
    }

    private static string? ExtractFilenameFromJobQueue(JsonNode? queueNode)
    {
        try
        {
            // Expected structure: {"result":{"queued_jobs":[{"filename":"job1.gcode",...}],"queue_state":"ready"}}
            if (queueNode is JsonObject root &&
                root.TryGetPropertyValue("result", out var result) && result is JsonObject resObj)
            {
                // Try queued_jobs array first
                if (resObj.TryGetPropertyValue("queued_jobs", out var jobs) && jobs is JsonArray jobsArray && jobsArray.Count > 0)
                {
                    // Get the first job in the queue
                    if (jobsArray[0] is JsonObject firstJob &&
                        firstJob.TryGetPropertyValue("filename", out var fn) && fn != null)
                    {
                        return fn.ToString();
                    }
                }
            }
        }
        catch { }
        return null;
    }

    // Extract job queue id from Moonraker job queue response
    private static string? ExtractJobIdFromJobQueue(JsonNode? queueNode)
    {
        try
        {
            // Expected structure: {"result":{"queued_jobs":[{"id":"jobid123", ...}]}}
            if (queueNode is JsonObject root &&
                root.TryGetPropertyValue("result", out var result) && result is JsonObject resObj)
            {
                if (resObj.TryGetPropertyValue("queued_jobs", out var jobs) && jobs is JsonArray jobsArray && jobsArray.Count > 0)
                {
                    if (jobsArray[0] is JsonObject firstJob &&
                        firstJob.TryGetPropertyValue("id", out var idVal) && idVal != null)
                    {
                        return idVal.ToString();
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static string? ExtractFilenameFromHistory(JsonNode? historyNode)
    {
        try
        {
            // Expected structure: {"result":{"count":1,"jobs":[{"filename":"test.gcode",...}]}}
            if (historyNode is JsonObject root &&
                root.TryGetPropertyValue("result", out var result) && result is JsonObject resObj &&
                resObj.TryGetPropertyValue("jobs", out var jobs) && jobs is JsonArray jobsArray && jobsArray.Count > 0)
            {
                // Get the most recent job (first in the list when ordered desc)
                if (jobsArray[0] is JsonObject firstJob &&
                    firstJob.TryGetPropertyValue("filename", out var fn) && fn != null)
                {
                    return fn.ToString();
                }
            }
        }
        catch { }
        return null;
    }

    private static string? ExtractFilenameFromResponse(JsonNode? node)
    {
        if (node == null) return null;
        // Search recursively for names or filename fields
        string? TryFind(JsonNode? n)
        {
            if (n == null) return null;
            if (n is JsonObject o)
            {
                foreach (var kv in o)
                {
                    var key = kv.Key?.ToLowerInvariant() ?? string.Empty;
                    if (key.Contains("file") || key.Contains("filename") || key.Contains("name"))
                    {
                        var maybe = ExtractNameFromNode(kv.Value);
                        if (!string.IsNullOrWhiteSpace(maybe)) return maybe;
                    }
                    var rec = TryFind(kv.Value);
                    if (!string.IsNullOrWhiteSpace(rec)) return rec;
                }
            }
            else if (n is JsonArray a)
            {
                foreach (var it in a)
                {
                    var r = TryFind(it);
                    if (!string.IsNullOrWhiteSpace(r)) return r;
                }
            }
            else
            {
                var s = n.ToString();
                if (!string.IsNullOrWhiteSpace(s) && (s.EndsWith(".gcode", StringComparison.OrdinalIgnoreCase) || s.IndexOf('.') > 0 && s.Length < 200)) return s;
            }
            return null;
        }

        return TryFind(node);
    }

    private static string? ExtractStateFromJob(JsonNode? jobNode)
    {
        try
        {
            // job -> state or status
            if (jobNode is JsonObject j)
            {
                if (j.TryGetPropertyValue("result", out var res) && res is JsonObject r)
                {
                    if (r.TryGetPropertyValue("job", out var job) && job is JsonObject jj)
                    {
                        if (jj.TryGetPropertyValue("state", out var st)) return st?.ToString();
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static double? ExtractProgressFromStats(JsonNode? statsNode)
    {
        try
        {
            // stats result -> progress -> completion
            if (statsNode is JsonObject s && s.TryGetPropertyValue("result", out var res) && res is JsonObject r)
            {
                if (r.TryGetPropertyValue("print_stats", out var ps) && ps is JsonObject psobj)
                {
                    if (psobj.TryGetPropertyValue("progress", out var prog) && prog is JsonObject progObj)
                    {
                        if (progObj.TryGetPropertyValue("completion", out var comp) && comp != null)
                        {
                            var compStr = comp.ToString();
                            if (!string.IsNullOrWhiteSpace(compStr) && double.TryParse(compStr, out var d)) return d * 100.0;
                        }
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static (TimeSpan? elapsed, TimeSpan? remaining) ExtractTimesFromStats(JsonNode? statsNode)
    {
        try
        {
            if (statsNode is JsonObject s && s.TryGetPropertyValue("result", out var res) && res is JsonObject r)
            {
                if (r.TryGetPropertyValue("print_stats", out var ps) && ps is JsonObject psobj)
                {
                    TimeSpan? el = null;
                    TimeSpan? rem = null;
                    if (psobj.TryGetPropertyValue("print_duration", out var pd) && pd != null)
                    {
                        var pdStr = pd.ToString();
                        if (!string.IsNullOrWhiteSpace(pdStr) && double.TryParse(pdStr, out var secs)) el = TimeSpan.FromSeconds(secs);
                    }
                    if (psobj.TryGetPropertyValue("print_time_left", out var pl) && pl != null)
                    {
                        var plStr = pl.ToString();
                        if (!string.IsNullOrWhiteSpace(plStr) && double.TryParse(plStr, out var secs2)) rem = TimeSpan.FromSeconds(secs2);
                    }
                    return (el, rem);
                }
            }
        }
        catch { }
        return (null, null);
    }

    private static string? ExtractStateFromStats(JsonNode? statsNode)
    {
        try
        {
            // stats result -> status -> print_stats -> state
            if (statsNode is JsonObject s && s.TryGetPropertyValue("result", out var res) && res is JsonObject r)
            {
                // Try direct print_stats access (result.print_stats.state)
                if (r.TryGetPropertyValue("print_stats", out var ps) && ps is JsonObject psobj)
                {
                    if (psobj.TryGetPropertyValue("state", out var st) && st != null)
                    {
                        var stStr = st.ToString();
                        if (!string.IsNullOrWhiteSpace(stStr)) return stStr;
                    }
                }
                
                // Try status -> print_stats access (result.status.print_stats.state - common Moonraker structure)
                if (r.TryGetPropertyValue("status", out var status) && status is JsonObject statusObj)
                {
                    if (statusObj.TryGetPropertyValue("print_stats", out var ps2) && ps2 is JsonObject psobj2)
                    {
                        if (psobj2.TryGetPropertyValue("state", out var st2) && st2 != null)
                        {
                            var stStr2 = st2.ToString();
                            if (!string.IsNullOrWhiteSpace(stStr2)) return stStr2;
                        }
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static (double? bedActual, double? bedTarget, double? tool0Actual, double? tool0Target) ExtractTempsFromDisplay(JsonNode? dispNode)
    {
        try
        {
            if (dispNode is JsonObject s && s.TryGetPropertyValue("result", out var res) && res is JsonObject r)
            {
                if (r.TryGetPropertyValue("display_status", out var ds) && ds is JsonObject dsobj)
                {
                    double? bedA = null, bedT = null, t0A = null, t0T = null;
                    if (dsobj.TryGetPropertyValue("temperature", out var tmp) && tmp is JsonObject tmpo)
                    {
                        if (tmpo.TryGetPropertyValue("bed", out var bed) && bed is JsonObject bedo)
                        {
                            if (bedo.TryGetPropertyValue("actual", out var ba))
                            {
                                var baStr = ba?.ToString();
                                if (!string.IsNullOrWhiteSpace(baStr) && double.TryParse(baStr, out var baD)) bedA = baD;
                            }
                            if (bedo.TryGetPropertyValue("target", out var bt))
                            {
                                var btStr = bt?.ToString();
                                if (!string.IsNullOrWhiteSpace(btStr) && double.TryParse(btStr, out var btD)) bedT = btD;
                            }
                        }
                        if (tmpo.TryGetPropertyValue("tool0", out var t0) && t0 is JsonObject t0o)
                        {
                            if (t0o.TryGetPropertyValue("actual", out var ta))
                            {
                                var taStr = ta?.ToString();
                                if (!string.IsNullOrWhiteSpace(taStr) && double.TryParse(taStr, out var taD)) t0A = taD;
                            }
                            if (t0o.TryGetPropertyValue("target", out var tt))
                            {
                                var ttStr = tt?.ToString();
                                if (!string.IsNullOrWhiteSpace(ttStr) && double.TryParse(ttStr, out var ttD)) t0T = ttD;
                            }
                        }
                    }
                    return (bedA, bedT, t0A, t0T);
                }
            }
        }
        catch { }
        return (null, null, null, null);
    }

    private static (double? bedActual, double? bedTarget, double? tool0Actual, double? tool0Target) ExtractTempsFromHeaterObjects(JsonNode? tempNode)
    {
        try
        {
            // Structure: {"result":{"status":{"select":{"heater_bed":{...}, "extruder":{...}}}}}
            if (tempNode is JsonObject root &&
                root.TryGetPropertyValue("result", out var res) && res is JsonObject r &&
                r.TryGetPropertyValue("status", out var stat) && stat is JsonObject s &&
                s.TryGetPropertyValue("select", out var sel) && sel is JsonObject selObj)
            {
                double? bedA = null, bedT = null, t0A = null, t0T = null;

                // Extract bed temps
                if (selObj.TryGetPropertyValue("heater_bed", out var bed) && bed is JsonObject bedObj)
                {
                    if (bedObj.TryGetPropertyValue("temperature", out var bTemp))
                    {
                        var btStr = bTemp?.ToString();
                        if (!string.IsNullOrWhiteSpace(btStr) && double.TryParse(btStr, out var btD)) bedA = btD;
                    }
                    if (bedObj.TryGetPropertyValue("target", out var bTarget))
                    {
                        var btgStr = bTarget?.ToString();
                        if (!string.IsNullOrWhiteSpace(btgStr) && double.TryParse(btgStr, out var btgD)) bedT = btgD;
                    }
                }

                // Extract extruder/tool0 temps
                if (selObj.TryGetPropertyValue("extruder", out var ext) && ext is JsonObject extObj)
                {
                    if (extObj.TryGetPropertyValue("temperature", out var eTemp))
                    {
                        var etStr = eTemp?.ToString();
                        if (!string.IsNullOrWhiteSpace(etStr) && double.TryParse(etStr, out var etD)) t0A = etD;
                    }
                    if (extObj.TryGetPropertyValue("target", out var eTarget))
                    {
                        var etgStr = eTarget?.ToString();
                        if (!string.IsNullOrWhiteSpace(etgStr) && double.TryParse(etgStr, out var etgD)) t0T = etgD;
                    }
                }

                return (bedA, bedT, t0A, t0T);
            }
        }
        catch { }
        return (null, null, null, null);
    }

    private static string? ExtractNameFromNode(JsonNode? node)
    {
        if (node == null) return null;
        if (node is JsonObject o)
        {
            if (o.TryGetPropertyValue("name", out var n) && n != null) return n.ToString();
            if (o.TryGetPropertyValue("filename", out var f) && f != null) return f.ToString();
            if (o.TryGetPropertyValue("path", out var p) && p != null) return p.ToString();
            if (o.TryGetPropertyValue("display_name", out var d) && d != null) return d.ToString();
        }
        else if (node is JsonValue v)
        {
            var s = v.ToString();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        return null;
    }

    private static List<SensorInfo>? ExtractSensorInfoFromList(JsonNode? listNode)
    {
        try
        {
            // Expected structure: {"result":{"sensors":{"sensor1":{...},"sensor2":{...}}}}
            if (listNode is JsonObject root &&
                root.TryGetPropertyValue("result", out var result) && result is JsonObject resObj &&
                resObj.TryGetPropertyValue("sensors", out var sensors) && sensors is JsonObject sensorsObj)
            {
                var sensorList = new List<SensorInfo>();
                
                foreach (var sensorKv in sensorsObj)
                {
                    var sensorId = sensorKv.Key;
                    if (sensorKv.Value is not JsonObject sensorObj) continue;

                    try
                    {
                        // Extract friendly name
                        string? friendlyName = null;
                        if (sensorObj.TryGetPropertyValue("friendly_name", out var fn))
                        {
                            friendlyName = fn?.ToString();
                        }

                        // Extract values with units from parameter_info
                        var measurements = new Dictionary<string, object>();
                        var units = new Dictionary<string, string>();

                        // First, get units from parameter_info if available
                        if (sensorObj.TryGetPropertyValue("parameter_info", out var paramInfo) && paramInfo is JsonArray paramArray)
                        {
                            foreach (var param in paramArray)
                            {
                                if (param is JsonObject paramObj)
                                {
                                    string? paramName = null;
                                    string? paramUnits = null;

                                    if (paramObj.TryGetPropertyValue("name", out var pn))
                                        paramName = pn?.ToString();
                                    if (paramObj.TryGetPropertyValue("units", out var pu))
                                        paramUnits = pu?.ToString();

                                    if (!string.IsNullOrWhiteSpace(paramName) && !string.IsNullOrWhiteSpace(paramUnits))
                                    {
                                        units[paramName] = paramUnits;
                                    }
                                }
                            }
                        }

                        // Now extract the actual values
                        if (sensorObj.TryGetPropertyValue("values", out var values) && values is JsonObject valuesObj)
                        {
                            foreach (var valueKv in valuesObj)
                            {
                                var valueName = valueKv.Key;
                                var value = valueKv.Value;

                                if (value == null) continue;

                                // Format the measurement with units if available
                                string formattedValue;
                                if (value is JsonValue jv)
                                {
                                    var strVal = jv.ToString();
                                    if (double.TryParse(strVal, out var dVal))
                                    {
                                        if (units.TryGetValue(valueName, out var unit))
                                        {
                                            formattedValue = $"{dVal:F1} {unit}";
                                        }
                                        else
                                        {
                                            formattedValue = dVal.ToString("F1");
                                        }
                                    }
                                    else
                                    {
                                        formattedValue = strVal;
                                    }
                                    measurements[valueName] = formattedValue;
                                }
                            }
                        }

                        if (measurements.Count > 0)
                        {
                            sensorList.Add(new SensorInfo(sensorId, friendlyName, measurements));
                            Console.WriteLine($"[Moonraker] Added sensor: {sensorId} ({friendlyName}) with {measurements.Count} measurements");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Moonraker] Error extracting sensor {sensorId}: {ex.Message}");
                    }
                }

                return sensorList.Count > 0 ? sensorList : null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Moonraker] Error extracting sensor data: {ex.Message}");
        }
        return null;
    }
}
