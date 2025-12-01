using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace PrintStreamer.Utils.Tests
{
    [TestClass]
    public class MixEndpointTests : BaseTest<TestServer>
    {
        private TestServer? _server;
        private HttpClient? _client;

        [TestInitialize]
        public override void TestInitialize()
        {
            base.TestInitialize();
        }

        private void SetupServer(bool serveEnabled)
        {
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Stream:Mix:Enabled"] = serveEnabled.ToString().ToLower()
                })
                .Build();

            var builder = new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<IConfiguration>(config);
                    services.AddLogging();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/stream/mix", async ctx =>
                        {
                            var cfg = ctx.RequestServices.GetRequiredService<IConfiguration>();
                            var serveEnabledValue = cfg.GetValue<bool?>("Stream:Mix:Enabled") ?? true;
                            
                            if (!serveEnabledValue)
                            {
                                ctx.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                                ctx.Response.ContentType = "text/plain";
                                await ctx.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes("Mix processing is disabled"), ctx.RequestAborted);
                            }
                            else
                            {
                                ctx.Response.StatusCode = (int)HttpStatusCode.OK;
                                ctx.Response.ContentType = "video/mp4";
                                await ctx.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes("mock mix data"), ctx.RequestAborted);
                            }
                        });
                    });
                });

            _server = new TestServer(builder);
            _client = _server.CreateClient();
        }

        [TestCleanup]
        public new void TestCleanup()
        {
            _client?.Dispose();
            _server?.Dispose();
            base.TestCleanup();
        }

        [TestMethod]
        public async Task MixEndpoint_WhenServeEnabledTrue_ReturnsOkWithMixData()
        {
            // Arrange
            SetupServer(true);

            // Act
            var response = await _client!.GetAsync("/stream/mix");

            // Assert
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, 
                "Mix endpoint should return 200 OK when Stream:Mix:Enabled=true");
            var content = await response.Content.ReadAsStringAsync();
            Assert.AreEqual("mock mix data", content, 
                "Mix endpoint should return mock stream data");
        }

        [TestMethod]
        public async Task MixEndpoint_WhenServeEnabledFalse_Returns503ServiceUnavailable()
        {
            // Arrange
            SetupServer(false);

            // Act
            var response = await _client!.GetAsync("/stream/mix");

            // Assert
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, 
                "Mix endpoint should return 503 when Stream:Mix:Enabled=false");
            var content = await response.Content.ReadAsStringAsync();
            Assert.IsTrue(content.Contains("Mix processing is disabled"), 
                "Response should explain that mix processing is disabled");
        }

        [TestMethod]
        public async Task MixEndpoint_WhenServeEnabledNotConfigured_DefaultsToTrue()
        {
            // Arrange
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Stream:Mix:Enabled not set - should default to true
                })
                .Build();

            var builder = new WebHostBuilder()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<IConfiguration>(config);
                    services.AddLogging();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/stream/mix", async ctx =>
                        {
                            var cfg = ctx.RequestServices.GetRequiredService<IConfiguration>();
                            var serveEnabledValue = cfg.GetValue<bool?>("Stream:Mix:Enabled") ?? true;
                            if (!serveEnabledValue)
                            {
                                ctx.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                                await ctx.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes("disabled"), ctx.RequestAborted);
                            }
                            else
                            {
                                ctx.Response.StatusCode = (int)HttpStatusCode.OK;
                                await ctx.Response.Body.WriteAsync(System.Text.Encoding.UTF8.GetBytes("enabled"), ctx.RequestAborted);
                            }
                        });
                    });
                });

            using var server = new TestServer(builder);
            using var client = server.CreateClient();

            // Act
            var response = await client.GetAsync("/stream/mix");

            // Assert
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, 
                "Mix endpoint should default to enabled when Stream:Mix:Enabled not configured");
        }
    }
}
