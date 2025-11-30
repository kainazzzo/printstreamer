using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;
using System.Threading;
using PrintStreamer.Services;

namespace PrintStreamer.Endpoints.Api.Audio
{
    public class AudioEnabledEndpoint : EndpointWithoutRequest<object>
    {
        private readonly ILogger<AudioEnabledEndpoint> _logger;
        private readonly IConfiguration _config;
        private readonly StreamService _streamService;
        private readonly AudioBroadcastService? _broadcaster;
        public AudioEnabledEndpoint(ILogger<AudioEnabledEndpoint> logger, IConfiguration config, StreamService streamService, AudioBroadcastService? broadcaster = null) { _logger = logger; _config = config; _streamService = streamService; _broadcaster = broadcaster; }
        public override void Configure() { Post("/api/audio/enabled"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct)
        {
            var ctx = HttpContext;
            var raw = ctx.Request.Query["enabled"].ToString();
            bool enabled;
            if (!bool.TryParse(raw, out enabled)) enabled = raw == "1" || string.Equals(raw, "true", System.StringComparison.OrdinalIgnoreCase);
            _config["Audio:Enabled"] = enabled.ToString();
            _logger.LogInformation("Audio stream: {State}", enabled ? "enabled" : "disabled");
            try
            {
                if (_streamService.IsStreaming)
                {
                    _logger.LogInformation("Restarting active stream to pick up audio setting change");
                    await _streamService.StopStreamAsync();
                    await _streamService.StartStreamAsync(null, ct);
                }
            }
            catch (System.Exception ex) { _logger.LogError(ex, "Failed to restart stream after audio toggle"); }

            try
            {
                _broadcaster?.ApplyAudioEnabledState(enabled);
            }
            catch (System.Exception ex) { _logger.LogError(ex, "Failed to apply audio toggle to broadcaster"); }

            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsJsonAsync(new { success = true, enabled }, ct);
        }
    }
}
