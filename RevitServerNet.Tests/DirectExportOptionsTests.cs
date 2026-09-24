using System;
using System.IO;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using RevitServerNet.Enterprise;
using RevitServerNet.Extensions;
using Xunit;

namespace RevitServerNet.Tests
{
    /// <summary>
    /// Direct export: endpoint, version and host handling, and what happens before any server call.
    /// None of these tests contacts a server (one connects to a closed local port).
    /// </summary>
    public class DirectExportOptionsTests
    {
        [Theory]
        [InlineData("rs.local", "2022", "rs.local", 808, "/ModelService2022/ModelService.svc/tcpstream")]
        [InlineData("rs.local:9808", "2024", "rs.local", 9808, "/ModelService2024/ModelService.svc/tcpstream")]
        [InlineData(" 10.0.0.5 ", "2019", "10.0.0.5", 808, "/ModelService2019/ModelService.svc/tcpstream")]
        [InlineData("::1", "2026", "[::1]", 808, "/ModelService2026/ModelService.svc/tcpstream")]
        [InlineData("[::1]:9808", "2027", "[::1]", 9808, "/ModelService2027/ModelService.svc/tcpstream")]
        public void Endpoint_IsNetTcpModelServiceOfTheYear(string serverHost, string year, string host, int port, string path)
        {
            var uri = RsModelServiceEndpoint.BuildUri(serverHost, year);

            Assert.Equal("net.tcp", uri.Scheme);
            Assert.Equal(host, uri.Host);
            Assert.Equal(port, uri.Port);
            Assert.Equal(path, uri.AbsolutePath);
        }

        [Theory]
        [InlineData("http://rs.local")]
        [InlineData("rs.local/Projects")]
        [InlineData("user@rs.local")]
        [InlineData("rs.local?x=1")]
        [InlineData("rs.local#x")]
        [InlineData("rs local")]
        public void Endpoint_HostWithSchemePathOrJunk_IsRejected(string serverHost)
        {
            var ex = Assert.Throws<ArgumentException>(() => RsModelServiceEndpoint.BuildUri(serverHost, "2022"));
            Assert.Equal("ServerHost", ex.ParamName);
        }

        [Theory]
        [InlineData("2012")]
        [InlineData("2024")]
        [InlineData("2031")]
        public void Version_FourDigitYear_IsAccepted(string year)
        {
            RsModelServiceEndpoint.ValidateRevitVersion(year);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("24")]
        [InlineData("20241")]
        [InlineData("abcd")]
        [InlineData(" 2024")]
        [InlineData("2024 ")]
        [InlineData("20-4")]
        [InlineData("\uFF12\uFF10\uFF12\uFF14")] // full-width digits
        public void Version_NotAFourDigitYear_IsRejected(string year)
        {
            var ex = Assert.Throws<ArgumentException>(() => RsModelServiceEndpoint.ValidateRevitVersion(year));
            Assert.Equal("RevitVersion", ex.ParamName);
        }

        [Fact]
        public async Task ExportAsync_InvalidVersion_ThrowsBeforeTouchingTheDestination()
        {
            using (var temp = new TempDirectory())
            {
                var destination = temp.Path(@"out\model.rvt");
                var options = new ModelExporterOptions { ServerHost = "rs.local", ModelPipePath = "|P|m.rvt", DestinationFile = destination, RevitVersion = "24" };

                var ex = await Assert.ThrowsAsync<ArgumentException>(() => ModelExporter.ExportAsync(options));

                Assert.Contains("four-digit year", ex.Message);
                Assert.False(Directory.Exists(Path.GetDirectoryName(destination)));
            }
        }

        [Theory]
        [InlineData("rs.local:2022", "2024", "rs.local", "2024")]
        [InlineData("rs.local", "2022", "rs.local", "2022")]
        [InlineData("10.1.2.3:8080", "2012", "10.1.2.3", "2019")] // no year in a 2012 BaseUrl: falls back to 2019 as before
        [InlineData("[::1]:2022", "2023", "[::1]", "2023")]
        public void ExportModelAsync_TakesHostWithoutHttpPortAndYearFromBaseUrl(string apiHost, string serverVersion, string expectedHost, string expectedYear)
        {
            var api = new RevitServerApi(apiHost, "tester", false, serverVersion);

            var options = ExportExtensions.CreateOptions(api, "|P|m.rvt", @"C:\out\m.rvt", true, @"C:\rs", true);

            Assert.Equal(expectedHost, options.ServerHost);
            Assert.Equal(expectedYear, options.RevitVersion);
            Assert.True(options.CreateLocal);
            Assert.True(options.Overwrite);
            Assert.Equal(@"C:\rs", options.AssembliesPath);
            Assert.Equal("|P|m.rvt", options.ModelPipePath);
        }

