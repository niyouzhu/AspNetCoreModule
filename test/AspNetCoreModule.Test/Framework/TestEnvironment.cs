﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using Microsoft.AspNetCore.Server.IntegrationTesting;
using Microsoft.Extensions.Logging;
using System.Threading;
using Microsoft.Extensions.PlatformAbstractions;
using System.Linq;
using static AspNetCoreModule.Test.Framework.TestUtility;
using System.IO.Compression;
using System.Diagnostics;

namespace AspNetCoreModule.Test.Framework
{
    public class TestEnvironment : IDisposable
    {
        public WebSiteContext TestsiteContext;
        public WebAppContext RootAppContext;
        public WebAppContext StandardTestApp;
        public WebAppContext WebSocketApp;
        public WebAppContext URLRewriteApp;
        public TestUtility testHelper;
        private ILogger _logger;
        private int _iisExpressPid = -1;

        private static int _siteId = 81;
        private string postfix = string.Empty;

        public void Dispose()
        {
            TestUtility.LogTrace("End of test!!!");            

            if (_iisExpressPid != -1)
            {
                var iisExpressProcess = Process.GetProcessById(Convert.ToInt32(_iisExpressPid));
                iisExpressProcess.Kill();
                iisExpressProcess.WaitForExit();
            }
        }
        
        public TestEnvironment(IISConfigUtility.AppPoolBitness appPoolBitness, ServerType serverType = ServerType.IIS)
        {
            TestUtility.LogTrace("Start of E2ETestEnv");
            // 
            // System.Diagnostics.Debugger.Launch();
            //

            string solutionPath = GlobalTestEnvironment.GetSolutionDirectory();

            if (serverType == ServerType.IIS)
            {
                // BugBug: Private build of ANCM causes VSJitDebuger and that should be cleaned up here
                TestUtility.RestartServices(TestUtility.RestartOption.KillVSJitDebugger);
            }

            //
            // Initialize test machine
            //
            _logger = new LoggerFactory()
                    .AddConsole()
                    .CreateLogger(string.Format("P1"));
            testHelper = new TestUtility(_logger);

            //
            // Initialize context variables
            //
            string siteRootPath = string.Empty;
            string siteName = string.Empty;

            for (int i = 0; i < 3; i++)
            {
                string postfix = Path.GetRandomFileName();
                siteRootPath = Path.Combine(Environment.ExpandEnvironmentVariables("%SystemDrive%") + @"\", "inetpub", Path.GetRandomFileName());
                siteName = "StandardTestSite" + postfix;
                if (!Directory.Exists(siteRootPath))
                {
                    GlobalTestEnvironment.PostFixes.Add(postfix);
                    break;
                }
            }
            DirectoryCopy(Path.Combine(solutionPath, "test", "WebRoot"), siteRootPath);

            string standardAppRootPath = Path.Combine(siteRootPath, "StandardTestApp");
            string appPath = GetApplicationPath(ApplicationType.Portable);

            string publishPath = Path.Combine(appPath, "bin", "Debug", "netcoreapp1.1", "publish");
            if (!Directory.Exists(publishPath))
            {
                RunCommand("dotnet", "publish " + appPath);
            }
            bool checkPublishedFiles = false;
            string[] publishedFiles = Directory.GetFiles(publishPath);
            foreach (var item in publishedFiles)
            {
                if (Path.GetFileName(item) == "web.config")
                {
                    checkPublishedFiles = true;
                }
            }

            if (!checkPublishedFiles)
            {
                throw new System.ApplicationException("check published files at " + publishPath);
            }
            DirectoryCopy(publishPath, standardAppRootPath);

            int tcpPort = _siteId++;
            TestsiteContext = new WebSiteContext("localhost", siteName, tcpPort);
            RootAppContext = new WebAppContext("/", Path.Combine(siteRootPath, "WebSite1"), TestsiteContext);
            StandardTestApp = new WebAppContext("/StandardTestApp", standardAppRootPath, TestsiteContext);
            WebSocketApp = new WebAppContext("/WebSocket", Path.Combine(siteRootPath, "WebSocket"), TestsiteContext);
            URLRewriteApp = new WebAppContext("/URLRewriteApp", Path.Combine(siteRootPath, "URLRewrite"), TestsiteContext);

            string iisExpressConfigPath = null;
            if (serverType == ServerType.IISExpress)
            {
                iisExpressConfigPath = Path.Combine(siteRootPath, "http.config");
                FileCopy(Path.Combine(solutionPath, "test", "AspNetCoreModule.Test", "http.config"), iisExpressConfigPath);
            }

            //
            // Create sites and apps to applicationhost.config
            //
            using (var iisConfig = new IISConfigUtility(serverType, iisExpressConfigPath))
            {
                iisConfig.CreateAppPool(TestsiteContext.SiteName);
                if (appPoolBitness == IISConfigUtility.AppPoolBitness.enable32Bit)
                {
                    if (serverType == ServerType.IIS)
                    {
                        if (appPoolBitness == IISConfigUtility.AppPoolBitness.enable32Bit)
                        {
                            iisConfig.SetAppPoolSetting(RootAppContext.AppPoolName, "enable32BitAppOnWin64", true);
                        }
                        else
                        {
                            iisConfig.SetAppPoolSetting(RootAppContext.AppPoolName, "enable32BitAppOnWin64", false);
                        }
                    }
                }
                iisConfig.CreateSite(TestsiteContext.SiteName, RootAppContext.PhysicalPath, _siteId, TestsiteContext.TcpPort, TestsiteContext.SiteName);
                RootAppContext.RestoreFile("web.config");
                RootAppContext.DeleteFile("app_offline.htm");

                iisConfig.CreateApp(TestsiteContext.SiteName, StandardTestApp.Name, StandardTestApp.PhysicalPath);
                StandardTestApp.RestoreFile("web.config");
                StandardTestApp.DeleteFile("app_offline.htm");

                iisConfig.CreateApp(TestsiteContext.SiteName, WebSocketApp.Name, WebSocketApp.PhysicalPath);
                WebSocketApp.RestoreFile("web.config");
                WebSocketApp.DeleteFile("app_offline.htm");

                iisConfig.CreateApp(TestsiteContext.SiteName, URLRewriteApp.Name, URLRewriteApp.PhysicalPath);
                URLRewriteApp.RestoreFile("web.config");
                URLRewriteApp.DeleteFile("app_offline.htm");
            }

            if (serverType == ServerType.IISExpress)
            {
                if (Directory.Exists(Environment.ExpandEnvironmentVariables("%ProgramFiles(x86)%")) && appPoolBitness == IISConfigUtility.AppPoolBitness.enable32Bit)
                {
                    _iisExpressPid = RunCommand(Path.Combine(Environment.ExpandEnvironmentVariables("%ProgramFiles(x86)%"), "IIS Express", "iisexpress.exe"), "/siteid:" + _siteId + " /config:\"" + iisExpressConfigPath + "\"", true, false);
                }
                else
                {
                    _iisExpressPid = RunCommand(Path.Combine(Environment.ExpandEnvironmentVariables("%ProgramFiles%"), "IIS Express", "iisexpress.exe"), "/siteid:" + _siteId + " /config:\"" + iisExpressConfigPath + "\"", true, false);
                }
            }
        }
    }
}