using Microsoft.VisualStudio.TestTools.UnitTesting;
using Bunit;
using printstreamer.Components.Shared;
using printstreamer.Components.Models;
using System;

namespace PrintStreamer.Utils.Tests
{
    [TestClass]
    public class TimelapseCardTests
    {
        private Bunit.BunitContext ctx = null!;

        [TestInitialize]
        public void Setup()
        {
            ctx = new Bunit.BunitContext();
        }

        [TestCleanup]
        public void Cleanup()
        {
            ctx.Dispose();
            ctx = null!;
        }

        [TestMethod]
        public void RendersRecordingHighlightWhenActive()
        {
            // Arrange
            var tl = new TimelapseInfo
            {
                Name = "TestTL",
                IsActive = true,
                FrameCount = 10,
                StartTime = DateTime.UtcNow,
                YouTubeUrl = "https://example.com" // prevent metadata fetch
            };

            // Act
            var cut = ctx.Render<TimelapseCard>(p => p
                .Add(x => x.Timelapse, tl)
                .Add(x => x.IsProcessing, false)
            );

            var root = cut.Find(".timelapse-card");

            // Assert
            Assert.IsTrue(root.ClassList.Contains("recording"), "Root element should contain 'recording' class when timelapse is active.");
            Assert.AreEqual("true", root.GetAttribute("data-recording"), "data-recording attribute should be 'true' when recording.");

            var badge = cut.Find(".status-badge");
            Assert.IsTrue(badge.TextContent.Trim().Equals("RECORDING"), "Status badge should show RECORDING when active.");
        }

        [TestMethod]
        public void DoesNotRenderRecordingHighlightWhenStopped()
        {
            // Arrange
            var tl = new TimelapseInfo
            {
                Name = "TestTL",
                IsActive = false,
                FrameCount = 0,
                StartTime = DateTime.UtcNow,
                YouTubeUrl = "https://example.com"
            };

            // Act
            var cut = ctx.Render<TimelapseCard>(p => p
                .Add(x => x.Timelapse, tl)
                .Add(x => x.IsProcessing, false)
            );

            var root = cut.Find(".timelapse-card");
            // Assert
            Assert.IsFalse(root.ClassList.Contains("recording"), "Root element should NOT contain 'recording' class when timelapse is not active.");
            Assert.AreEqual("false", root.GetAttribute("data-recording"), "data-recording attribute should be 'false' when not recording.");

            var badge = cut.Find(".status-badge");
            Assert.IsTrue(badge.TextContent.Trim().Equals("STOPPED"), "Status badge should show STOPPED when not active.");
        }
    }
}
