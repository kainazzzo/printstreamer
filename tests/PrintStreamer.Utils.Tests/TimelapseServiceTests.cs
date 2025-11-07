using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;
using Moq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PrintStreamer.Utils.Tests
{
    [TestClass]
    public class TimelapseServiceTests
    {
        private Mock<ILogger<TimelapseService>> _loggerMock;
        private TimelapseService _sut;
        private string _tempDir;

        [TestInitialize]
        public void Setup()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"timelapse_service_test_{Guid.NewGuid()}");
            Directory.CreateDirectory(_tempDir);

            _loggerMock = new Mock<ILogger<TimelapseService>>();
        }

        [TestCleanup]
        public void Cleanup()
        {
            _sut?.Dispose();
            if (Directory.Exists(_tempDir))
            {
                try
                {
                    Directory.Delete(_tempDir, true);
                }
                catch { /* Ignore cleanup errors */ }
            }
        }

        [TestMethod]
        public void Constructor_CreatesOutputDirectory()
        {
            // Act
            _sut = new TimelapseService(_tempDir, "test_stream", _loggerMock.Object);

            // Assert
            Assert.IsTrue(Directory.Exists(_sut.OutputDir), "Output directory should be created");
        }

        [TestMethod]
        public void Constructor_CreatesUniqueDirectoriesForDuplicateNames()
        {
            // Act
            var service1 = new TimelapseService(_tempDir, "duplicate", _loggerMock.Object);
            var service2 = new TimelapseService(_tempDir, "duplicate", _loggerMock.Object);

            // Assert
            Assert.AreNotEqual(service1.OutputDir, service2.OutputDir, "Duplicate names should create different directories");
            Assert.IsTrue(service2.OutputDir.Contains("_1") || service2.OutputDir.Contains("_2"), 
                "Second service should have numeric suffix");

            service1.Dispose();
            service2.Dispose();
        }

        [TestMethod]
        public void OutputDir_ReturnsValidPath()
        {
            // Act
            _sut = new TimelapseService(_tempDir, "output_test", _loggerMock.Object);

            // Assert
            Assert.IsNotNull(_sut.OutputDir);
            Assert.IsTrue(Path.IsPathRooted(_sut.OutputDir), "Output directory should be an absolute path");
            Assert.IsTrue(_sut.OutputDir.StartsWith(_tempDir), "Output directory should be under the temp directory");
        }

        [TestMethod]
        public void SaveFrameAsync_CreatesFrameFile()
        {
            // Arrange
            _sut = new TimelapseService(_tempDir, "save_frame_test", _loggerMock.Object);
            var frameData = new byte[] { 0xFF, 0xD8, 0xFF }; // JPEG header

            // Act
            _sut.SaveFrameAsync(frameData, CancellationToken.None).GetAwaiter().GetResult();

            // Assert
            var frameFiles = Directory.GetFiles(_sut.OutputDir, "frame_*.jpg");
            Assert.AreEqual(1, frameFiles.Length, "Should create exactly one frame file");
            Assert.AreEqual("frame_000000.jpg", Path.GetFileName(frameFiles[0]));
        }

        [TestMethod]
        public void SaveFrameAsync_IncrementFrameCount()
        {
            // Arrange
            _sut = new TimelapseService(_tempDir, "frame_count_test", _loggerMock.Object);
            var frameData = new byte[] { 0xFF, 0xD8, 0xFF };

            // Act
            _sut.SaveFrameAsync(frameData, CancellationToken.None).GetAwaiter().GetResult();
            _sut.SaveFrameAsync(frameData, CancellationToken.None).GetAwaiter().GetResult();
            _sut.SaveFrameAsync(frameData, CancellationToken.None).GetAwaiter().GetResult();

            // Assert
            var frameFiles = Directory.GetFiles(_sut.OutputDir, "frame_*.jpg").OrderBy(f => f).ToArray();
            Assert.AreEqual(3, frameFiles.Length, "Should have created 3 frame files");
            Assert.AreEqual("frame_000000.jpg", Path.GetFileName(frameFiles[0]));
            Assert.AreEqual("frame_000001.jpg", Path.GetFileName(frameFiles[1]));
            Assert.AreEqual("frame_000002.jpg", Path.GetFileName(frameFiles[2]));
        }

        [TestMethod]
        public void SaveFrameAsync_IgnoresNullData()
        {
            // Arrange
            _sut = new TimelapseService(_tempDir, "null_frame_test", _loggerMock.Object);

            // Act
            _sut.SaveFrameAsync(null, CancellationToken.None).GetAwaiter().GetResult();

            // Assert
            var frameFiles = Directory.GetFiles(_sut.OutputDir, "frame_*.jpg");
            Assert.AreEqual(0, frameFiles.Length, "Should not create file for null data");
        }

        [TestMethod]
        public void SaveFrameAsync_PreservesFrameData()
        {
            // Arrange
            _sut = new TimelapseService(_tempDir, "frame_data_test", _loggerMock.Object);
            var frameData = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };

            // Act
            _sut.SaveFrameAsync(frameData, CancellationToken.None).GetAwaiter().GetResult();

            // Assert
            var frameFiles = Directory.GetFiles(_sut.OutputDir, "frame_*.jpg");
            var savedData = File.ReadAllBytes(frameFiles[0]);
            Assert.IsTrue(savedData.SequenceEqual(frameData), "Saved frame data should match original");
        }

        [TestMethod]
        public void SaveFrameAsync_ThreadSafe_WithConcurrentWrites()
        {
            // Arrange
            _sut = new TimelapseService(_tempDir, "concurrent_save_test", _loggerMock.Object);
            var frameData = new byte[] { 0xFF, 0xD8, 0xFF };
            var tasks = new List<System.Threading.Tasks.Task>();

            // Act - Write 10 frames concurrently
            for (int i = 0; i < 10; i++)
            {
                tasks.Add(_sut.SaveFrameAsync(frameData, CancellationToken.None));
            }
            System.Threading.Tasks.Task.WaitAll(tasks.ToArray());

            // Assert
            var frameFiles = Directory.GetFiles(_sut.OutputDir, "frame_*.jpg");
            Assert.AreEqual(10, frameFiles.Length, "Should have created 10 frame files safely");

            // Verify sequential naming
            var sortedFiles = frameFiles.OrderBy(f => Path.GetFileName(f)).ToArray();
            for (int i = 0; i < 10; i++)
            {
                var expectedName = $"frame_{i:D6}.jpg";
                Assert.AreEqual(expectedName, Path.GetFileName(sortedFiles[i]));
            }
        }

        [TestMethod]
        public void CreateVideoAsync_ReturnsNull_WhenNoFramesCaptured()
        {
            // Arrange
            _sut = new TimelapseService(_tempDir, "no_frames_test", _loggerMock.Object);
            var outputPath = Path.Combine(_tempDir, "output.mp4");

            // Act
            var result = _sut.CreateVideoAsync(outputPath, 30, CancellationToken.None).GetAwaiter().GetResult();

            // Assert
            Assert.IsNull(result, "Should return null when no frames were captured");
            Assert.IsFalse(File.Exists(outputPath), "Output file should not be created");
        }

        [TestMethod]
        public void CreateVideoAsync_ReturnsNull_OnSecondCall()
        {
            // Arrange
            _sut = new TimelapseService(_tempDir, "second_call_test", _loggerMock.Object);
            var frameData = new byte[] { 0xFF, 0xD8, 0xFF };
            _sut.SaveFrameAsync(frameData, CancellationToken.None).GetAwaiter().GetResult();
            var outputPath = Path.Combine(_tempDir, "output.mp4");

            // Act - First call
            var result1 = _sut.CreateVideoAsync(outputPath, 30, CancellationToken.None).GetAwaiter().GetResult();
            // Second call
            var result2 = _sut.CreateVideoAsync(outputPath, 30, CancellationToken.None).GetAwaiter().GetResult();

            // Assert
            Assert.IsNull(result2, "Second call to CreateVideoAsync should return null (already finalized)");
        }

        [TestMethod]
        public void CreateVideoAsync_CreatesOutputDirectory_IfNotExists()
        {
            // Arrange
            _sut = new TimelapseService(_tempDir, "output_dir_test", _loggerMock.Object);
            var frameData = new byte[] { 0xFF, 0xD8, 0xFF };
            _sut.SaveFrameAsync(frameData, CancellationToken.None).GetAwaiter().GetResult();
            
            var nestedOutputPath = Path.Combine(_tempDir, "nested", "output", "video.mp4");
            var outputDir = Path.GetDirectoryName(nestedOutputPath);

            // Act
            var result = _sut.CreateVideoAsync(nestedOutputPath, 30, CancellationToken.None).GetAwaiter().GetResult();

            // Assert - Directory should be created even if ffmpeg fails
            Assert.IsTrue(Directory.Exists(outputDir), "Output directory should be created");
        }

        [TestMethod]
        public void Dispose_CanBeCalledMultipleTimes()
        {
            // Arrange
            _sut = new TimelapseService(_tempDir, "dispose_multiple_test", _loggerMock.Object);

            // Act & Assert - Should not throw
            _sut.Dispose();
            _sut.Dispose(); // Should not throw on second call
            Assert.IsTrue(Directory.Exists(_sut.OutputDir), "Output directory should still exist after disposal");
        }

        [TestMethod]
        public void SaveFrameAsync_WithLargeFrameData()
        {
            // Arrange
            _sut = new TimelapseService(_tempDir, "large_frame_test", _loggerMock.Object);
            var largeFrameData = new byte[1024 * 100]; // 100 KB
            new Random().NextBytes(largeFrameData);

            // Act
            _sut.SaveFrameAsync(largeFrameData, CancellationToken.None).GetAwaiter().GetResult();

            // Assert
            var frameFiles = Directory.GetFiles(_sut.OutputDir, "frame_*.jpg");
            Assert.AreEqual(1, frameFiles.Length, "Should create frame file for large data");
            var fileInfo = new FileInfo(frameFiles[0]);
            Assert.AreEqual(1024 * 100, fileInfo.Length, "Saved file should match data size");
        }

        [TestMethod]
        public void Constructor_WithVariousStreamIds()
        {
            // Arrange & Act
            var streamIds = new[] { "simple", "with_underscore_name", "with-dashes", "with spaces" };
            var services = new List<TimelapseService>();

            foreach (var streamId in streamIds)
            {
                var service = new TimelapseService(_tempDir, streamId, _loggerMock.Object);
                services.Add(service);
            }

            // Assert
            Assert.AreEqual(4, services.Count);
            var outputDirs = services.Select(s => s.OutputDir).Distinct();
            Assert.AreEqual(4, outputDirs.Count(), "Each service should have unique output directory");

            foreach (var service in services)
            {
                service.Dispose();
            }
        }
    }
}
