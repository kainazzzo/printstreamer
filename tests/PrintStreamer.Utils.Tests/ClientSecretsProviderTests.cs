using System;
using System.IO;
using PrintStreamer.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PrintStreamer.Utils.Tests
{
    [TestClass]
    public class ClientSecretsProviderTests
    {
        [TestMethod]
        public void Loads_ClientId_And_ClientSecret_From_Installed_Shape()
        {
            var temp = Path.Combine(Path.GetTempPath(), "client_secrets_test.json");
            try
            {
                var json = "{\"installed\":{\"client_id\":\"test-client-id\",\"client_secret\":\"test-secret\"}}";
                File.WriteAllText(temp, json);

                var source = new ClientSecretsConfigurationSource(temp, optional: false);
                var provider = new ClientSecretsConfigurationProvider(source);
                provider.Load();

                Assert.IsTrue(provider.TryGet("YouTube:OAuth:ClientId", out var clientId));
                Assert.AreEqual("test-client-id", clientId);

                Assert.IsTrue(provider.TryGet("YouTube:OAuth:ClientSecret", out var clientSecret));
                Assert.AreEqual("test-secret", clientSecret);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }
    }
}
