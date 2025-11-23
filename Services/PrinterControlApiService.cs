using System.Net.Http.Json;

namespace PrintStreamer.Services
{
    /// <summary>
    /// Client-side service for the printer control popout component
    /// Handles HTTP communication with the PrinterControlController API
    /// </summary>
    public class PrinterControlApiService
    {
        private readonly HttpClient _http;
        private readonly ILogger<PrinterControlApiService> _logger;

        public PrinterControlApiService(HttpClient http, ILogger<PrinterControlApiService> logger)
        {
            _http = http;
            _logger = logger;
        }

        /// <summary>
        /// Get printer configuration (max temps, rate limits, etc.)
        /// </summary>
        public async Task<PrinterConfigResponse?> GetConfigAsync(CancellationToken ct = default)
        {
            try
            {
                return await _http.GetFromJsonAsync<PrinterConfigResponse>("/api/printer/config", ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting printer config");
                return null;
            }
        }

        /// <summary>
        /// Set tool/nozzle temperature
        /// </summary>
        public async Task<ApiResponse?> SetToolTemperatureAsync(int temperature, int toolIndex = 0, CancellationToken ct = default)
        {
            try
            {
                var url = $"/api/printer/temperature/tool?temperature={temperature}&toolIndex={toolIndex}";
                var response = await _http.PostAsync(url, null, ct);
                return await response.Content.ReadFromJsonAsync<ApiResponse>(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting tool temperature");
                return null;
            }
        }

        /// <summary>
        /// Set bed temperature
        /// </summary>
        public async Task<ApiResponse?> SetBedTemperatureAsync(int temperature, CancellationToken ct = default)
        {
            try
            {
                var url = $"/api/printer/temperature/bed?temperature={temperature}";
                var response = await _http.PostAsync(url, null, ct);
                return await response.Content.ReadFromJsonAsync<ApiResponse>(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting bed temperature");
                return null;
            }
        }

        /// <summary>
        /// Set both tool and bed temperatures
        /// </summary>
        public async Task<ApiResponse?> SetTemperaturesAsync(int? toolTemp, int? bedTemp, int toolIndex = 0, CancellationToken ct = default)
        {
            try
            {
                var url = $"/api/printer/temperature/set";
                var queryParams = new List<string>();
                if (toolTemp.HasValue) queryParams.Add($"toolTemp={toolTemp.Value}");
                if (bedTemp.HasValue) queryParams.Add($"bedTemp={bedTemp.Value}");
                queryParams.Add($"toolIndex={toolIndex}");
                
                if (queryParams.Count > 0)
                    url += "?" + string.Join("&", queryParams);

                var response = await _http.PostAsync(url, null, ct);
                return await response.Content.ReadFromJsonAsync<ApiResponse>(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting temperatures");
                return null;
            }
        }

        /// <summary>
        /// Apply a material preset (PLA, PETG, ABS, etc.)
        /// </summary>
        public async Task<ApiResponse?> ApplyPresetAsync(string preset, CancellationToken ct = default)
        {
            try
            {
                var url = $"/api/printer/temperature/preset?preset={Uri.EscapeDataString(preset)}";
                var response = await _http.PostAsync(url, null, ct);
                return await response.Content.ReadFromJsonAsync<ApiResponse>(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying preset");
                return null;
            }
        }

        /// <summary>
        /// Send a G-code command
        /// </summary>
        public async Task<GcodeResponse?> SendGcodeAsync(string command, bool confirmed = false, CancellationToken ct = default)
        {
            try
            {
                var url = $"/api/printer/gcode/send?command={Uri.EscapeDataString(command)}&confirmed={confirmed}";
                var response = await _http.PostAsync(url, null, ct);
                return await response.Content.ReadFromJsonAsync<GcodeResponse>(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending G-code");
                return null;
            }
        }

        /// <summary>
        /// Get recent console lines
        /// </summary>
        public async Task<ConsoleLinesResponse?> GetConsoleLinesAsync(int maxLines = 100, CancellationToken ct = default)
        {
            try
            {
                var url = $"/api/printer/console/lines?maxLines={maxLines}";
                return await _http.GetFromJsonAsync<ConsoleLinesResponse>(url, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting console lines");
                return null;
            }
        }

        /// <summary>
        /// Get G-code history for rendering
        /// </summary>
        public async Task<GcodeHistoryResponse?> GetGcodeHistoryAsync(int maxLines = 1000, CancellationToken ct = default)
        {
            try
            {
                var url = $"/api/printer/gcode/history?maxLines={maxLines}";
                return await _http.GetFromJsonAsync<GcodeHistoryResponse>(url, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting G-code history");
                return null;
            }
        }

        /// <summary>
        /// Get G-code bounds for optimization
        /// </summary>
        public async Task<GcodeBoundsResponse?> GetGcodeBoundsAsync(int maxLines = 1000, CancellationToken ct = default)
        {
            try
            {
                var url = $"/api/printer/gcode/bounds?maxLines={maxLines}";
                return await _http.GetFromJsonAsync<GcodeBoundsResponse>(url, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting G-code bounds");
                return null;
            }
        }

        /// <summary>
        /// Get printer connection status
        /// </summary>
        public async Task<StatusResponse?> GetStatusAsync(CancellationToken ct = default)
        {
            try
            {
                return await _http.GetFromJsonAsync<StatusResponse>("/api/printer/status", ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting printer status");
                return null;
            }
        }

        /// <summary>
        /// Get reprint info: whether the printer is currently printing and the last completed filename
        /// </summary>
        public async Task<ReprintInfoResponse?> GetReprintInfoAsync(CancellationToken ct = default)
        {
            try
            {
                return await _http.GetFromJsonAsync<ReprintInfoResponse>($"/api/printer/reprint/info", ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting reprint info");
                return null;
            }
        }

        /// <summary>
        /// Request to reprint the last completed job (best-effort server action)
        /// </summary>
        public async Task<ApiResponse?> ReprintLastAsync(CancellationToken ct = default)
        {
            try
            {
                var response = await _http.PostAsync($"/api/printer/reprint", null, ct);
                return await response.Content.ReadFromJsonAsync<ApiResponse>(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reprinting last job");
                return null;
            }
        }
    }

    // Response DTOs
    public class PrinterConfigResponse
    {
        public int ToolMaxTemp { get; set; }
        public int BedMaxTemp { get; set; }
        public RateLimitInfo RateLimit { get; set; } = new();
    }

    public class RateLimitInfo
    {
        public int CommandsPerMinute { get; set; }
    }

    public class ApiResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("success")]
        public bool Success { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("message")]
        public string? Message { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("error")]
        public string? Error { get; set; }
        public int? Temperature { get; set; }
        public string? Preset { get; set; }
        public int? ToolTemp { get; set; }
        public int? BedTemp { get; set; }
    }

    public class GcodeResponse
    {
        public bool Success { get; set; }
        public bool ConfirmationRequired { get; set; }
        public string? Message { get; set; }
        public string? Error { get; set; }
        public string? Command { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ConsoleLineDto
    {
        public DateTime Timestamp { get; set; }
        public string Text { get; set; } = string.Empty;
        public string Level { get; set; } = "info";
        public bool FromLocal { get; set; }
    }

    public class ConsoleLinesResponse
    {
        public List<ConsoleLineDto> Lines { get; set; } = new();
    }

    public class GcodeHistoryResponse
    {
        public List<string> Commands { get; set; } = new();
        public int Count { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class GcodeBoundsResponse
    {
        public bool HasData { get; set; }
        public GcodeBoundsInfo? Bounds { get; set; }
        public GcodeDimensions? Dimensions { get; set; }
    }

    public class GcodeBoundsInfo
    {
        public double MinX { get; set; }
        public double MaxX { get; set; }
        public double MinY { get; set; }
        public double MaxY { get; set; }
    }

    public class GcodeDimensions
    {
        public double Width { get; set; }
        public double Height { get; set; }
    }

    public class StatusResponse
    {
        public bool Connected { get; set; }
        public string? LastMessage { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ReprintInfoResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("isPrinting")]
        public bool IsPrinting { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("lastCompletedFilename")]
        public string? LastCompletedFilename { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("currentFilename")]
        public string? CurrentFilename { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("isPrintingInProgress")]
        public bool IsPrintingInProgress { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("isError")]
        public bool IsError { get; set; }
    }
}
