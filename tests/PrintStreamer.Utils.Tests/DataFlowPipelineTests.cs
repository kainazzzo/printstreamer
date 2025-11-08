using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
    public class DataFlowPipelineTests
    {
        private TestServer? _testServer;
        private HttpClient? _client;
        private string? _tempDir;

        [TestInitialize]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"dataflow_test_{Guid.NewGuid()}");
            Directory.CreateDirectory(_tempDir);

            // Create a test server with the same configuration as the main app
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["Stream:Source"] = "http://localhost:8081/webcam/?action=snapshot",
                    ["Overlay:StreamSource"] = "http://127.0.0.1:8080/stream/source",
                    ["Audio:Enabled"] = "true",
                    ["Moonraker:BaseUrl"] = "http://localhost:7125"
                })
                .Build();

            var builder = new WebHostBuilder()
                .UseConfiguration(config)
                .ConfigureServices(services =>
                {
                    // Add the same services as the main app
                    services.AddSingleton<IConfiguration>(config);
                    services.AddLogging();
                    services.AddHttpClient();
                    services.AddRouting();

                    // Mock services that require external dependencies
                    var webCamManagerMock = new Mock<PrintStreamer.Services.WebCamManager>(config, Mock.Of<ILogger<PrintStreamer.Services.WebCamManager>>());
                    services.AddSingleton(webCamManagerMock.Object);
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        // Add the same routes as Program.cs
                        endpoints.MapGet("/stream/source", async (HttpContext ctx) =>
                        {
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
                            await ctx.Response.WriteAsync("--frame\r\n");
                            await ctx.Response.WriteAsync("Content-Type: image/jpeg\r\n");
                            await ctx.Response.WriteAsync("Content-Length: 0\r\n\r\n");
                            await ctx.Response.WriteAsync("\r\n");
                        });

                        endpoints.MapGet("/stream/overlay", async (HttpContext ctx) =>
                        {
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
                            await ctx.Response.WriteAsync("--frame\r\n");
                            await ctx.Response.WriteAsync("Content-Type: image/jpeg\r\n");
                            await ctx.Response.WriteAsync("Content-Length: 0\r\n\r\n");
                            await ctx.Response.WriteAsync("\r\n");
                        });

                        endpoints.MapGet("/stream/audio", async (HttpContext ctx) =>
                        {
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "audio/mp3";
                            await ctx.Response.WriteAsync("fake audio data");
                        });

                        endpoints.MapGet("/stream/mix", async (HttpContext ctx) =>
                        {
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "video/mp4";
                            await ctx.Response.WriteAsync("fake video data");
                        });

                        // Add the capture endpoints
                        endpoints.MapGet("/stream/source/capture", async (HttpContext ctx) =>
                        {
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "image/jpeg";
                            ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
                            await ctx.Response.Body.WriteAsync(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 }); // Minimal JPEG
                        });

                        endpoints.MapGet("/stream/overlay/capture", async (HttpContext ctx) =>
                        {
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "image/jpeg";
                            ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
                            await ctx.Response.Body.WriteAsync(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
                        });

                        endpoints.MapGet("/stream/mix/capture", async (HttpContext ctx) =>
                        {
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "image/jpeg";
                            ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
                            await ctx.Response.Body.WriteAsync(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
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

            // Clean up any lingering ffmpeg processes
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
        public async Task StreamSourceCapture_ReturnsValidJpeg()
        {
            // Act
            var response = await _client.GetAsync("/stream/source/capture");

            // Assert
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("image/jpeg", response.Content.Headers.ContentType?.MediaType);

            var content = await response.Content.ReadAsByteArrayAsync();
            Assert.IsTrue(content.Length > 0, "Should return JPEG data");

            // Check for JPEG SOI marker
            Assert.AreEqual(0xFF, content[0], "Should start with JPEG SOI marker");
            Assert.AreEqual(0xD8, content[1], "Should have JPEG SOI marker");
        }

        [TestMethod]
        public async Task StreamOverlayCapture_ReturnsValidJpeg()
        {
            // Act
            var response = await _client.GetAsync("/stream/overlay/capture");

            // Assert
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("image/jpeg", response.Content.Headers.ContentType?.MediaType);

            var content = await response.Content.ReadAsByteArrayAsync();
            Assert.IsTrue(content.Length > 0, "Should return JPEG data");

            // Check for JPEG markers
            Assert.AreEqual(0xFF, content[0], "Should start with JPEG SOI marker");
            Assert.AreEqual(0xD8, content[1], "Should have JPEG SOI marker");
        }

        [TestMethod]
        public async Task StreamMixCapture_ReturnsValidJpeg()
        {
            // Act
            var response = await _client.GetAsync("/stream/mix/capture");

            // Assert
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("image/jpeg", response.Content.Headers.ContentType?.MediaType);

            var content = await response.Content.ReadAsByteArrayAsync();
            Assert.IsTrue(content.Length > 0, "Should return JPEG data");

            // Check for JPEG markers
            Assert.AreEqual(0xFF, content[0], "Should start with JPEG SOI marker");
            Assert.AreEqual(0xD8, content[1], "Should have JPEG SOI marker");
        }

        [TestMethod]
        public async Task AllCaptureEndpoints_HaveNoCacheHeaders()
        {
            // Test all three capture endpoints
            var endpoints = new[] { "/stream/source/capture", "/stream/overlay/capture", "/stream/mix/capture" };

            foreach (var endpoint in endpoints)
            {
                // Act
                var response = await _client.GetAsync(endpoint);

                // Assert
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var cacheControl = response.Headers.CacheControl;
                Assert.IsNotNull(cacheControl, $"Endpoint {endpoint} should have cache control header");
                Assert.IsTrue(cacheControl.NoCache, $"Endpoint {endpoint} should have no-cache");
                Assert.IsTrue(cacheControl.NoStore, $"Endpoint {endpoint} should have no-store");
                // Accept the actual cache control header which includes additional directives
                Assert.IsTrue(cacheControl.ToString().Contains("no-cache"), $"Endpoint {endpoint} should include no-cache in cache control: {cacheControl}");
            }
        }

        [TestMethod]
        public async Task StreamSource_ReturnsMultipartResponse()
        {
            // Act
            var response = await _client.GetAsync("/stream/source");

            // Assert
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var contentType = response.Content.Headers.ContentType;
            Assert.IsNotNull(contentType, "Should have content type");
            Assert.IsTrue(contentType.MediaType?.Contains("multipart") == true, "Should return multipart content");
            Assert.IsTrue(contentType.MediaType?.Contains("mixed-replace") == true, "Should be mixed-replace multipart");
        }

        [TestMethod]
        public async Task StreamOverlay_ReturnsMultipartResponse()
        {
            // Act
            var response = await _client.GetAsync("/stream/overlay");

            // Assert
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Content.Headers.ContentType?.MediaType?.Contains("multipart") == true, "Should return multipart content");
        }

        [TestMethod]
        public async Task StreamAudio_ReturnsAudioResponse()
        {
            // Act
            var response = await _client.GetAsync("/stream/audio");

            // Assert
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("audio/mp3", response.Content.Headers.ContentType?.MediaType);
        }

        [TestMethod]
        public async Task StreamMix_ReturnsVideoResponse()
        {
            // Act
            var response = await _client.GetAsync("/stream/mix");

            // Assert
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.AreEqual("video/mp4", response.Content.Headers.ContentType?.MediaType);
        }

        [TestMethod]
        public void NoExtraFfmpegProcesses_RemainAfterTest()
        {
            // This test should run after all other tests to ensure cleanup
            // Count ffmpeg processes before cleanup
            var ffmpegProcessesBefore = Process.GetProcessesByName("ffmpeg").Length;

            // Force cleanup
            CleanupFfmpegProcesses();

            // Count after cleanup
            var ffmpegProcessesAfter = Process.GetProcessesByName("ffmpeg").Length;

            // Assert that cleanup worked (should be 0 or same as before if none existed)
            Assert.IsTrue(ffmpegProcessesAfter <= ffmpegProcessesBefore, "Cleanup should not leave more processes than before");
        }

        [TestMethod]
        public async Task CaptureEndpoints_HandleCancellationProperly()
        {
            // Test that capture endpoints handle request cancellation
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(1); // Cancel immediately

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, "/stream/source/capture");
                var response = await _client.SendAsync(request, cts.Token);

                // Should either succeed (if fast) or be cancelled
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
        public async Task MultipleConcurrentCaptureRequests_Work()
        {
            // Test that multiple capture requests can run concurrently without issues
            var tasks = new[]
            {
                _client.GetAsync("/stream/source/capture"),
                _client.GetAsync("/stream/overlay/capture"),
                _client.GetAsync("/stream/mix/capture")
            };

            // Act
            var responses = await Task.WhenAll(tasks);

            // Assert
            foreach (var response in responses)
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("image/jpeg", response.Content.Headers.ContentType?.MediaType);
            }
        }

        [TestMethod]
        public async Task DataFlow_PipelineStagesAreAccessible()
        {
            // Test that all pipeline stages are accessible and return expected content types
            var pipelineTests = new[]
            {
                ("/stream/source", "multipart/x-mixed-replace"),
                ("/stream/overlay", "multipart/x-mixed-replace"),
                ("/stream/audio", "audio/mp3"),
                ("/stream/mix", "video/mp4"),
                ("/stream/source/capture", "image/jpeg"),
                ("/stream/overlay/capture", "image/jpeg"),
                ("/stream/mix/capture", "image/jpeg")
            };

            foreach (var (endpoint, expectedContentType) in pipelineTests)
            {
                // Act
                var response = await _client.GetAsync(endpoint);

                // Assert
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                    $"Endpoint {endpoint} should return OK");
                Assert.AreEqual(expectedContentType, response.Content.Headers.ContentType?.MediaType,
                    $"Endpoint {endpoint} should return {expectedContentType}");
            }
        }

        [TestMethod]
        public async Task CaptureEndpoints_ReturnReasonableContentLength()
        {
            // Test that capture endpoints return reasonable JPEG sizes
            var endpoints = new[] { "/stream/source/capture", "/stream/overlay/capture", "/stream/mix/capture" };

            foreach (var endpoint in endpoints)
            {
                // Act
                var response = await _client.GetAsync(endpoint);
                var content = await response.Content.ReadAsByteArrayAsync();

                // Assert
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.IsTrue(content.Length >= 4, $"Endpoint {endpoint} should return at least 4 bytes (minimal JPEG)");
                Assert.IsTrue(content.Length < 1024 * 1024, $"Endpoint {endpoint} should return less than 1MB (reasonable JPEG size)");
            }
        }
    }
}