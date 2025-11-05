
namespace PrintStreamer.Utils.Tests
{
    [TestClass]
    public class YouTubeControlServiceTests
    {
        [TestMethod]
        public void AutoMock_Creates_YouTubeControlService_With_Mocked_Dependencies()
        {
            // Arrange
            using var mock = AutoMock.GetLoose();

            var configMock = new Mock<IConfiguration>();
            configMock.Setup(c => c[It.IsAny<string>()]).Returns(string.Empty);
            mock.Provide(configMock.Object);

            var loggerMock = new Mock<ILogger<YouTubeControlService>>();
            mock.Provide(loggerMock.Object);

            // Act: AutoMock should construct the YouTubeControlService directly
            var sut = mock.Create<YouTubeControlService>();

            // Assert
            Assert.IsNotNull(sut, "AutoMock failed to construct YouTubeControlService.");
        }
    }
}