        [Theory]
        [InlineData("rs.local:2022", "2024", null, "rs.local", "2024")]
        [InlineData("10.1.2.3:8080", "2012", null, "10.1.2.3", "2022")] // no year in a 2012 BaseUrl: falls back to 2022 as before
        [InlineData("rs.local", "2023", "2025", "rs.local", "2025")] // an explicit revitVersion wins over the BaseUrl year
        public void CreateLocalModelWithoutToolAsync_TakesHostWithoutHttpPortAndTheYear(string apiHost, string serverVersion, string revitVersion, string expectedHost, string expectedYear)
        {
            var api = new RevitServerApi(apiHost, "tester", false, serverVersion);

            var options = FolderExtensions.CreateLocalOptions(api, "|P|m.rvt", @"C:\out\m.rvt", true, revitVersion, @"C:\rs", true);
            var withDefaults = FolderExtensions.CreateLocalOptions(api, "|P|m.rvt", @"C:\out\m.rvt", false, revitVersion, null, false);

            Assert.Equal(expectedHost, options.ServerHost);
            Assert.Equal(expectedYear, options.RevitVersion);
            Assert.True(options.CreateLocal);
            Assert.True(options.Overwrite);
            Assert.Equal(@"C:\rs", options.AssembliesPath);
            Assert.Equal("|P|m.rvt", options.ModelPipePath);
            Assert.Equal(@"C:\out\m.rvt", options.DestinationFile);
            Assert.False(withDefaults.CreateLocal);
            Assert.False(withDefaults.Overwrite);
            Assert.Null(withDefaults.AssembliesPath);
        }

        /// <summary>
        /// The whole export path up to the first server call, with the bundled assemblies: loader, Autodesk type binding (including the
        /// Windsor container check), WCF channel creation for this runtime, error wrapping and work folder cleanup.
        /// </summary>
        [Fact]
        public async Task Export_UnreachableEndpoint_FailsInIdentifyModel_AndRemovesItsWorkFolder()
        {
            using (var temp = new TempDirectory())
            {
                var options = new RsModelExporterOptions
                {
                    // Nothing listens on port 1: the connection is refused.
                    ServerHost = "127.0.0.1:1",
                    ModelPipePath = "|P|m.rvt",
                    DestinationFile = temp.Path(@"out\m.rvt"),
                    RevitVersion = "2024",
                    WorkRoot = temp.Create("work"),
                };

                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new RsModelExporter().ExportAsync(options));

                Assert.StartsWith("IdentifyModel failed for model '|P|m.rvt' at net.tcp://127.0.0.1:1/ModelService2024/ModelService.svc/tcpstream", ex.Message);
                Assert.Contains("no ModelService endpoint responded", ex.Message);
                Assert.IsType<EndpointNotFoundException>(ex.InnerException);
                Assert.Empty(Directory.GetFileSystemEntries(options.WorkRoot));
                Assert.False(File.Exists(options.DestinationFile));
            }
        }

        [Fact]
        public async Task ExportAsync_CancelledBeforeStart_ThrowsWithoutAnySideEffect()
        {
            using (var temp = new TempDirectory())
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                var destination = temp.Path(@"out\model.rvt");
                // No server listens on this host: a server call would fail with another exception.
                var options = new ModelExporterOptions { ServerHost = "unreachable.invalid", ModelPipePath = "|P|m.rvt", DestinationFile = destination, RevitVersion = "2024" };

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ModelExporter.ExportAsync(options, null, cts.Token));

                Assert.False(Directory.Exists(Path.GetDirectoryName(destination)));
            }
        }

        [Fact]
        public async Task ExtensionOverloads_CancelledBeforeStart_Throw()
        {
            using (var temp = new TempDirectory())
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                var api = new RevitServerApi("unreachable.invalid", "tester", false, "2024");

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.ExportModelAsync("|P|m.rvt", temp.Path(@"a\m.rvt"), true, cancellationToken: cts.Token));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api.CreateLocalModelWithoutToolAsync("|P|m.rvt", temp.Path(@"b\m.rvt"), true, cancellationToken: cts.Token));

                Assert.False(Directory.Exists(temp.Path("a")));
                Assert.False(Directory.Exists(temp.Path("b")));
            }
        }

        [Fact]
        public async Task ExportAsync_DestinationExistsWithoutOverwrite_ThrowsBeforeContactingTheServer()
        {
            using (var temp = new TempDirectory())
            {
                var destination = temp.Path("model.rvt");
                File.WriteAllText(destination, "existing");
                var options = new ModelExporterOptions { ServerHost = "unreachable.invalid", ModelPipePath = "|P|m.rvt", DestinationFile = destination, RevitVersion = "2024" };

                await Assert.ThrowsAsync<IOException>(() => ModelExporter.ExportAsync(options));

                Assert.Equal("existing", File.ReadAllText(destination));
            }
        }
    }
}
