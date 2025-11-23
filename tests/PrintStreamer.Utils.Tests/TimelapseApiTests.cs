using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PrintStreamer.Timelapse;

namespace PrintStreamer.Utils.Tests
{
    [TestClass]
    public class TimelapseApiTests
    {
        private TestServer? _server;
        private HttpClient? _client;
        private string? _tempDir;
        private TimelapseManager? _timelapseManager;

        [TestInitialize]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"timelapse_api_test_{Guid.NewGuid()}");
            Directory.CreateDirectory(_tempDir);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["Timelapse:MainFolder"] = _tempDir,
                    // Keep other defaults
                })
                .Build();

            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            _timelapseManager = new TimelapseManager(config, loggerFactory, null!);

            var builder = new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IConfiguration>(config);
                    services.AddSingleton(_timelapseManager);
                    services.AddLogging();
                    services.AddRouting();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        // Provide the endpoints under test (similar to Program.cs minimal API)
                        endpoints.MapGet("/api/timelapses/{name}/frames", async context =>
                        {
                            var name = (string)context.Request.RouteValues["name"]!;
                            var dir = Path.Combine(_timelapseManager!.TimelapseDirectory, name);
                            if (!Directory.Exists(dir))
                            {
                                context.Response.StatusCode = 404;
                                return;
                            }
                            var frames = Directory.GetFiles(dir, "frame_*.jpg").OrderBy(f => f).Select(Path.GetFileName).ToArray();
                            context.Response.ContentType = "application/json";
                            await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { success = true, frames }));
                        });

                        endpoints.MapDelete("/api/timelapses/{name}/frames/{filename}", async context =>
                        {
                            var name = (string)context.Request.RouteValues["name"]!;
                            var filename = (string)context.Request.RouteValues["filename"]!;
                            var dir = Path.Combine(_timelapseManager!.TimelapseDirectory, name);
                            if (!Directory.Exists(dir)) { context.Response.StatusCode = 404; return; }

                            if (filename.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) >= 0)
                            {
                                context.Response.StatusCode = 400;
                                    context.Response.ContentType = "application/json";
                                    await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { success = false, error = "Invalid filename" }));
                                return;
                            }
                            var filePath = Path.Combine(dir, filename);
                            if (!File.Exists(filePath)) { context.Response.ContentType = "application/json"; await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { success = false, error = "File not found" })); return; }
                            if (!filename.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) { context.Response.ContentType = "application/json"; await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { success = false, error = "Only frame .jpg files can be deleted" })); return; }
                            if (_timelapseManager!.GetActiveSessionNames().Contains(name)) { context.Response.ContentType = "application/json"; await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { success = false, error = "Cannot delete frames while timelapse is active" })); return; }

                            File.Delete(filePath);

                            // Reindex
                            var remaining = Directory.GetFiles(dir, "frame_*.jpg").OrderBy(f => f).ToArray();
                            for (int i = 0; i < remaining.Length; i++)
                            {
                                var dst = Path.Combine(dir, $"frame_{i:D6}.jpg");
                                var src = remaining[i];
                                if (string.Equals(Path.GetFileName(src), Path.GetFileName(dst), StringComparison.OrdinalIgnoreCase)) continue;
                                try { if (File.Exists(dst)) File.Delete(dst); File.Move(src, dst); } catch { }
                            }

                            context.Response.ContentType = "application/json";
                            await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { success = true }));
                        });
                    });
                });

            _server = new TestServer(builder);
            _client = _server.CreateClient();
        }

        [TestCleanup]
        public void Cleanup()
        {
            _client?.Dispose();
            _server?.Dispose();
            if (!string.IsNullOrEmpty(_tempDir) && Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true);
        }

        [TestMethod]
        public async Task ListFrames_ReturnsOrderedList()
        {
            // Arrange: create a timelapse folder and frames
            var name = "testtl";
            var dir = Path.Combine(_tempDir!, name);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "frame_000000.jpg"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(dir, "frame_000001.jpg"), new byte[] { 2 });
            File.WriteAllBytes(Path.Combine(dir, "frame_000002.jpg"), new byte[] { 3 });

            // Act
            var resp = await _client!.GetAsync($"/api/timelapses/{Uri.EscapeDataString(name)}/frames");
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync();

            // Assert
            Assert.IsTrue(body.Contains("frame_000000.jpg"), "Should contain first frame");
            Assert.IsTrue(body.Contains("frame_000001.jpg"), "Should contain second frame");
            Assert.IsTrue(body.Contains("frame_000002.jpg"), "Should contain third frame");
        }

        [TestMethod]
        public async Task DeleteFrame_ReindexesRemaining()
        {
            // Arrange
            var name = "testtl2";
            var dir = Path.Combine(_tempDir!, name);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "frame_000000.jpg"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(dir, "frame_000001.jpg"), new byte[] { 2 });
            File.WriteAllBytes(Path.Combine(dir, "frame_000002.jpg"), new byte[] { 3 });

            // Act: delete middle frame
            var delResp = await _client!.DeleteAsync($"/api/timelapses/{Uri.EscapeDataString(name)}/frames/frame_000001.jpg");
            delResp.EnsureSuccessStatusCode();
            var delBody = await delResp.Content.ReadAsStringAsync();
            Assert.IsTrue(delBody.Contains("\"success\":true"), "Deletion should report success");

            // Re-get frames via listing
            var listResp = await _client.GetAsync($"/api/timelapses/{Uri.EscapeDataString(name)}/frames");
            listResp.EnsureSuccessStatusCode();
            var listJson = await listResp.Content.ReadAsStringAsync();

            // Remaining should be frame_000000.jpg and frame_000001.jpg after reindex
            Assert.IsTrue(listJson.Contains("frame_000000.jpg"));
            Assert.IsTrue(listJson.Contains("frame_000001.jpg"));
            Assert.IsFalse(listJson.Contains("frame_000002.jpg"));
        }

        [TestMethod]
        public async Task DeleteFrame_WhileActive_ReturnsError()
        {
            // Arrange
            var name = "testtl3";
            var dir = Path.Combine(_tempDir!, name);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "frame_000000.jpg"), new byte[] { 1 });

            // Start a session to make it active
            // Start a session with the same timed manager instance used by the server to make it active
            var started = _timelapseManager!.StartTimelapseAsync(name).GetAwaiter().GetResult();

            // Now use the test server's endpoint that checks active session names
            var resp = await _client!.DeleteAsync($"/api/timelapses/{Uri.EscapeDataString(name)}/frames/frame_000000.jpg");
            var body = await resp.Content.ReadAsStringAsync();

            // Assert - should contain failure due to active session
            Assert.IsTrue(body.Contains("Cannot delete frames while timelapse is active") || body.Contains("\"success\":false"));
        }
    }
}
