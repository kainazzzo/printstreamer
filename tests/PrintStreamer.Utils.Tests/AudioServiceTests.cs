using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PrintStreamer.Services;

namespace PrintStreamer.Utils.Tests
{
    [TestClass]
    public class AudioServiceTests
    {
        [TestMethod]
        public void TrySelectRandomTrackSelectsFileFromLibrary()
        {
            var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tmp);
            try
            {
                File.WriteAllText(Path.Combine(tmp, "a.mp3"), "x");
                File.WriteAllText(Path.Combine(tmp, "b.mp3"), "x");
                File.WriteAllText(Path.Combine(tmp, "c.mp3"), "x");

                var inMemory = new Dictionary<string, string?>
                {
                    ["Audio:Folder"] = tmp
                };
                var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
                var svc = new AudioService(config);

                Assert.IsFalse(string.IsNullOrWhiteSpace(svc.Folder));
                Assert.IsTrue(svc.Library.Count >= 3);

                var selected = svc.TrySelectRandomTrack(out var path);
                Assert.IsTrue(selected, "TrySelectRandomTrack should return true when library is populated");
                Assert.IsFalse(string.IsNullOrWhiteSpace(path));
                Assert.IsTrue(File.Exists(path), "Selected path should exist on disk");
                Assert.AreEqual(Path.GetFileName(path), svc.Current);
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        [TestMethod]
        public void DefaultShuffleIsDisabled()
        {
            var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(tmp);
            try
            {
                File.WriteAllText(Path.Combine(tmp, "a.mp3"), "x");
                var inMemory = new Dictionary<string, string?>
                {
                    ["Audio:Folder"] = tmp
                };
                var config = new ConfigurationBuilder().AddInMemoryCollection(inMemory).Build();
                var svc = new AudioService(config);
                var state = svc.GetState();
                Assert.IsFalse(state.Shuffle, "Shuffle should be disabled by default");
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }
    }
}
