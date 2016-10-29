﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Server.IntegrationTesting;
using Microsoft.AspNetCore.Testing.xunit;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Xunit;
using Xunit.Sdk;
using AspNetCoreModule.Test;
using AspNetCoreModule.Test.Utility;
using static AspNetCoreModule.Test.Utility.IISConfigUtility;
using System.IO;

namespace AspNetCoreModule.FunctionalTests
{
    public class ResponseTests : IClassFixture<UseLatestAncm>
    {
        [ConditionalTheory]
        [OSSkipCondition(OperatingSystems.Linux)]
        [OSSkipCondition(OperatingSystems.MacOSX)]
        [InlineData(ServerType.IISExpress, RuntimeFlavor.CoreClr, RuntimeArchitecture.x64, "http://localhost:5090/")]
        [InlineData(ServerType.IISExpress, RuntimeFlavor.Clr, RuntimeArchitecture.x64, "http://localhost:5091/")]
        public Task BasicTest(ServerType serverType, RuntimeFlavor runtimeFlavor, RuntimeArchitecture architecture, string applicationBaseUrl)
        {
            return ResponseFormats(AppPoolSettings.none, serverType, runtimeFlavor, architecture, applicationBaseUrl, CheckChunkedAsync, ApplicationType.Portable);
        }

        private Task ResponseFormats(object none, ServerType serverType, RuntimeFlavor runtimeFlavor, RuntimeArchitecture architecture, string applicationBaseUrl, Func<HttpClient, ILogger, Task> checkChunkedAsync, ApplicationType portable)
        {
            throw new NotImplementedException();
        }

        [SkipIfEnvironmentVariableNotEnabled("IIS_VARIATIONS_ENABLED")]
        [ConditionalTheory]
        [OSSkipCondition(OperatingSystems.Linux)]
        [OSSkipCondition(OperatingSystems.MacOSX)]
        [InlineData(AppPoolSettings.enable32BitAppOnWin64, ServerType.IIS, RuntimeFlavor.CoreClr, RuntimeArchitecture.x64, "http://localhost:5093/")]
        public Task BasicTestForIIS(AppPoolSettings appPoolSetting, ServerType serverType, RuntimeFlavor runtimeFlavor, RuntimeArchitecture architecture, string applicationBaseUrl)
        {
            return ResponseFormats(appPoolSetting, serverType, runtimeFlavor, architecture, applicationBaseUrl, CheckChangeNotificationAsync, ApplicationType.Portable);
        }
        
