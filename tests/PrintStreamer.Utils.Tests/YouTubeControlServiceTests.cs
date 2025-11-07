
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Autofac.Extras.Moq;
using PrintStreamer.Services;

namespace PrintStreamer.Utils.Tests
{
    [TestClass]
    public class YouTubeControlServiceTests
    {
        [TestMethod]
        [Ignore("AutoMock test with YouTubeControlService requires internal visibility setup")]
        public void AutoMock_Creates_YouTubeControlService_With_Mocked_Dependencies()
        {
            // Arrange
            using var mock = AutoMock.GetLoose();

            var configMock = new Mock<IConfiguration>();
            configMock.Setup(c => c[It.IsAny<string>()]).Returns(string.Empty);

            var loggerMock = new Mock<ILogger<YouTubeControlService>>();

            // Act: AutoMock should construct the YouTubeControlService directly
            var sut = mock.Create<YouTubeControlService>();

            // Assert
            Assert.IsNotNull(sut, "AutoMock failed to construct YouTubeControlService.");
        }
    }
}