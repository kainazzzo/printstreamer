using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using PrintStreamer.Models;
using PrintStreamer.Timelapse;
using PrintStreamer.Services;
using PrintStreamer.Interfaces;

namespace PrintStreamer.Utils.Tests
{
    [TestClass]
    public class PrintStreamOrchestratorAutoFinalizeTests
    {
        private string? _tempTimelapseDir;
        private Mock<IStreamOrchestrator>? _streamOrchestratorMock;
        private Mock<IMoonrakerPoller>? _moonrakerPollerMock;
        private Mock<ILogger<PrintStreamOrchestrator>>? _orchestratorLoggerMock;

        [TestInitialize]
        public void Setup()
        {
            _tempTimelapseDir = Path.Combine(Path.GetTempPath(), $"timelapse_orchestrator_autofinalize_{Guid.NewGuid()}");
            Directory.CreateDirectory(_tempTimelapseDir);
            _streamOrchestratorMock = new Mock<IStreamOrchestrator>();
            _moonrakerPollerMock = new Mock<IMoonrakerPoller>();
            _orchestratorLoggerMock = new Mock<ILogger<PrintStreamOrchestrator>>();
        }

        [TestCleanup]
        public void Cleanup()
        {
            try
            {
                if (Directory.Exists(_tempTimelapseDir!))
                    Directory.Delete(_tempTimelapseDir!, true);
            }
            catch { /* ignore */ }
        }

        [TestMethod]
        public async Task Orchestrator_CallsStop_WhenManagerDoesNotAutoFinalize()
        {
            // Arrange: mock timelapse manager that signals threshold reached but doesn't finalize (returns null)
            var timelapseMock = new Mock<ITimelapseManager>();
            var sessionName = "orchestrator_session";
            timelapseMock.Setup(m => m.NotifyPrintProgressAsync(sessionName, It.IsAny<int?>(), It.IsAny<int?>()))
                         .ReturnsAsync((string?)null);
            timelapseMock.Setup(m => m.NotifyPrinterState(sessionName, It.IsAny<string?>()));
            timelapseMock.Setup(m => m.StartTimelapseAsync(It.IsAny<string>(), It.IsAny<string?>()))
                         .ReturnsAsync(sessionName);

            // When orchestrator calls StopTimelapseAsync we return a dummy path
            var dummyVideo = Path.Combine(_tempTimelapseDir!, $"{sessionName}.mp4");
            File.WriteAllText(dummyVideo, "dummy");
            timelapseMock.Setup(m => m.StopTimelapseAsync(sessionName)).ReturnsAsync(dummyVideo);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Timelapse:MainFolder"] = _tempTimelapseDir,
                    ["Stream:Source"] = "http://localhost:8080/stream.mjpeg",
                    ["Timelapse:AutoFinalize"] = "false",
                    // Use small offsets so last-layer detection triggers quickly
                    ["Timelapse:LastLayerOffset"] = "1"
                })
                .Build();

            var orchestrator = new PrintStreamOrchestrator(config, timelapseMock.Object, _streamOrchestratorMock!.Object, _moonrakerPollerMock!.Object, _orchestratorLoggerMock!.Object);

            // Simulate a state where printing and at last layer
            var state = new PrinterState
            {
                State = "printing",
                Filename = "job.gcode",
                CurrentLayer = 10,
                TotalLayers = 10,
                ProgressPercent = 100.0
            };

            // Act - call handler (this should trigger orchestrator to call StopTimelapseAsync after manager signaled null)
            await orchestrator.HandlePrinterStateChangedAsync(null, state, CancellationToken.None);

            // The orchestrator finalization may run in background; wait briefly for it to complete
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(2))
            {
                try
                {
                    timelapseMock.Verify(m => m.StopTimelapseAsync(sessionName), Times.AtLeastOnce);
                    break;
                }
                catch (MockException)
                {
                    await Task.Delay(50);
                }
            }

            // Final assert: StopTimelapseAsync was called
            timelapseMock.Verify(m => m.StopTimelapseAsync(sessionName), Times.AtLeastOnce);
        }
    }
}
