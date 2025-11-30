using FastEndpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PrintStreamer.Endpoints.Api.Proxy
{
    public class FallbackBlackEndpoint : EndpointWithoutRequest<object>
    {
        private readonly ILogger<FallbackBlackEndpoint> _logger;

        public FallbackBlackEndpoint(ILogger<FallbackBlackEndpoint> logger)
        {
            _logger = logger;
        }

        public override void Configure()
        {
            Get("/fallback_black.jpg");
            AllowAnonymous();
        }

        public override async Task HandleAsync(CancellationToken ct)
        {
            var fallbackPath = Path.Combine(Directory.GetCurrentDirectory(), "fallback_black.jpg");
            if (File.Exists(fallbackPath))
            {
                HttpContext.Response.ContentType = "image/jpeg";
                await HttpContext.Response.SendFileAsync(fallbackPath, ct);
            }
            else
            {
                HttpContext.Response.StatusCode = 404;
                await HttpContext.Response.WriteAsync("Fallback image not found", ct);
            }
        }
    }
}
