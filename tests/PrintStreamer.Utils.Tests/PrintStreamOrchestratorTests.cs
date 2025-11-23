using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using PrintStreamer.Models;
using PrintStreamer.Timelapse;
using PrintStreamer.Services;

namespace PrintStreamer.Utils.Tests
{
    [TestClass]
    public class PrintStreamOrchestratorTests
    {
        [TestMethod]
        public async Task StartsOnlyOneTimelapsePerPrintJob()
        {
            // Arrange
            var inMemory = new Dictionary<string, string?>
            {
                ["YouTube:LiveBroadcast:Enabled"] = "false", // avoid broadcast side-effects
                ["Timelapse:LastLayerOffset"] = "1",
                ["Timelapse:LastLayerRemainingSeconds"] = "30",
                ["Timelapse:LastLayerProgressPercent"] = "98.5"
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();

            var timelapseFake = new FakeTimelapseManager();

            var orchestrator = new PrintStreamOrchestrator(config, NullLoggerFactory.Instance, timelapseFake);

            var startState = new PrinterState
            {
                State = "printing",
                Filename = "job.gcode",
                ProgressPercent = 0.0,
                CurrentLayer = 1,
                TotalLayers = 100,
                SnapshotTime = DateTime.UtcNow
            };

            // Act - initial printing event should start a timelapse
            await orchestrator.HandlePrinterStateChangedAsync(null, startState, CancellationToken.None);

            // Assert Start called once
            Assert.AreEqual(1, timelapseFake.StartCount);

            // Act - simulate print finishing (idle) which should finalize/stop the timelapse
            // Mark done state as completed (progress/layers complete) so orchestrator will finalize immediately
            var doneState = startState with { State = "idle", CurrentLayer = startState.TotalLayers, ProgressPercent = 100.0 };
            await orchestrator.HandlePrinterStateChangedAsync(startState, doneState, CancellationToken.None);

            // Ensure Stop was called at least once during finalization (allow background finalize task to run)
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (timelapseFake.StopCount < 1 && sw.Elapsed < TimeSpan.FromSeconds(2))
            {
                await Task.Delay(50);
            }
            Assert.IsTrue(timelapseFake.StopCount >= 1);

            // Act - simulate a new printing state but with the same filename (should NOT start a new timelapse)
            var resumedState = doneState with { State = "printing" };
            await orchestrator.HandlePrinterStateChangedAsync(doneState, resumedState, CancellationToken.None);

            // Verify Start still only called once
            Assert.AreEqual(1, timelapseFake.StartCount);
        }

        [TestMethod]
        public async Task NotifiesTimelapseManagerOnPauseAndResume()
        {
            // Arrange
            var inMemory = new Dictionary<string, string?>
            {
                ["YouTube:LiveBroadcast:Enabled"] = "false",
                ["Timelapse:LastLayerOffset"] = "1",
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();

            var timelapseFake = new FakeTimelapseManager();
            var orchestrator = new PrintStreamOrchestrator(config, NullLoggerFactory.Instance, timelapseFake);

            var startState = new PrinterState {
                State = "printing",
                Filename = "job.gcode",
                CurrentLayer = 1,
                TotalLayers = 100,
                ProgressPercent = 10.0,
                SnapshotTime = DateTime.UtcNow
            };

            // Act - Start session
            await orchestrator.HandlePrinterStateChangedAsync(null, startState, CancellationToken.None);

            // Act - Pause
            var pausedState = startState with { State = "paused" };
            await orchestrator.HandlePrinterStateChangedAsync(startState, pausedState, CancellationToken.None);

            // Assert pause notified
            Assert.AreEqual("paused", timelapseFake.LastStateNotified, true);

            // Act - Resume
            var resumeState = pausedState with { State = "printing" };
            await orchestrator.HandlePrinterStateChangedAsync(pausedState, resumeState, CancellationToken.None);

            // Assert resume notified
            Assert.AreEqual("printing", timelapseFake.LastStateNotified, true);
        }
        
        private class FakeTimelapseManager : ITimelapseManager
        {
            public int StartCount;
            public int StopCount;
                public string? LastStateNotified;

            public Task<string?> StartTimelapseAsync(string sessionName, string? moonrakerFilename = null)
            {
                StartCount++;
                return Task.FromResult<string?>("session_folder");
            }

            public Task<string?> StopTimelapseAsync(string sessionName)
            {
                StopCount++;
                return Task.FromResult<string?>("/tmp/video.mp4");
            }

            public void NotifyPrintProgress(string? sessionName, int? currentLayer, int? totalLayers) { }
            public void NotifyPrinterState(string? sessionName, string? state) { LastStateNotified = state; }
        }
    }
}
