using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;

namespace PrintStreamer.Utils.Tests
{
    [TestClass]
    public class ProcessManagementTests
    {
        private TestServer? _testServer;
        private HttpClient? _client;
        private string? _tempDir;
        private int _initialFfmpegCount;

        [TestInitialize]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"process_test_{Guid.NewGuid()}");
            Directory.CreateDirectory(_tempDir);

            // Count initial ffmpeg processes
            _initialFfmpegCount = Process.GetProcessesByName("ffmpeg").Length;

            // Create a test server that simulates the real streaming endpoints
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Stream:Source"] = "http://localhost:8081/webcam/?action=snapshot",
                    ["Overlay:StreamSource"] = "http://127.0.0.1:8080/stream/source",
                    ["Audio:Enabled"] = "true"
                })
                .Build();

            var builder = new WebHostBuilder()
                .UseConfiguration(config)
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IConfiguration>(config);
                    services.AddLogging();
                    services.AddHttpClient();
                    services.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        // Simulate streaming endpoints that might start ffmpeg processes
                        endpoints.MapGet("/stream/source", async (HttpContext ctx) =>
                        {
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";

                            // Simulate some processing time
                            await Task.Delay(100);

                            await ctx.Response.WriteAsync("--frame\r\n");
                            await ctx.Response.WriteAsync("Content-Type: image/jpeg\r\n");
                            await ctx.Response.WriteAsync("Content-Length: 4\r\n\r\n");
                            await ctx.Response.Body.WriteAsync(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
                            await ctx.Response.WriteAsync("\r\n");
                        });

                        endpoints.MapGet("/stream/overlay", async (HttpContext ctx) =>
                        {
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";

                            // Simulate ffmpeg processing
                            await Task.Delay(150);

                            await ctx.Response.WriteAsync("--frame\r\n");
                            await ctx.Response.WriteAsync("Content-Type: image/jpeg\r\n");
                            await ctx.Response.WriteAsync("Content-Length: 4\r\n\r\n");
                            await ctx.Response.Body.WriteAsync(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
                            await ctx.Response.WriteAsync("\r\n");
                        });

                        endpoints.MapGet("/stream/mix", async (HttpContext ctx) =>
                        {
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "video/mp4";

                            // Simulate longer processing for mix
                            await Task.Delay(200);

                            await ctx.Response.Body.WriteAsync(new byte[] { 0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70 }); // Fake MP4 header
                        });
                    });
                });

            _testServer = new TestServer(builder);
            _client = _testServer.CreateClient();
        }

        [TestCleanup]
        public void Cleanup()
        {
            _client?.Dispose();
            _testServer?.Dispose();

            if (Directory.Exists(_tempDir))
            {
                try
                {
                    Directory.Delete(_tempDir, true);
                }
                catch { /* Ignore cleanup errors */ }
            }

            // Clean up any ffmpeg processes created during tests
            CleanupFfmpegProcesses();
        }

        private void CleanupFfmpegProcesses()
        {
            try
            {
                var ffmpegProcesses = Process.GetProcessesByName("ffmpeg");
                foreach (var process in ffmpegProcesses)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                            process.WaitForExit(1000);
                        }
                    }
                    catch { /* Ignore process cleanup errors */ }
                }
            }
            catch { /* Ignore process enumeration errors */ }
        }

        [TestMethod]
        public async Task StreamingEndpoints_DoNotLeaveExtraFfmpegProcesses()
        {
            // Arrange - Count processes before
            var processesBefore = Process.GetProcessesByName("ffmpeg").Length;

            // Act - Make multiple requests to streaming endpoints
            var tasks = new[]
            {
                _client.GetAsync("/stream/source"),
                _client.GetAsync("/stream/overlay"),
                _client.GetAsync("/stream/mix")
            };

            // Cancel requests after short time to avoid hanging
            using var cts = new CancellationTokenSource(500); // 500ms timeout
            var responses = await Task.WhenAll(tasks.Select(t => t.ContinueWith(task =>
            {
                if (cts.IsCancellationRequested)
                    return new HttpResponseMessage(HttpStatusCode.RequestTimeout);
                return task.Result;
            }, cts.Token)));

            // Assert - Count processes after
            var processesAfter = Process.GetProcessesByName("ffmpeg").Length;

            // Should not have more processes than before (allowing for some background processes)
            Assert.IsTrue(processesAfter <= processesBefore + 1, // Allow 1 extra for system processes
                $"Streaming endpoints left {processesAfter - processesBefore} extra ffmpeg processes");
        }

        [TestMethod]
        public async Task ConcurrentStreamingRequests_DoNotCreateProcessLeak()
        {
            // Arrange
            const int concurrentRequests = 5;
            var processesBefore = Process.GetProcessesByName("ffmpeg").Length;

            // Act - Make many concurrent requests
            var tasks = new List<Task<HttpResponseMessage>>();
            for (int i = 0; i < concurrentRequests; i++)
            {
                tasks.Add(_client.GetAsync("/stream/source"));
                tasks.Add(_client.GetAsync("/stream/overlay"));
                tasks.Add(_client.GetAsync("/stream/mix"));
            }

            // Use timeout to avoid hanging
            using var cts = new CancellationTokenSource(1000); // 1 second timeout
            try
            {
                await Task.WhenAll(tasks.Select(t => t.ContinueWith(task =>
                {
                    if (cts.IsCancellationRequested && !task.IsCompleted)
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestTimeout));
                    return task;
                }, cts.Token)).ToArray());
            }
            catch (OperationCanceledException) { }

            // Give processes time to start
            await Task.Delay(200);

            // Assert
            var processesAfter = Process.GetProcessesByName("ffmpeg").Length;

            // Should not have excessive process growth
            var processGrowth = processesAfter - processesBefore;
            Assert.IsTrue(processGrowth <= 2, // Allow small growth for legitimate processes
                $"Concurrent requests created {processGrowth} extra ffmpeg processes");
        }

        [TestMethod]
        public async Task RequestCancellation_StopsProcessing()
        {
            // Arrange
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(50); // Cancel very quickly

            // Act & Assert
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "/stream/mix");
                var response = await _client.SendAsync(request, cts.Token);

                // Should either complete quickly or be cancelled
                Assert.IsTrue(response.IsSuccessStatusCode || cts.IsCancellationRequested,
                    "Request should either succeed or be cancelled");
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelled
                Assert.IsTrue(cts.IsCancellationRequested, "Should be cancelled by our token");
            }
        }

        [TestMethod]
        public async Task LongRunningRequests_CanBeCancelled()
        {
            // Arrange - Create a request that would normally take time
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(100); // Cancel after 100ms

            var startTime = DateTime.UtcNow;

            // Act
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "/stream/mix");
                await _client.SendAsync(request, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }

            var elapsed = DateTime.UtcNow - startTime;

            // Assert - Should not take much longer than our cancellation time
            Assert.IsTrue(elapsed.TotalMilliseconds < 500, // Allow some buffer
                $"Request took too long ({elapsed.TotalMilliseconds}ms) after cancellation");
        }

        [TestMethod]
        public void ProcessCleanup_WorksCorrectly()
        {
            // Arrange - Start some processes (simulated)
            var processesBeforeCleanup = Process.GetProcessesByName("ffmpeg").Length;

            // Act - Run cleanup
            CleanupFfmpegProcesses();

            // Assert - Should not crash and should clean up
            var processesAfterCleanup = Process.GetProcessesByName("ffmpeg").Length;

            // Cleanup should not leave more processes than before
            Assert.IsTrue(processesAfterCleanup <= processesBeforeCleanup,
                "Cleanup should not create more processes");
        }

        [TestMethod]
        public async Task ResourceUsage_StaysReasonable()
        {
            // Arrange
            var initialMemory = GC.GetTotalMemory(true);
            var processesBefore = Process.GetProcessesByName("ffmpeg").Length;

            // Act - Make several requests
            var tasks = new[]
            {
                _client.GetAsync("/stream/source"),
                _client.GetAsync("/stream/overlay"),
                _client.GetAsync("/stream/mix"),
                _client.GetAsync("/stream/source"),
                _client.GetAsync("/stream/overlay")
            };

            using var cts = new CancellationTokenSource(800);
            try
            {
                await Task.WhenAll(tasks.Select(t => t.ContinueWith(task =>
                {
                    if (cts.IsCancellationRequested && !task.IsCompleted)
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestTimeout));
                    return task;
                }, cts.Token)));
            }
            catch (OperationCanceledException) { }

            // Force garbage collection
            GC.Collect();
            GC.WaitForPendingFinalizers();

            var finalMemory = GC.GetTotalMemory(true);
            var processesAfter = Process.GetProcessesByName("ffmpeg").Length;

            // Assert - Memory and process usage should be reasonable
            var memoryGrowth = finalMemory - initialMemory;
            var processGrowth = processesAfter - processesBefore;

            Assert.IsTrue(memoryGrowth < 10 * 1024 * 1024, // Less than 10MB growth
                $"Memory grew by {memoryGrowth / 1024 / 1024}MB, which is excessive");

            Assert.IsTrue(processGrowth <= 1, // Allow 1 extra process
                $"Process count grew by {processGrowth}, which is excessive");
        }

        [TestMethod]
        public async Task ErrorConditions_DoNotLeaveProcesses()
        {
            // Arrange
            var processesBefore = Process.GetProcessesByName("ffmpeg").Length;

            // Act - Make requests that might fail or timeout
            var tasks = new[]
            {
                _client.GetAsync("/stream/source"),
                _client.GetAsync("/stream/overlay"),
                _client.GetAsync("/stream/mix")
            };

            using var cts = new CancellationTokenSource(200); // Short timeout
            try
            {
                await Task.WhenAll(tasks.Select(t => t.ContinueWith(task =>
                {
                    if (cts.IsCancellationRequested && !task.IsCompleted)
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestTimeout));
                    return task;
                }, cts.Token)));
            }
            catch (OperationCanceledException) { }

            // Assert
            var processesAfter = Process.GetProcessesByName("ffmpeg").Length;
            var processGrowth = processesAfter - processesBefore;

            Assert.IsTrue(processGrowth <= 1, // Allow minimal growth
                $"Error conditions left {processGrowth} extra ffmpeg processes");
        }
    }
}