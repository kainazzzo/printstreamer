using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
// MoonrakerClient is in the global namespace (Clients/MoonrakerClient.cs)
using System.Text.Json.Nodes;

namespace PrintStreamer.Utils.Tests
{
    [TestClass]
    public class TimelapseIntegrationTests
    {
        /// <summary>
        /// Integration test: verifies that Moonraker exposes metadata for a G-code file which
        /// TimelapseManager expects (slicer, layer_count, filament_total_mm etc).
        ///
        /// This test is optional; set environment variables and enable it to run against a real Moonraker server.
        /// To enable, set MOONRAKER_INTEGRATION=1 (or RUN_MOONRAKER_INTEGRATION=true) and configure other vars below.
        /// - MOONRAKER_BASEURL (default: http://127.0.0.1:7125)
        /// - MOONRAKER_API_KEY (optional)
        /// - MOONRAKER_FILENAME (optional; defaults to current print job if printing)
        ///
        /// Run example:
        /// MOONRAKER_BASEURL=http://192.168.1.117:7125 MOONRAKER_API_KEY=myKey dotnet test --filter FullyQualifiedName~PrintStreamer.Utils.Tests.TimelapseIntegrationTests
        ///
        /// Note: this test will call live Moonraker endpoints. If no printer or current job is present,
        /// set MOONRAKER_FILENAME to the path or name of a G-code file on the server.
        /// </summary>
        [TestMethod]
        public async Task Moonraker_FileMetadata_IsPresent_ForCurrentJobOrProvidedFile()
        {
            // Integration gating: only run if MOONRAKER_INTEGRATION or RUN_MOONRAKER_INTEGRATION is set to '1' or 'true'
            var run = Environment.GetEnvironmentVariable("MOONRAKER_INTEGRATION") ?? Environment.GetEnvironmentVariable("RUN_MOONRAKER_INTEGRATION");
            if (string.IsNullOrWhiteSpace(run) || !(run == "1" || run.Equals("true", StringComparison.OrdinalIgnoreCase)))
            {
                Assert.Inconclusive("Integration test disabled - set MOONRAKER_INTEGRATION=1 or RUN_MOONRAKER_INTEGRATION=true to enable.");
            }
            var baseUrl = Environment.GetEnvironmentVariable("MOONRAKER_BASEURL") ?? "http://127.0.0.1:7125";
            var apiKey = Environment.GetEnvironmentVariable("MOONRAKER_API_KEY");
            var providedFilename = Environment.GetEnvironmentVariable("MOONRAKER_FILENAME");

            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));
            var client = new MoonrakerClient(loggerFactory.CreateLogger<MoonrakerClient>());
            var baseUri = new Uri(baseUrl);

            // Try to find a filename to look up metadata for
            string? filename = providedFilename;
            if (string.IsNullOrWhiteSpace(filename))
            {
                var info = await client.GetPrintInfoAsync(baseUri, apiKey, null, CancellationToken.None);
                filename = info?.Filename;
            }

            // Fallback: try gcode_store when we still don't have a filename
            if (string.IsNullOrWhiteSpace(filename))
            {
                var store = await client.GetGcodeStoreAsync(baseUri, apiKey, null, 25, CancellationToken.None);
                if (store != null && store.Count > 0)
                {
                    // Try to pick an entry that has a filename property
                    foreach (var it in store)
                    {
                        var obj = it as JsonObject;
                        if (obj == null) continue;
                        if (obj.TryGetPropertyValue("filename", out var f) && f != null)
                        {
                            filename = f.ToString();
                            break;
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(filename))
                Assert.Inconclusive("No filename found; set MOONRAKER_FILENAME or ensure the printer is printing a job to run this integration test.");

            var metadata = await client.GetFileMetadataAsync(baseUri, filename, apiKey, null, CancellationToken.None);
            Assert.IsNotNull(metadata, "File metadata should be retrievable from Moonraker for the given file");

            // Top-level result object should hold metadata
            var result = metadata? ["result"] as JsonObject;
            Assert.IsNotNull(result, "Expected 'result' object in Moonraker metadata response");

            // Check for 'metadata' object or at least some common metadata fields
            var metaObj = result?.TryGetPropertyValue("metadata", out var m) == true ? m as JsonObject : null;
            bool hasSlicer = metaObj != null && metaObj.ContainsKey("slicer");
            bool hasLayerCount = (metaObj != null && (metaObj.ContainsKey("layer_count") || metaObj.ContainsKey("layerCount"))) ||
                                 (result != null && (result.ContainsKey("layer_count") || result.ContainsKey("layerCount")));
            bool hasFilamentTotal = (metaObj != null && (metaObj.ContainsKey("filament_total_mm") || metaObj.ContainsKey("filament_total"))) ||
                                    (result != null && (result.ContainsKey("filament_total_mm") || result.ContainsKey("filament_total")));

            Assert.IsTrue(hasSlicer || hasLayerCount || hasFilamentTotal,
                "Expected at least one of 'slicer', 'layer_count', or 'filament_total_mm' to be present in the file metadata or result.");
        }
    }
}
