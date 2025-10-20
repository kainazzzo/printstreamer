using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PrintStreamer.Utils
{
    internal class MjpegReader
    {
        private readonly string _url;

        public MjpegReader(string url)
        {
            _url = url;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

            Console.WriteLine($"Connecting to MJPEG stream: {_url}");

            using var resp = await client.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            resp.EnsureSuccessStatusCode();

            using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);

            Console.WriteLine("Connected. Reading frames...");

            var buffer = new byte[64 * 1024];
            using var ms = new MemoryStream();
            int bytesRead;
            long totalRead = 0;
            int frameCount = 0;

            // Simple MJPEG frame extraction by searching for JPEG SOI/EOI markers
            while (!cancellationToken.IsCancellationRequested)
            {
                bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                if (bytesRead == 0) break;
                ms.Write(buffer, 0, bytesRead);
                totalRead += bytesRead;

                // Try to extract frames from ms
                while (TryExtractJpeg(ms, out var jpegBytes))
                {
                    if (jpegBytes == null) continue;
                    frameCount++;
                    Console.WriteLine($"Frame {frameCount}: {jpegBytes.Length} bytes at {DateTime.UtcNow:O}");

                    // Save every 30th frame as an example
                    if (frameCount % 30 == 0)
                    {
                        var dir = Path.Combine(Directory.GetCurrentDirectory(), "frames");
                        Directory.CreateDirectory(dir);
                        var path = Path.Combine(dir, $"frame_{frameCount}.jpg");
                        await File.WriteAllBytesAsync(path, jpegBytes, cancellationToken);
                        Console.WriteLine($"Saved {path}");
                    }
                }
            }

            Console.WriteLine($"Stream ended, total frames: {frameCount}, total bytes read: {totalRead}");
        }

        // Looks for the first JPEG (0xFFD8 ... 0xFFD9) in the memory stream.
        // If found, returns true and sets jpegBytes (and removes data up to end of JPEG from the stream).
        public static bool TryExtractJpeg(MemoryStream ms, out byte[]? jpegBytes)
        {
            jpegBytes = null;
            var buf = ms.ToArray();
            var len = buf.Length;
            int soi = -1;
            for (int i = 0; i < len - 1; i++)
            {
                if (buf[i] == 0xFF && buf[i + 1] == 0xD8)
                {
                    soi = i;
                    break;
                }
            }
            if (soi == -1) return false;

            int eoi = -1;
            for (int i = soi + 2; i < len - 1; i++)
            {
                if (buf[i] == 0xFF && buf[i + 1] == 0xD9)
                {
                    eoi = i + 1; // index of second byte of marker
                    break;
                }
            }
            if (eoi == -1) return false;

            var frameLen = eoi - soi + 1;
            jpegBytes = new byte[frameLen];
            Array.Copy(buf, soi, jpegBytes, 0, frameLen);

            // Remove consumed bytes from the MemoryStream by shifting remaining data to a fresh MemoryStream
            var remaining = len - (eoi + 1);
            ms.SetLength(0);
            if (remaining > 0)
            {
                ms.Write(buf, eoi + 1, remaining);
            }
            ms.Position = 0;
            return true;
        }
    }
}