        public async Task ResponseFormats(AppPoolSettings appPoolSetting, ServerType serverType, RuntimeFlavor runtimeFlavor, RuntimeArchitecture architecture, string applicationBaseUrl, Func<HttpClient, ILogger, Task> scenario, ApplicationType applicationType)
        {
            if (serverType == ServerType.IIS)
            {
                IISConfigUtility.RestoreAppHostConfig(true);
            }

            var logger = new LoggerFactory()
                            .AddConsole()
                            .CreateLogger(string.Format("ResponseFormats:{0}:{1}:{2}:{3}", serverType, runtimeFlavor, architecture, applicationType));

            string solutionPath = UseLatestAncm.GetSolutionDirectory();
            string publishedApplicationRootPathBackup = Path.Combine(solutionPath, ".build", "publishedApplicationRootPath");
            
            using (logger.BeginScope("ResponseFormatsTest"))
            {
                string applicationPath = Helpers.GetApplicationPath(applicationType);
                string testSiteName = "ANCMTestSite"; // This is configured in the Http.config
                var deploymentParameters = new DeploymentParameters(applicationPath, serverType, runtimeFlavor, architecture)
                {
                    ApplicationBaseUriHint = applicationBaseUrl,
                    EnvironmentName = "Response",
                    ServerConfigTemplateContent = Helpers.GetConfigContent(serverType, "Http.config"),
                    SiteName = testSiteName, 
                    TargetFramework = runtimeFlavor == RuntimeFlavor.Clr ? "net451" : "netcoreapp1.0",
                    ApplicationType = applicationType,
                    PublishApplicationBeforeDeployment = true
                };
                
                using (var deployer = ApplicationDeployerFactory.Create(deploymentParameters, logger))
                {
                    var deploymentResult = deployer.Deploy();

                    if (!Directory.Exists(publishedApplicationRootPathBackup))
                    {
                        TestUtility.DirectoryCopy(deploymentParameters.PublishedApplicationRootPath, publishedApplicationRootPathBackup);
                    }
                    var applicationBaseAddress = new Uri(deploymentResult.ApplicationBaseUri);

                    var httpClientHandler = new HttpClientHandler();
                    var httpClient = new HttpClient(httpClientHandler)
                    {
                        BaseAddress = applicationBaseAddress,
                        Timeout = TimeSpan.FromSeconds(5),
                    };

                    // Request to base address and check if various parts of the body are rendered & measure the cold startup time.
                    var response = await RetryHelper.RetryRequest(() =>
                    {
                        return httpClient.GetAsync(string.Empty);
                    }, logger, deploymentResult.HostShutdownToken);

                    var responseText = await response.Content.ReadAsStringAsync();
                    try
                    {
                        Assert.Equal("Running", responseText);
                    }
                    catch (XunitException)
                    {
                        logger.LogWarning(response.ToString());
                        logger.LogWarning(responseText);
                        throw;
                    }

                    await scenario(httpClient, logger);

                    if (serverType == ServerType.IIS)
                    {
                        using (var iisConfig = new IISConfigUtility(serverType))
                        {
                            iisConfig.EnableUrlRewriteToIIS();

                            var testsiteContext = new SiteContext("localhost", "StandardTestSite", 1234);
                            string webRootPath = Path.Combine(solutionPath, "test", "WebRoot", "WebSite1");

                            var rootApp = new AppContext("/", webRootPath, testsiteContext);
                            iisConfig.CreateSite(testsiteContext.SiteName, rootApp.PhysicalPath, 555, testsiteContext.TcpPort, rootApp.AppPoolName);

                            if (appPoolSetting == AppPoolSettings.enable32BitAppOnWin64)
                            {
                                iisConfig.StopAppPool(rootApp.AppPoolName);
                                iisConfig.SetAppPoolSetting(rootApp.AppPoolName, AppPoolSettings.enable32BitAppOnWin64, true);
                                iisConfig.StartAppPool(rootApp.AppPoolName);
                                //IISConfigUtility.RestartServices(2);
                            }

                            var fooApp = new AppContext("/foo", publishedApplicationRootPathBackup, testsiteContext);
                            iisConfig.CreateApp(testsiteContext.SiteName, fooApp.Name, fooApp.PhysicalPath);

                            var httpClientHandler2 = new HttpClientHandler();
                            var httpClient2 = new HttpClient(httpClientHandler2)
                            {
                                BaseAddress = fooApp.GetHttpUri(),
                                Timeout = TimeSpan.FromSeconds(5),
                            };

                            await scenario(httpClient2, logger);
                        }
                    }
                }
            }
        }

        private static async Task CheckChunkedAsync(HttpClient client, ILogger logger)
        {
            var response = await client.GetAsync("chunked");
            var responseText = await response.Content.ReadAsStringAsync();
            try
            {
                Assert.Equal("Chunked", responseText);
                Assert.True(response.Headers.TransferEncodingChunked, "/chunked, chunked?");
                Assert.Null(response.Headers.ConnectionClose);
                Assert.Null(GetContentLength(response));
            }
            catch (XunitException)
            {
                logger.LogWarning(response.ToString());
                logger.LogWarning(responseText);
                throw;
            }
        }

        private static async Task CheckChangeNotificationAsync(HttpClient client, ILogger logger)
        {
            var response = await client.GetAsync("manuallychunked");
            var responseText = await response.Content.ReadAsStringAsync();
            try
            {
                Assert.Equal("Manually Chunked", responseText);
                Assert.True(response.Headers.TransferEncodingChunked, "/manuallychunked, chunked?");
                Assert.Null(response.Headers.ConnectionClose);
                Assert.Null(GetContentLength(response));
            }
            catch (XunitException)
            {
                logger.LogWarning(response.ToString());
                logger.LogWarning(responseText);
                throw;
            }
        }
        
        private static string GetContentLength(HttpResponseMessage response)
        {
            // Don't use response.Content.Headers.ContentLength, it will dynamically calculate the value if it can.
            IEnumerable<string> values;
            return response.Content.Headers.TryGetValues(HeaderNames.ContentLength, out values) ? values.FirstOrDefault() : null;
        }
    }
}