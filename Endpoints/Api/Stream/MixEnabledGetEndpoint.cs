using FastEndpoints;
using Microsoft.AspNetCore.Http;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Configuration;

namespace PrintStreamer.Endpoints.Api.Stream
{
    public class MixEnabledGetEndpoint : EndpointWithoutRequest<object>
    {
        private readonly IConfiguration _config;

        public MixEnabledGetEndpoint(IConfiguration config)
        {
            _config = config;
        }

        public override void Configure() { Get("/api/stream/mix-enabled"); AllowAnonymous(); }
        public override async Task HandleAsync(CancellationToken ct)
        {
            var ctx = HttpContext;
            try
            {
                var enabled = _config.GetValue<bool?>("Stream:Mix:Enabled") ?? true;
                ctx.Response.StatusCode = 200;
                await ctx.Response.WriteAsJsonAsync(new { enabled }, ct);
            }
            catch (System.Exception ex)
            {
                ctx.Response.StatusCode = 500;
                await ctx.Response.WriteAsJsonAsync(new { success = false, error = ex.Message }, ct);
            }
        }
    }
}
