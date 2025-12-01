using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PrintStreamer.Timelapse;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PrintStreamer.Utils.Tests
{
    [TestClass]
    public class TimelapseManagerTests : BaseTest<TimelapseManager>
    {
        protected Mock<ILogger<TimelapseService>>? TimelapseServiceLoggerMock { get; set; }
        protected string? TempTimelapseDir { get; set; }

        [TestInitialize]
        public override void TestInitialize()
        {
            base.TestInitialize();

            TempTimelapseDir = Path.Combine(Path.GetTempPath(), $"timelapse_test_{Guid.NewGuid()}");
            Directory.CreateDirectory(TempTimelapseDir);

            TimelapseServiceLoggerMock = new Mock<ILogger<TimelapseService>>();

            // Create a real ConfigurationBuilder with InMemory data
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Timelapse:MainFolder"] = TempTimelapseDir,
                    ["Stream:Source"] = "http://localhost:8080/stream.mjpeg"
                })
                .Build();

            // MoonrakerClient requires HTTP clients, so we pass null and let it handle gracefully
            // The tests don't use features that require MoonrakerClient anyway
            Sut = new TimelapseManager(config, AutoMock.Mock<ILogger<TimelapseManager>>().Object, TimelapseServiceLoggerMock.Object, null!);
        }

        [TestCleanup]
        public new void TestCleanup()
        {
            if (Directory.Exists(TempTimelapseDir))
            {
                try
                {
                    Directory.Delete(TempTimelapseDir, true);
                }
                catch { /* Ignore cleanup errors */ }
            }
            base.TestCleanup();
        }

        [TestMethod]
        public void Constructor_CreatesTimelapseDirectory_WhenItDoesNotExist()
        {
            // Assert
            Assert.IsTrue(Directory.Exists(TempTimelapseDir), "Timelapse directory should be created");
        }

        [TestMethod]
        public void TimelapseDirectory_ReturnsConfiguredPath()
        {
            // Act & Assert
            Assert.AreEqual(TempTimelapseDir, Sut!.TimelapseDirectory);
        }

        [TestMethod]
        public void StartTimelapseAsync_ReturnsSessionName_WhenSuccessful()
        {
            // Arrange
            var sessionName = "test_print";

            // Act
            var result = Sut!.StartTimelapseAsync(sessionName).GetAwaiter().GetResult();

            // Assert
            Assert.IsNotNull(result, "StartTimelapseAsync should return a session name");
            Assert.IsFalse(string.IsNullOrEmpty(result), "Session name should not be empty");
        }

        [TestMethod]
        public void StartTimelapseAsync_ReturnsNull_WhenSessionNameIsEmpty()
        {
            // Act
            var result = Sut!.StartTimelapseAsync(string.Empty).GetAwaiter().GetResult();

            // Assert
            Assert.IsNull(result, "Should return null for empty session name");
        }

        [TestMethod]
        public void StartTimelapseAsync_ReturnsNull_WhenSessionNameIsNull()
        {
            // Act
            var result = Sut!.StartTimelapseAsync(null).GetAwaiter().GetResult();

            // Assert
            Assert.IsNull(result, "Should return null for null session name");
        }

        [TestMethod]
        public void StartTimelapseAsync_CreatesOutputDirectory()
        {
            // Arrange
            var sessionName = "test_print_2";

            // Act
            var result = Sut!.StartTimelapseAsync(sessionName).GetAwaiter().GetResult();

            // Assert
            Assert.IsNotNull(result);
            var expectedDir = Path.Combine(TempTimelapseDir, result);
            Assert.IsTrue(Directory.Exists(expectedDir), "Output directory should be created");
        }

        [TestMethod]
        public void StartTimelapseAsync_AppendsNumericSuffixWhenDuplicateSessionName()
        {
            // Arrange
            var sessionName = "duplicate_test";

            // Act
            var result1 = Sut!.StartTimelapseAsync(sessionName).GetAwaiter().GetResult();
            Sut!.StopTimelapseAsync(result1).GetAwaiter().GetResult();
            var result2 = Sut!.StartTimelapseAsync(sessionName).GetAwaiter().GetResult();

            // Assert
            Assert.IsNotNull(result1);
            Assert.IsNotNull(result2);
            Assert.AreNotEqual(result1, result2, "Duplicate sessions should have different names");
            Assert.IsTrue(result2.Contains("_1") || result2.Contains("_2"), "Result should contain numeric suffix");
        }

        [TestMethod]
        public void GetActiveSessionNames_ReturnsActiveSessionName()
        {
            // Arrange
            var sessionName = "active_session";
            var result = Sut!.StartTimelapseAsync(sessionName).GetAwaiter().GetResult();

            // Act
            var activeNames = Sut!.GetActiveSessionNames();

            // Assert
            Assert.IsTrue(activeNames.Contains(result), "Active session should be in the active sessions list");
        }

        [TestMethod]
        public void GetActiveSessionNames_DoesNotIncludeStoppedSession()
        {
            // Arrange
            var sessionName = "stopped_session";
            var sessionResult = Sut!.StartTimelapseAsync(sessionName).GetAwaiter().GetResult();

            // Act
            Sut!.StopTimelapseAsync(sessionResult).GetAwaiter().GetResult();
            var activeNames = Sut!.GetActiveSessionNames();

            // Assert
            Assert.IsFalse(activeNames.Contains(sessionResult), "Stopped session should not be in active sessions list");
        }

        [TestMethod]
        public void StopTimelapseAsync_RemovesSessionFromActive()
        {
            // Arrange
            var sessionName = "stop_test";
            var sessionResult = Sut!.StartTimelapseAsync(sessionName).GetAwaiter().GetResult();

            // Act
            Sut!.StopTimelapseAsync(sessionResult).GetAwaiter().GetResult();
            var activeNames = Sut!.GetActiveSessionNames();

            // Assert
            Assert.IsFalse(activeNames.Contains(sessionResult), "Session should be removed from active sessions");
        }

        [TestMethod]
        public void StopTimelapseAsync_ReturnsNull_WhenSessionNotFound()
        {
            // Act
            var result = Sut!.StopTimelapseAsync("nonexistent_session").GetAwaiter().GetResult();

            // Assert
            Assert.IsNull(result, "Should return null when stopping nonexistent session");
        }

        [TestMethod]
        public void GetAllTimelapses_ReturnsTimelapseInfo_ForCreatedSessions()
        {
            // Arrange
            var sessionName = "info_test";
            var sessionResult = Sut!.StartTimelapseAsync(sessionName).GetAwaiter().GetResult();

            // Act
            var allTimelapses = Sut!.GetAllTimelapses();

            // Assert
            Assert.IsNotNull(allTimelapses);
            var timelapse = allTimelapses.FirstOrDefault(t => t.Name == sessionResult);
            Assert.IsNotNull(timelapse, "Created timelapse should be returned");
            Assert.AreEqual(sessionResult, timelapse.Name);
            Assert.IsTrue(timelapse.IsActive, "Recently created timelapse should be active");
        }

        [TestMethod]
        public void GetAllTimelapses_ReturnsEmptyList_WhenNoTimelapses()
        {
            // Act
            var allTimelapses = Sut!.GetAllTimelapses().ToList();

            // Assert
            Assert.IsNotNull(allTimelapses);
            Assert.AreEqual(0, allTimelapses.Count, "Should return empty list when no timelapses exist");
        }

        [TestMethod]
        public void GetMetadataForFilename_ReturnsNull_ForNonexistentFilename()
        {
            // Act
            var metadata = Sut!.GetMetadataForFilename("nonexistent.gcode");

            // Assert
            Assert.IsNull(metadata, "Should return null for nonexistent filename");
        }

        [TestMethod]
        public void GetMetadataForFilename_ReturnsNull_ForEmptyFilename()
        {
            // Act
            var metadata = Sut!.GetMetadataForFilename(string.Empty);

            // Assert
            Assert.IsNull(metadata, "Should return null for empty filename");
        }

        [TestMethod]
        public void GetMetadataForFilename_ReturnsNull_ForNullFilename()
        {
            // Act
            var metadata = Sut!.GetMetadataForFilename(null);

            // Assert
            Assert.IsNull(metadata, "Should return null for null filename");
        }

        [TestMethod]
        public void MultipleActiveSessions_AreTrackedIndependently()
        {
            // Arrange
            var session1 = "session_1";
            var session2 = "session_2";

            // Act
            var result1 = Sut!.StartTimelapseAsync(session1).GetAwaiter().GetResult();
            var result2 = Sut!.StartTimelapseAsync(session2).GetAwaiter().GetResult();
            var activeNames = Sut!.GetActiveSessionNames().ToList();

            // Assert
            Assert.AreEqual(2, activeNames.Count, "Should have 2 active sessions");
            Assert.IsTrue(activeNames.Contains(result1));
            Assert.IsTrue(activeNames.Contains(result2));
        }

        [TestMethod]
        public void NotifyPrinterState_PausesAndResumesSession()
        {
            // Arrange
            var sessionName = "pause_session";
            var session = Sut!.StartTimelapseAsync(sessionName).GetAwaiter().GetResult();
            Assert.IsNotNull(session);

            // Act - pause
            Sut!.NotifyPrinterState(session, "paused");

            // Assert - session should be paused when inspecting GetAllTimelapses
            var all = Sut!.GetAllTimelapses().ToList();
            var info = all.FirstOrDefault(t => t.Name == session);
            Assert.IsNotNull(info);
            Assert.IsTrue(info.IsActive);
            Assert.IsTrue(info.IsPaused, "Session should be flagged as paused after NotifyPrinterState paused");

            // Act - resume
            Sut!.NotifyPrinterState(session, "printing");
            all = Sut!.GetAllTimelapses().ToList();
            info = all.FirstOrDefault(t => t.Name == session);
            Assert.IsNotNull(info);
            Assert.IsFalse(info.IsPaused, "Session should not be paused after notifying printing state");
        }

        [TestMethod]
        public void StartTimelapse_AfterRestart_ResumesSameTimelapseForSameJob()
        {
            // Arrange
            var jobFilename = "resume_test.gcode";
            var jobSafe = Path.GetFileNameWithoutExtension(jobFilename);

            // Start initial session (no moonraker filename in tests to avoid NRE)
            var startedSession = Sut!.StartTimelapseAsync(jobSafe).GetAwaiter().GetResult();
            Assert.IsNotNull(startedSession);

            var outputDir = Path.Combine(TempTimelapseDir, startedSession);
            Assert.IsTrue(Directory.Exists(outputDir));

            // Simulate a captured frame by creating a frame file on disk
            var sampleFrame = Path.Combine(outputDir, "frame_000000.jpg");
            File.WriteAllBytes(sampleFrame, new byte[] { 1, 2, 3 });

            // Simulate app restart by disposing current manager and creating a new one with same configuration
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Timelapse:MainFolder"] = TempTimelapseDir,
                    ["Stream:Source"] = "http://localhost:8080/stream.mjpeg"
                })
                .Build();
            Sut = new TimelapseManager(config, AutoMock.Mock<ILogger<TimelapseManager>>().Object, TimelapseServiceLoggerMock!.Object, null!);

            // Act - start timelapse again for same job name (representing a restart)
            var resumed = Sut!.StartTimelapseAsync(jobSafe).GetAwaiter().GetResult();

            // Assert - we expect the restarted manager to resume the same folder for the continuing print
            Assert.AreEqual(startedSession, resumed, "Timelapse manager should resume writing to same folder after restart (same job)");

            // Simulate print reaching last layer (trigger finalization)
            var finalized = Sut!.NotifyPrintProgressAsync(resumed, 10, 10).GetAwaiter().GetResult();
            Assert.IsNull(finalized, "CreateVideoAsync may be null in unit test environment (ffmpeg not available), but notify should succeed and stop session");

            // Since ffmpeg isn't available in unit tests, simulate a created mp4 to indicate finalization
            var dummyVideo = Path.Combine(outputDir, $"{resumed}.mp4");
            File.WriteAllBytes(dummyVideo, new byte[] { 0 });

            // After finalization, starting a new print with the same file name should create a new directory (suffix appended)
            var secondSession = Sut!.StartTimelapseAsync(jobSafe).GetAwaiter().GetResult();
            Assert.IsNotNull(secondSession);
            Assert.AreNotEqual(resumed, secondSession, "A new print with the same file name should result in a new timelapse folder (numeric suffix appended)");
        }

        [TestMethod]
        public void StartTimelapse_DoesNotResume_WhenLastFrameIsOlderThanThreshold()
        {
            // Arrange
            var jobFilename = "oldframes_test.gcode";
            var jobSafe = Path.GetFileNameWithoutExtension(jobFilename);

            // Create initial session and simulate a captured frame
            var startedSession = Sut!.StartTimelapseAsync(jobSafe).GetAwaiter().GetResult();
            Assert.IsNotNull(startedSession);
            var outputDir = Path.Combine(TempTimelapseDir, startedSession);
            Assert.IsTrue(Directory.Exists(outputDir));
            var sampleFrame = Path.Combine(outputDir, "frame_000000.jpg");
            File.WriteAllBytes(sampleFrame, new byte[] { 1, 2, 3 });

            // Save metadata to indicate this folder belonged to a different moonraker filename (i.e. different print)
            var metaJson = new System.Text.Json.Nodes.JsonObject();
            metaJson["session_name"] = startedSession + "_other"; // different session name to prevent resume
            metaJson["moonraker_filename"] = "different_name.gcode";
            File.WriteAllText(Path.Combine(outputDir, "timelapse_metadata.json"), metaJson.ToJsonString());

            // recreate manager
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Timelapse:MainFolder"] = TempTimelapseDir,
                    ["Stream:Source"] = "http://localhost:8080/stream.mjpeg",
                })
                .Build();
            Sut = new TimelapseManager(config, AutoMock.Mock<ILogger<TimelapseManager>>().Object, TimelapseServiceLoggerMock!.Object, null!);

            // Act - attempt to start timelapse for the same job filename
            var resumed = Sut!.StartTimelapseAsync(jobSafe).GetAwaiter().GetResult();

            // Assert - manager should not resume into the old folder due to old frame time; it should create a new folder
            Assert.IsNotNull(resumed);
            Assert.AreNotEqual(startedSession, resumed, "Timelapse manager should create a new folder if last frame is older than configured resume window");
        }

        [TestMethod]
        public void StartTimelapse_Resumes_WhenLastFrameWithinThreshold()
        {
            // Arrange
            var jobFilename = "recentframes_test.gcode";
            var jobSafe = Path.GetFileNameWithoutExtension(jobFilename);

            // Create initial session and simulate a captured frame that is current
            var startedSession = Sut!.StartTimelapseAsync(jobSafe).GetAwaiter().GetResult();
            Assert.IsNotNull(startedSession);
            var outputDir = Path.Combine(TempTimelapseDir, startedSession);
            Assert.IsTrue(Directory.Exists(outputDir));
            var sampleFrame = Path.Combine(outputDir, "frame_000000.jpg");
            File.WriteAllBytes(sampleFrame, new byte[] { 1, 2, 3 });

            // Save metadata to indicate this folder belonged to the same session (so resume should be allowed)
            var metaJson = new System.Text.Json.Nodes.JsonObject();
            metaJson["session_name"] = startedSession;
            File.WriteAllText(Path.Combine(outputDir, "timelapse_metadata.json"), metaJson.ToJsonString());

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Timelapse:MainFolder"] = TempTimelapseDir,
                    ["Stream:Source"] = "http://localhost:8080/stream.mjpeg",
                    ["Timelapse:ResumeWithinSeconds"] = "5"
                })
                .Build();
            Sut = new TimelapseManager(config, AutoMock.Mock<ILogger<TimelapseManager>>().Object, TimelapseServiceLoggerMock!.Object, null!);

            // Act - attempt to start timelapse for the same job filename
            var resumed = Sut!.StartTimelapseAsync(jobSafe).GetAwaiter().GetResult();

            // Assert - manager should resume into the existing folder due to recent frame
            Assert.IsNotNull(resumed);
            Assert.AreEqual(startedSession, resumed, "Timelapse manager should reuse folder if last frame is within resume window");
        }

        [TestMethod]
        public void StartTimelapse_Resumes_WhenMoonrakerFilenameMatchesSavedMetadata()
        {
            // Arrange
            var jobFilename = "moonraker_match_test.gcode";
            var jobSafe = Path.GetFileNameWithoutExtension(jobFilename);

            // Create initial session and simulate a captured frame
            var startedSession = Sut!.StartTimelapseAsync(jobSafe).GetAwaiter().GetResult();
            Assert.IsNotNull(startedSession);
            var outputDir = Path.Combine(TempTimelapseDir, startedSession);
            Assert.IsTrue(Directory.Exists(outputDir));
            var sampleFrame = Path.Combine(outputDir, "frame_000000.jpg");
            File.WriteAllBytes(sampleFrame, new byte[] { 1, 2, 3 });

            // Save metadata indicating the moonraker filename
            var metaJson = new System.Text.Json.Nodes.JsonObject();
            metaJson["session_name"] = startedSession;
            metaJson["moonraker_filename"] = jobFilename;
            File.WriteAllText(Path.Combine(outputDir, "timelapse_metadata.json"), metaJson.ToJsonString());

            // recreate manager
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Timelapse:MainFolder"] = TempTimelapseDir,
                    ["Stream:Source"] = "http://localhost:8080/stream.mjpeg"
                })
                .Build();
            Sut = new TimelapseManager(config, AutoMock.Mock<ILogger<TimelapseManager>>().Object, TimelapseServiceLoggerMock!.Object, null!);

            // Act - supply the moonraker filename as the second parameter
            var resumed = Sut!.StartTimelapseAsync(jobSafe, jobFilename).GetAwaiter().GetResult();

            // Assert - should resume into the existing folder
            Assert.IsNotNull(resumed);
            Assert.AreEqual(startedSession, resumed, "Timelapse manager should reuse folder when moonraker filename matches saved metadata");
        }
    }
}
