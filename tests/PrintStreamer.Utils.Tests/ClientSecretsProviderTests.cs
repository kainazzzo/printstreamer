using System;
using System.IO;
using PrintStreamer.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PrintStreamer.Utils.Tests
{
    [TestClass]
    public class ClientSecretsProviderTests : BaseTest<ClientSecretsConfigurationProvider>
    {
        protected string? TempFile { get; set; }

        [TestInitialize]
        public override void TestInitialize()
        {
            // Call base to initialize AutoMock
            base.TestInitialize();
            
            TempFile = Path.Combine(Path.GetTempPath(), "client_secrets_test.json");

            var json = @"{
                ""installed"": {
                    ""client_id"": ""test-client-id"",
                    ""client_secret"": ""test-secret""
                }
            }";
            File.WriteAllText(TempFile, json);

            // Create the real ClientSecretsConfigurationSource (the dependency we're mocking)
            var source = new ClientSecretsConfigurationSource(TempFile, optional: false);
            
            // Create Sut with the mocked source dependency
            Sut = new ClientSecretsConfigurationProvider(source);
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (File.Exists(TempFile))
            {
                try
                {
                    File.Delete(TempFile);
                }
                catch { /* Ignore cleanup errors */ }
            }
        }

        [TestMethod]
        public void Loads_ClientId_And_ClientSecret_From_Installed_Shape()
        {
            Sut.Load();

            Assert.IsTrue(Sut.TryGet("YouTube:OAuth:ClientId", out var clientId));
            Assert.AreEqual("test-client-id", clientId);

            Assert.IsTrue(Sut.TryGet("YouTube:OAuth:ClientSecret", out var clientSecret));
            Assert.AreEqual("test-secret", clientSecret);
        }
    }
}
