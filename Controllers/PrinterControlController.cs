using Microsoft.AspNetCore.Mvc;
using PrintStreamer.Services;
using System.Text.Json.Serialization;

namespace PrintStreamer.Controllers
{
    /// <summary>
    /// Printer Control API endpoints for the popout component
    /// Handles temperature control, G-code command sending, and rendering data
    /// </summary>
    [ApiController]
    [Route("api/printer")]
    public class PrinterControlController : ControllerBase
    {
        private readonly PrinterConsoleService _console;
        private readonly IConfiguration _config;
        private readonly ILogger<PrinterControlController> _logger;

        public PrinterControlController(
            PrinterConsoleService console, 
            IConfiguration config, 
            ILogger<PrinterControlController> logger)
        {
            _console = console;
            _config = config;
            _logger = logger;
        }

        /// <summary>
        /// Get current printer temperature constraints and configuration
        /// </summary>
        [HttpGet("config")]
        public IActionResult GetPrinterConfig()
        {
            try
            {
                // Prefer new Macros:Temperature config, fallback to legacy Stream:Console values for compatibility
                var toolMaxTemp = _config.GetValue<int?>("Macros:Temperature:ToolMaxTemp")
                    ?? _config.GetValue<int?>("Stream:Console:ToolMaxTemp") ?? 350;
                var bedMaxTemp = _config.GetValue<int?>("Macros:Temperature:BedMaxTemp")
                    ?? _config.GetValue<int?>("Stream:Console:BedMaxTemp") ?? 120;

                var rateLimit = new RateLimitInfo
                {
                    CommandsPerMinute = _config.GetValue<int?>("Stream:Console:RateLimit:CommandsPerMinute") ?? 0
                };

                var resp = new PrinterConfigResponse
                {
                    ToolMaxTemp = toolMaxTemp,
                    BedMaxTemp = bedMaxTemp,
                    RateLimit = rateLimit
                };

                return Ok(resp);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting printer config");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Set tool/nozzle temperature
        /// </summary>
        [HttpPost("temperature/tool")]
        public async Task<IActionResult> SetToolTemperature([FromQuery] int temperature, [FromQuery] int toolIndex = 0)
        {
            try
            {
                if (temperature < 0 || temperature > 350)
                    return BadRequest(new { error = "Temperature out of valid range (0-350°C)" });

                var result = await _console.SetToolTemperatureAsync(toolIndex, temperature);
                
                if (result.Ok)
                    return Ok(new { success = true, message = result.Message, temperature });
                
                return BadRequest(new { error = result.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting tool temperature");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Set bed temperature
        /// </summary>
        [HttpPost("temperature/bed")]
        public async Task<IActionResult> SetBedTemperature([FromQuery] int temperature)
        {
            try
            {
                if (temperature < 0 || temperature > 120)
                    return BadRequest(new { error = "Temperature out of valid range (0-120°C)" });

                var result = await _console.SetBedTemperatureAsync(temperature);
                
                if (result.Ok)
                    return Ok(new { success = true, message = result.Message, temperature });
                
                return BadRequest(new { error = result.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting bed temperature");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Set both tool and bed temperatures at once
        /// </summary>
        [HttpPost("temperature/set")]
        public async Task<IActionResult> SetTemperatures(
            [FromQuery] int? toolTemp, 
            [FromQuery] int? bedTemp,
            [FromQuery] int toolIndex = 0)
        {
            try
            {
                if (!toolTemp.HasValue && !bedTemp.HasValue)
                    return BadRequest(new { error = "At least one temperature must be specified" });

                var result = await _console.SetTemperaturesAsync(toolTemp, bedTemp, toolIndex);
                
                if (result.Ok)
                    return Ok(new { success = true, message = result.Message, toolTemp, bedTemp });
                
                return BadRequest(new { error = result.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting temperatures");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Apply a material preset (PLA, PETG, ABS, etc.)
        /// </summary>
        [HttpPost("temperature/preset")]
        public async Task<IActionResult> ApplyPreset([FromQuery] string preset)
        {
            try
            {
                int toolTemp = 0, bedTemp = 0;
                // First attempt to load presets from configuration
                try
                {
                    var presets = _config.GetSection("Macros:Temperature:Presets").Get<List<TemperaturePreset>>();
                    if (presets != null)
                    {
                        var match = presets.FirstOrDefault(p => string.Equals(p.Name, preset, StringComparison.OrdinalIgnoreCase));
                        if (match != null)
                        {
                            toolTemp = match.ToolTemp;
                            bedTemp = match.BedTemp;
                        }
                    }
                }
                catch { /* ignore config parsing errors and fallback to built-in mapping */ }

                // Fallback to built-in mapping when no configured preset found
                if (toolTemp == 0 && bedTemp == 0)
                {
                    (toolTemp, bedTemp) = preset.ToLowerInvariant() switch
                    {
                        "pla" => (200, 60),
                        "petg" => (240, 70),
                        "abs" => (250, 100),
                        "tpu" => (220, 60),
                        "nylon" => (250, 85),
                        "cooldown" => (0, 0),
                        _ => (0, 0)
                    };
                }

                var result = await _console.SetTemperaturesAsync(toolTemp, bedTemp);
                
                if (result.Ok)
                    return Ok(new { success = true, message = result.Message, preset, toolTemp, bedTemp });
                
                return BadRequest(new { error = result.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying preset");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Send a raw G-code command to the printer
        /// </summary>
        [HttpPost("gcode/send")]
        public async Task<IActionResult> SendGcode([FromQuery] string command, [FromQuery] bool confirmed = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(command))
                    return BadRequest(new { error = "Command cannot be empty" });

                var result = await _console.SendCommandAsync(command, confirmed);
                
                if (result.Ok)
                    return Ok(new 
                    { 
                        success = true, 
                        message = result.Message, 
                        command = result.SentCommand,
                        timestamp = result.Timestamp
                    });
                
                // Check if confirmation is required
                if (result.Message == "confirmation-required")
                    return Ok(new 
                    { 
                        success = false, 
                        confirmationRequired = true, 
                        message = "This command requires confirmation",
                        command = result.SentCommand 
                    });
                
                return BadRequest(new { error = result.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending G-code");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Get recent console lines for display/debugging
        /// </summary>
        [HttpGet("console/lines")]
        public IActionResult GetConsoleLines([FromQuery] int maxLines = 100)
        {
            try
            {
                var lines = _console.GetLatestLines(maxLines)
                    .Select(l => new
                    {
                        l.Timestamp,
                        l.Text,
                        l.Level,
                        l.FromLocal
                    })
                    .ToList();

                return Ok(new { lines });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting console lines");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Get G-code commands from the console for rendering the preview
        /// Filters for sent commands that contain G-code
        /// </summary>
        [HttpGet("gcode/history")]
        public IActionResult GetGcodeHistory([FromQuery] int maxLines = 1000)
        {
            try
            {
                var allLines = _console.GetLatestLines(maxLines);
                var gcodeCommands = new List<string>();

                foreach (var consoleLine in allLines)
                {
                    var text = consoleLine.Text.Trim();
                    // Look for sent commands in the console
                    if (consoleLine.FromLocal && text.StartsWith("Sent:"))
                    {
                        var cmd = text.Substring(5).Trim();
                        if (!string.IsNullOrEmpty(cmd) && IsGcodeCommand(cmd))
                        {
                            gcodeCommands.Add(cmd);
                        }
                    }
                }

                return Ok(new
                {
                    commands = gcodeCommands,
                    count = gcodeCommands.Count,
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting G-code history");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Get render bounds for the current G-code history
        /// Used to optimize the mini renderer view
        /// </summary>
        [HttpGet("gcode/bounds")]
        public IActionResult GetGcodeBounds([FromQuery] int maxLines = 1000)
        {
            try
            {
                var allLines = _console.GetLatestLines(maxLines);
                double minX = 0, maxX = 0, minY = 0, maxY = 0;
                var hasData = false;

                foreach (var consoleLine in allLines)
                {
                    var text = consoleLine.Text.Trim();
                    if (consoleLine.FromLocal && text.StartsWith("Sent:"))
                    {
                        var cmd = text.Substring(5).Trim();
                        if (IsGcodeCommand(cmd))
                        {
                            var (x, y) = ParseGcodeCoordinates(cmd);
                            if (x.HasValue)
                            {
                                if (!hasData)
                                {
                                    minX = maxX = x.Value;
                                    minY = maxY = y.GetValueOrDefault(0);
                                    hasData = true;
                                }
                                else
                                {
                                    minX = Math.Min(minX, x.Value);
                                    maxX = Math.Max(maxX, x.Value);
                                    minY = Math.Min(minY, y.GetValueOrDefault(minY));
                                    maxY = Math.Max(maxY, y.GetValueOrDefault(maxY));
                                }
                            }
                        }
                    }
                }

                return Ok(new
                {
                    hasData,
                    bounds = hasData ? new { minX, maxX, minY, maxY } : null,
                    dimensions = hasData ? new 
                    { 
                        width = maxX - minX,
                        height = maxY - minY
                    } : null
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting G-code bounds");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Get health status of the printer connection
        /// </summary>
        [HttpGet("status")]
        public IActionResult GetStatus()
        {
            try
            {
                var lines = _console.GetLatestLines(5);
                var lastMessage = lines.LastOrDefault()?.Text ?? "No recent messages";

                return Ok(new
                {
                    connected = lines.Count > 0,
                    lastMessage,
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting printer status");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Get current temperatures from the printer (from cached WebSocket data)
        /// </summary>
        [HttpGet("temperatures")]
        public IActionResult GetTemperatures()
        {
            try
            {
                var (toolTemp, bedTemp) = _console.GetCurrentTemperatures();
                return Ok(new
                {
                    toolTemp,
                    bedTemp,
                    timestamp = DateTime.UtcNow
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting temperatures");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        // Private helpers

        private static bool IsGcodeCommand(string cmd)
        {
            var upper = cmd.ToUpperInvariant();
            // Check if it starts with a recognized G-code command
            return upper.StartsWith("G") || upper.StartsWith("M") || 
                   upper.StartsWith("T") || upper.StartsWith("N");
        }

        private static (double? x, double? y) ParseGcodeCoordinates(string cmd)
        {
            double? x = null, y = null;

            // Extract X coordinate
            var xMatch = System.Text.RegularExpressions.Regex.Match(cmd, @"X([-\d.]+)", 
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (xMatch.Success && double.TryParse(xMatch.Groups[1].Value, out var xVal))
                x = xVal;

            // Extract Y coordinate
            var yMatch = System.Text.RegularExpressions.Regex.Match(cmd, @"Y([-\d.]+)", 
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (yMatch.Success && double.TryParse(yMatch.Groups[1].Value, out var yVal))
                y = yVal;

            return (x, y);
        }
    }

    /// <summary>
    /// Response model for temperature presets
    /// </summary>
    public class TemperaturePreset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("toolTemp")]
        public int ToolTemp { get; set; }

        [JsonPropertyName("bedTemp")]
        public int BedTemp { get; set; }
    }
}
