using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Bunit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using printstreamer.Components.Shared;

namespace PrintStreamer.Utils.Tests
{
    [TestClass]
    public class TimelapseEditorTests
    {
        private TestServer? _server;
        private HttpClient? _client;
        private string? _tempDir;

        [TestInitialize]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"timelapse_editor_test_{Guid.NewGuid()}");
            Directory.CreateDirectory(_tempDir);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string> { ["Timelapse:MainFolder"] = _tempDir })
                .Build();

            var loggerFactory = LoggerFactory.Create(b => b.AddConsole());
            var timelapseManager = new PrintStreamer.Timelapse.TimelapseManager(config, loggerFactory, null!);

            var builder = new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<IConfiguration>(config);
                    services.AddSingleton(timelapseManager);
                    services.AddLogging();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/api/timelapses/{name}/frames", async context =>
                        {
                            var name = (string)context.Request.RouteValues["name"]!;
                            var dir = Path.Combine(timelapseManager.TimelapseDirectory, name);
                            if (!Directory.Exists(dir)) { context.Response.StatusCode = 404; return; }
                            var frames = Directory.GetFiles(dir, "frame_*.jpg").OrderBy(f => f).Select(Path.GetFileName).ToArray();
                            context.Response.ContentType = "application/json";
                            await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { success = true, frames }));
                        });

                        endpoints.MapDelete("/api/timelapses/{name}/frames/{filename}", async context =>
                        {
                            var name = (string)context.Request.RouteValues["name"]!;
                            var filename = (string)context.Request.RouteValues["filename"]!;
                            var dir = Path.Combine(timelapseManager.TimelapseDirectory, name);
                            if (!Directory.Exists(dir)) { context.Response.StatusCode = 404; return; }
                            var filePath = Path.Combine(dir, filename);
                            if (!File.Exists(filePath)) { await context.Response.WriteAsJsonAsync(new { success = false, error = "File not found" }); return; }
                            context.Response.ContentType = "application/json";
                            await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { success = false, error = "File not found" })); return; }
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
                            await context.Response.WriteAsJsonAsync(new { success = true });
                            context.Response.ContentType = "application/json";
                            await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new { success = true }));
                        });
                        endpoints.MapPost("/api/timelapses/{name}/generate", async context =>
                        {
                            // Lightweight fake generation: ensure there are frames and respond success
                            var name = (string)context.Request.RouteValues["name"]!;
                            var dir = Path.Combine(timelapseManager.TimelapseDirectory, name);
                            if (!Directory.Exists(dir)) { await context.Response.WriteAsJsonAsync(new { success = false, error = "Timelapse not found" }); return; }
                            var frames = Directory.GetFiles(dir, "frame_*.jpg");
                            if (frames.Length == 0) { await context.Response.WriteAsJsonAsync(new { success = false, error = "No frames" }); return; }
                            // create a fake mp4 file
                            var fileName = name + ".mp4";
                            var path = Path.Combine(dir, fileName);
                            File.WriteAllBytes(path, new byte[] { 0x00, 0x00, 0x00 });
                            await context.Response.WriteAsJsonAsync(new { success = true, videoPath = path });
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
        public async Task TimelapseEditor_ListsFrames_AndDeletes()
        {
            // Arrange - create timelapse folder and frames
            var name = "editor_test";
            var dir = Path.Combine(_tempDir!, name);
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "frame_000000.jpg"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(dir, "frame_000001.jpg"), new byte[] { 2 });
            File.WriteAllBytes(Path.Combine(dir, "frame_000002.jpg"), new byte[] { 3 });

            using var ctx = new Bunit.BunitContext();
            // Add the HttpClient from the server to the test DI container so the component uses it
            ctx.Services.AddSingleton<HttpClient>(_client!);

            // Render the component
            var cut = ctx.RenderComponent<TimelapseEditor>(parameters => parameters.Add(p => p.Name, name));

            // Wait for the frames to load
            await Task.Delay(200);
            var items = cut.FindAll(".tl-frame-item");
            Assert.AreEqual(3, items.Count);

            // Click delete on the middle frame
            var middleDelete = items[1].QuerySelector("button.icon-btn.delete");
            middleDelete.Click();

            // After deletion, expect count to drop to 2
            await Task.Delay(200);
            items = cut.FindAll(".tl-frame-item");
            Assert.AreEqual(2, items.Count);

            // Confirm files on disk were reindexed
            var files = Directory.GetFiles(dir, "frame_*.jpg").OrderBy(f => f).Select(Path.GetFileName).ToArray();
            Assert.IsTrue(files.Contains("frame_000000.jpg"));
            Assert.IsTrue(files.Contains("frame_000001.jpg"));
            Assert.IsFalse(files.Contains("frame_000002.jpg"));
        }
    }
}
