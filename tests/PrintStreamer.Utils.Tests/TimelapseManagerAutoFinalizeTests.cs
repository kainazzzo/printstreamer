using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using PrintStreamer.Timelapse;

namespace PrintStreamer.Utils.Tests
{
    [TestClass]
    public class TimelapseManagerAutoFinalizeTests : BaseTest<TimelapseManager>
    {
        protected string? TempTimelapseDir { get; set; }
        protected Mock<ILogger<TimelapseService>>? TimelapseServiceLoggerMock { get; set; }
        protected Mock<MoonrakerClient>? MoonrakerClientMock { get; set; }

        [TestInitialize]
        public override void TestInitialize()
        {
            base.TestInitialize();

            TempTimelapseDir = Path.Combine(Path.GetTempPath(), $"timelapse_autofinalize_{Guid.NewGuid()}");
            Directory.CreateDirectory(TempTimelapseDir);
            TimelapseServiceLoggerMock = new Mock<ILogger<TimelapseService>>();
            var moonrakerClientLoggerMock = new Mock<ILogger<MoonrakerClient>>();
            MoonrakerClientMock = new Mock<MoonrakerClient>(moonrakerClientLoggerMock.Object);

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Timelapse:MainFolder"] = TempTimelapseDir,
                    ["Stream:Source"] = "http://localhost:8080/stream.mjpeg",
                    ["Timelapse:AutoFinalize"] = "false"
                })
                .Build();

            Sut = new TimelapseManager(config, AutoMock.Mock<ILogger<TimelapseManager>>().Object, TimelapseServiceLoggerMock.Object, MoonrakerClientMock.Object);
        }

        [TestCleanup]
        public new void TestCleanup()
        {
            try
            {
                if (Directory.Exists(TempTimelapseDir!))
                    Directory.Delete(TempTimelapseDir!, true);
            }
            catch { /* ignore */ }
            base.TestCleanup();
        }

        [TestMethod]
        public void Manager_DoesNotRemoveSession_WhenAutoFinalizeDisabled()
        {
            try
            {
                // Start session
                var session = Sut.StartTimelapseAsync("job1").GetAwaiter().GetResult();
                Assert.IsNotNull(session);

                // Act - notify final layer
                var result = Sut.NotifyPrintProgressAsync(session, 10, 10).GetAwaiter().GetResult();

                // Assert - manager returned null (did not create video) and session remains active but stopped
                Assert.IsNull(result, "Manager should not create video when AutoFinalize=false");
                var active = Sut.GetActiveSessionNames().ToList();
                Assert.IsTrue(active.Contains(session), "Session should remain active when auto-finalize is disabled");

                // Confirm caller can call StopTimelapseAsync to finalize
                var created = Sut.StopTimelapseAsync(session).GetAwaiter().GetResult();
                // created may be null in test env (ffmpeg not available) but session should be removed
                var postActive = Sut.GetActiveSessionNames().ToList();
                Assert.IsFalse(postActive.Contains(session), "Session should be removed after explicit StopTimelapseAsync");
            }
            finally
            {
            }
        }
    }
}
