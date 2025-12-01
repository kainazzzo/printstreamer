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
    public class TimelapseServiceTests : BaseTest<TimelapseService>
    {
        protected Mock<ILogger<TimelapseService>>? LoggerMock { get; set; }
        protected string? TempDir { get; set; }

        [TestInitialize]
        public override void TestInitialize()
        {
            base.TestInitialize();
            TempDir = Path.Combine(Path.GetTempPath(), $"timelapse_service_test_{Guid.NewGuid()}");
            Directory.CreateDirectory(TempDir);
            LoggerMock = new Mock<ILogger<TimelapseService>>();
        }

        [TestCleanup]
        public new void TestCleanup()
        {
            Sut?.Dispose();
            if (Directory.Exists(TempDir))
            {
                try
                {
                    Directory.Delete(TempDir, true);
                }
                catch { /* Ignore cleanup errors */ }
            }
            base.TestCleanup();
        }

        [TestMethod]
        public void Constructor_CreatesOutputDirectory()
        {
            // Act
            Sut = new TimelapseService(TempDir, "test_stream", LoggerMock!.Object);

            // Assert
            Assert.IsTrue(Directory.Exists(Sut.OutputDir), "Output directory should be created");
        }

        [TestMethod]
        public void Constructor_CreatesUniqueDirectoriesForDuplicateNames()
        {
            // Act
            var service1 = new TimelapseService(TempDir, "duplicate", LoggerMock!.Object);
            var service2 = new TimelapseService(TempDir, "duplicate", LoggerMock!.Object);

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
            Sut = new TimelapseService(TempDir, "output_test", LoggerMock!.Object);

            // Assert
            Assert.IsNotNull(Sut.OutputDir);
            Assert.IsTrue(Path.IsPathRooted(Sut.OutputDir), "Output directory should be an absolute path");
            Assert.IsTrue(Sut.OutputDir.StartsWith(TempDir), "Output directory should be under the temp directory");
        }

        [TestMethod]
        public void SaveFrameAsync_CreatesFrameFile()
        {
            // Arrange
            Sut = new TimelapseService(TempDir, "save_frame_test", LoggerMock!.Object);
            var frameData = new byte[] { 0xFF, 0xD8, 0xFF }; // JPEG header

            // Act
            Sut.SaveFrameAsync(frameData, CancellationToken.None).GetAwaiter().GetResult();

            // Assert
            var frameFiles = Directory.GetFiles(Sut.OutputDir, "frame_*.jpg");
            Assert.AreEqual(1, frameFiles.Length, "Should create exactly one frame file");
            Assert.AreEqual("frame_000000.jpg", Path.GetFileName(frameFiles[0]));
        }

        [TestMethod]
        public void SaveFrameAsync_IncrementFrameCount()
        {
            // Arrange
            Sut = new TimelapseService(TempDir, "frame_count_test", LoggerMock!.Object);
            var frameData = new byte[] { 0xFF, 0xD8, 0xFF };

            // Act
            Sut.SaveFrameAsync(frameData, CancellationToken.None).GetAwaiter().GetResult();
            Sut.SaveFrameAsync(frameData, CancellationToken.None).GetAwaiter().GetResult();
            Sut.SaveFrameAsync(frameData, CancellationToken.None).GetAwaiter().GetResult();

            // Assert
            var frameFiles = Directory.GetFiles(Sut.OutputDir, "frame_*.jpg").OrderBy(f => f).ToArray();
            Assert.AreEqual(3, frameFiles.Length, "Should have created 3 frame files");
            Assert.AreEqual("frame_000000.jpg", Path.GetFileName(frameFiles[0]));
            Assert.AreEqual("frame_000001.jpg", Path.GetFileName(frameFiles[1]));
            Assert.AreEqual("frame_000002.jpg", Path.GetFileName(frameFiles[2]));
        }

        [TestMethod]
        public void SaveFrameAsync_IgnoresNullData()
        {
            // Arrange
            Sut = new TimelapseService(TempDir, "null_frame_test", LoggerMock!.Object);

            // Act
            Sut.SaveFrameAsync(null, CancellationToken.None).GetAwaiter().GetResult();

            // Assert
            var frameFiles = Directory.GetFiles(Sut.OutputDir, "frame_*.jpg");
            Assert.AreEqual(0, frameFiles.Length, "Should not create file for null data");
        }

        [TestMethod]
        public void SaveFrameAsync_PreservesFrameData()
        {
            // Arrange
            Sut = new TimelapseService(TempDir, "frame_data_test", LoggerMock!.Object);
            var frameData = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 };

            // Act
            Sut.SaveFrameAsync(frameData, CancellationToken.None).GetAwaiter().GetResult();

            // Assert
            var frameFiles = Directory.GetFiles(Sut.OutputDir, "frame_*.jpg");
            var savedData = File.ReadAllBytes(frameFiles[0]);
            Assert.IsTrue(savedData.SequenceEqual(frameData), "Saved frame data should match original");
        }

        [TestMethod]
        public void SaveFrameAsync_ThreadSafe_WithConcurrentWrites()
        {
            // Arrange
            Sut = new TimelapseService(TempDir, "concurrent_save_test", LoggerMock!.Object);
            var frameData = new byte[] { 0xFF, 0xD8, 0xFF };
            var tasks = new List<System.Threading.Tasks.Task>();

            // Act - Write 10 frames concurrently
            for (int i = 0; i < 10; i++)
            {
                tasks.Add(Sut.SaveFrameAsync(frameData, CancellationToken.None));
            }
            System.Threading.Tasks.Task.WaitAll(tasks.ToArray());

            // Assert
            var frameFiles = Directory.GetFiles(Sut.OutputDir, "frame_*.jpg");
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
            Sut = new TimelapseService(TempDir, "no_frames_test", LoggerMock!.Object);
            var outputPath = Path.Combine(TempDir, "output.mp4");

            // Act
            var result = Sut.CreateVideoAsync(outputPath, 30, CancellationToken.None).GetAwaiter().GetResult();

            // Assert
            Assert.IsNull(result, "Should return null when no frames were captured");
            Assert.IsFalse(File.Exists(outputPath), "Output file should not be created");
        }

        [TestMethod]
        public void CreateVideoAsync_ReturnsNull_OnSecondCall()
        {
            // Arrange
            Sut = new TimelapseService(TempDir, "second_call_test", LoggerMock!.Object);
            var frameData = new byte[] { 0xFF, 0xD8, 0xFF };
            Sut.SaveFrameAsync(frameData, CancellationToken.None).GetAwaiter().GetResult();
            var outputPath = Path.Combine(TempDir, "output.mp4");

            // Act - First call
            var result1 = Sut.CreateVideoAsync(outputPath, 30, CancellationToken.None).GetAwaiter().GetResult();
            // Second call
            var result2 = Sut.CreateVideoAsync(outputPath, 30, CancellationToken.None).GetAwaiter().GetResult();

            // Assert
            Assert.IsNull(result2, "Second call to CreateVideoAsync should return null (already finalized)");
        }

        [TestMethod]
        public void CreateVideoAsync_CreatesOutputDirectory_IfNotExists()
        {
            // Arrange
            Sut = new TimelapseService(TempDir, "output_dir_test", LoggerMock!.Object);
            var frameData = new byte[] { 0xFF, 0xD8, 0xFF };
            Sut.SaveFrameAsync(frameData, CancellationToken.None).GetAwaiter().GetResult();
            
            var nestedOutputPath = Path.Combine(TempDir, "nested", "output", "video.mp4");
            var outputDir = Path.GetDirectoryName(nestedOutputPath);

            // Act
            var result = Sut.CreateVideoAsync(nestedOutputPath, 30, CancellationToken.None).GetAwaiter().GetResult();

            // Assert - Directory should be created even if ffmpeg fails
            Assert.IsTrue(Directory.Exists(outputDir), "Output directory should be created");
        }

        [TestMethod]
        public void Dispose_CanBeCalledMultipleTimes()
        {
            // Arrange
            Sut = new TimelapseService(TempDir, "dispose_multiple_test", LoggerMock!.Object);

            // Act & Assert - Should not throw
            Sut.Dispose();
            Sut.Dispose(); // Should not throw on second call
            Assert.IsTrue(Directory.Exists(Sut.OutputDir), "Output directory should still exist after disposal");
        }

        [TestMethod]
        public void SaveFrameAsync_WithLargeFrameData()
        {
            // Arrange
            Sut = new TimelapseService(TempDir, "large_frame_test", LoggerMock!.Object);
            var largeFrameData = new byte[1024 * 100]; // 100 KB
            new Random().NextBytes(largeFrameData);

            // Act
            Sut.SaveFrameAsync(largeFrameData, CancellationToken.None).GetAwaiter().GetResult();

            // Assert
            var frameFiles = Directory.GetFiles(Sut.OutputDir, "frame_*.jpg");
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
                var service = new TimelapseService(TempDir, streamId, LoggerMock!.Object);
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
