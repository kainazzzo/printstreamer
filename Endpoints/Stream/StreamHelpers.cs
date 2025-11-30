using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;
using System;

namespace PrintStreamer.Endpoints.Stream
{
    public static class StreamHelpers
    {
        public static async Task StreamSilentAudioAsync(HttpContext ctx, ILogger logger, CancellationToken ct)
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers["Content-Type"] = "audio/mpeg";
            ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
            ctx.Response.Headers["Pragma"] = "no-cache";
            await ctx.Response.Body.FlushAsync(ct);

            Process? proc = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = "-hide_banner -loglevel error -f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100 -c:a libmp3lame -b:a 128k -f mp3 -",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                proc = Process.Start(psi);
                if (proc == null)
                {
                    ctx.Response.StatusCode = 500;
                    await ctx.Response.WriteAsync("Silent audio unavailable", ct);
                    return;
                }

                using var reg = ct.Register(() =>
                {
                    try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                });

                var buffer = new byte[16 * 1024];
                var stdout = proc.StandardOutput.BaseStream;
                while (!ct.IsCancellationRequested)
                {
                    var read = await stdout.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                    if (read <= 0) break;
                    await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.LogError(ex, "Silent audio stream error");
            }
            finally
            {
                try { if (proc != null && !proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                try { proc?.Dispose(); } catch { }
            }
        }
    }
}
