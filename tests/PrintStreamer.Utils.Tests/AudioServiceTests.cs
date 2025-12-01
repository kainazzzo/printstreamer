using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PrintStreamer.Services;

namespace PrintStreamer.Utils.Tests
{
    [TestClass]
    public class AudioServiceTests : BaseTest<AudioService>
    {
        protected string? TempDir { get; set; }

    [TestInitialize]
    public void Setup()
    {
        TempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(TempDir);

        File.WriteAllText(Path.Combine(TempDir, "a.mp3"), "x");
        File.WriteAllText(Path.Combine(TempDir, "b.mp3"), "x");
        File.WriteAllText(Path.Combine(TempDir, "c.mp3"), "x");

        var sectionMock = new Moq.Mock<Microsoft.Extensions.Configuration.IConfigurationSection>();
        sectionMock.Setup(s => s.Value).Returns(TempDir);
        AutoMock.Mock<IConfiguration>().Setup(c => c.GetSection("Audio:Folder")).Returns(sectionMock.Object);
        Sut = AutoMock.Create<AudioService>();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(TempDir))
        {
            try
            {
                Directory.Delete(TempDir, true);
            }
            catch { /* Ignore cleanup errors */ }
        }
    }

    [TestMethod]
    public void TrySelectRandomTrackSelectsFileFromLibrary()
    {
        Assert.IsFalse(string.IsNullOrWhiteSpace(Sut.Folder));
        Assert.IsTrue(Sut.Library.Count >= 3);

        var selected = Sut.TrySelectRandomTrack(out var path);
        Assert.IsTrue(selected, "TrySelectRandomTrack should return true when library is populated");
        Assert.IsFalse(string.IsNullOrWhiteSpace(path));
        Assert.IsTrue(File.Exists(path), "Selected path should exist on disk");
        Assert.AreEqual(Path.GetFileName(path), Sut.Current);
    }

    [TestMethod]
    public void DefaultShuffleIsDisabled()
    {
        var state = Sut.GetState();
        Assert.IsFalse(state.Shuffle, "Shuffle should be disabled by default");
    }
    }
}
