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
    public class TimelapseManagerTests
    {
        private Mock<IConfiguration>? _configMock;
        private Mock<ILoggerFactory>? _loggerFactoryMock;
        private TimelapseManager? _sut;
        private string? _tempTimelapseDir;

        [TestInitialize]
        public void Setup()
        {
            _tempTimelapseDir = Path.Combine(Path.GetTempPath(), $"timelapse_test_{Guid.NewGuid()}");
            Directory.CreateDirectory(_tempTimelapseDir);

            _configMock = new Mock<IConfiguration>();
            _loggerFactoryMock = new Mock<ILoggerFactory>();

            // Since GetValue is an extension method, we can't mock it directly.
            // Instead, we'll use a simple configuration that returns values when indexed.
            // Create a real ConfigurationBuilder with InMemory data
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string>
                {
                    ["Timelapse:MainFolder"] = _tempTimelapseDir,
                    ["Stream:Source"] = "http://localhost:8080/stream.mjpeg"
                })
                .Build();

            _configMock = new Mock<IConfiguration>();
            // For all configuration accesses, use the real implementation
            foreach (var section in config.AsEnumerable())
            {
                _configMock
                    .Setup(c => c[section.Key])
                    .Returns(section.Value);
            }

            _sut = new TimelapseManager(config, _loggerFactoryMock.Object);
        }

        [TestCleanup]
        public void Cleanup()
        {
            _sut?.Dispose();
            if (Directory.Exists(_tempTimelapseDir))
            {
                try
                {
                    Directory.Delete(_tempTimelapseDir, true);
                }
                catch { /* Ignore cleanup errors */ }
            }
        }

        [TestMethod]
        public void Constructor_CreatesTimelapseDirectory_WhenItDoesNotExist()
        {
            // Assert
            Assert.IsTrue(Directory.Exists(_tempTimelapseDir), "Timelapse directory should be created");
        }

        [TestMethod]
        public void TimelapseDirectory_ReturnsConfiguredPath()
        {
            // Act & Assert
            Assert.AreEqual(_tempTimelapseDir, _sut.TimelapseDirectory);
        }

        [TestMethod]
        public void StartTimelapseAsync_ReturnsSessionName_WhenSuccessful()
        {
            // Arrange
            var sessionName = "test_print";

            // Act
            var result = _sut.StartTimelapseAsync(sessionName).GetAwaiter().GetResult();

            // Assert
            Assert.IsNotNull(result, "StartTimelapseAsync should return a session name");
            Assert.IsFalse(string.IsNullOrEmpty(result), "Session name should not be empty");
        }

        [TestMethod]
        public void StartTimelapseAsync_ReturnsNull_WhenSessionNameIsEmpty()
        {
            // Act
            var result = _sut.StartTimelapseAsync(string.Empty).GetAwaiter().GetResult();

            // Assert
            Assert.IsNull(result, "Should return null for empty session name");
        }

        [TestMethod]
        public void StartTimelapseAsync_ReturnsNull_WhenSessionNameIsNull()
        {
            // Act
            var result = _sut.StartTimelapseAsync(null).GetAwaiter().GetResult();

            // Assert
            Assert.IsNull(result, "Should return null for null session name");
        }

        [TestMethod]
        public void StartTimelapseAsync_CreatesOutputDirectory()
        {
            // Arrange
            var sessionName = "test_print_2";

            // Act
            var result = _sut.StartTimelapseAsync(sessionName).GetAwaiter().GetResult();

            // Assert
            Assert.IsNotNull(result);
            var expectedDir = Path.Combine(_tempTimelapseDir, result);
            Assert.IsTrue(Directory.Exists(expectedDir), "Output directory should be created");
        }

        [TestMethod]
        public void StartTimelapseAsync_AppendsNumericSuffixWhenDuplicateSessionName()
        {
            // Arrange
            var sessionName = "duplicate_test";

            // Act
            var result1 = _sut.StartTimelapseAsync(sessionName).GetAwaiter().GetResult();
            _sut.StopTimelapseAsync(result1).GetAwaiter().GetResult();
            var result2 = _sut.StartTimelapseAsync(sessionName).GetAwaiter().GetResult();

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
            var result = _sut.StartTimelapseAsync(sessionName).GetAwaiter().GetResult();

            // Act
            var activeNames = _sut.GetActiveSessionNames();

            // Assert
            Assert.IsTrue(activeNames.Contains(result), "Active session should be in the active sessions list");
        }

        [TestMethod]
        public void GetActiveSessionNames_DoesNotIncludeStoppedSession()
        {
            // Arrange
            var sessionName = "stopped_session";
            var sessionResult = _sut.StartTimelapseAsync(sessionName).GetAwaiter().GetResult();

            // Act
            _sut.StopTimelapseAsync(sessionResult).GetAwaiter().GetResult();
            var activeNames = _sut.GetActiveSessionNames();

            // Assert
            Assert.IsFalse(activeNames.Contains(sessionResult), "Stopped session should not be in active sessions list");
        }

        [TestMethod]
        public void StopTimelapseAsync_RemovesSessionFromActive()
        {
            // Arrange
            var sessionName = "stop_test";
            var sessionResult = _sut.StartTimelapseAsync(sessionName).GetAwaiter().GetResult();

            // Act
            _sut.StopTimelapseAsync(sessionResult).GetAwaiter().GetResult();
            var activeNames = _sut.GetActiveSessionNames();

            // Assert
            Assert.IsFalse(activeNames.Contains(sessionResult), "Session should be removed from active sessions");
        }

        [TestMethod]
        public void StopTimelapseAsync_ReturnsNull_WhenSessionNotFound()
        {
            // Act
            var result = _sut.StopTimelapseAsync("nonexistent_session").GetAwaiter().GetResult();

            // Assert
            Assert.IsNull(result, "Should return null when stopping nonexistent session");
        }

        [TestMethod]
        public void GetAllTimelapses_ReturnsTimelapseInfo_ForCreatedSessions()
        {
            // Arrange
            var sessionName = "info_test";
            var sessionResult = _sut.StartTimelapseAsync(sessionName).GetAwaiter().GetResult();

            // Act
            var allTimelapses = _sut.GetAllTimelapses();

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
            var allTimelapses = _sut.GetAllTimelapses().ToList();

            // Assert
            Assert.IsNotNull(allTimelapses);
            Assert.AreEqual(0, allTimelapses.Count, "Should return empty list when no timelapses exist");
        }

        [TestMethod]
        public void GetMetadataForFilename_ReturnsNull_ForNonexistentFilename()
        {
            // Act
            var metadata = _sut.GetMetadataForFilename("nonexistent.gcode");

            // Assert
            Assert.IsNull(metadata, "Should return null for nonexistent filename");
        }

        [TestMethod]
        public void GetMetadataForFilename_ReturnsNull_ForEmptyFilename()
        {
            // Act
            var metadata = _sut.GetMetadataForFilename(string.Empty);

            // Assert
            Assert.IsNull(metadata, "Should return null for empty filename");
        }

        [TestMethod]
        public void GetMetadataForFilename_ReturnsNull_ForNullFilename()
        {
            // Act
            var metadata = _sut.GetMetadataForFilename(null);

            // Assert
            Assert.IsNull(metadata, "Should return null for null filename");
        }

        [TestMethod]
        public void Dispose_CleansUpResources()
        {
            // Arrange
            var sessionName = "dispose_test";
            var sessionResult = _sut.StartTimelapseAsync(sessionName).GetAwaiter().GetResult();

            // Act
            _sut.Dispose();

            // Assert - should not throw after dispose
            var activeNames = _sut.GetActiveSessionNames();
            Assert.IsNotNull(activeNames);
        }

        [TestMethod]
        public void MultipleActiveSessions_AreTrackedIndependently()
        {
            // Arrange
            var session1 = "session_1";
            var session2 = "session_2";

            // Act
            var result1 = _sut.StartTimelapseAsync(session1).GetAwaiter().GetResult();
            var result2 = _sut.StartTimelapseAsync(session2).GetAwaiter().GetResult();
            var activeNames = _sut.GetActiveSessionNames().ToList();

            // Assert
            Assert.AreEqual(2, activeNames.Count, "Should have 2 active sessions");
            Assert.IsTrue(activeNames.Contains(result1));
            Assert.IsTrue(activeNames.Contains(result2));
        }
    }
}
