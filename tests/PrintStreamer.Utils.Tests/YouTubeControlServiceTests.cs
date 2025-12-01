
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Autofac.Extras.Moq;
using PrintStreamer.Services;

namespace PrintStreamer.Utils.Tests
{
    [TestClass]
    public class YouTubeControlServiceTests : BaseTest<YouTubeControlService>
    {
        [TestMethod]
        [Ignore("AutoMock test with YouTubeControlService requires internal visibility setup")]
        public void AutoMock_Creates_YouTubeControlService_With_Mocked_Dependencies()
        {
            // Arrange - Already have AutoMock container from BaseTest
            var configMock = new Mock<IConfiguration>();
            configMock.Setup(c => c[It.IsAny<string>()]).Returns(string.Empty);

            var loggerMock = new Mock<ILogger<YouTubeControlService>>();

            // Act: AutoMock should construct the YouTubeControlService directly
            Sut = AutoMock.Create<YouTubeControlService>();

            // Assert
            Assert.IsNotNull(Sut, "AutoMock failed to construct YouTubeControlService.");
        }
    }
}