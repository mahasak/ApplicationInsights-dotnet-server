﻿namespace FunctionalTests
{
    using System.Diagnostics;
    using System.IO;

    using Functional.Helpers;
    using Functional.IisExpress;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using PerfCounterCollector.FunctionalTests;
    using System;
    using System.Collections.Generic;

    [TestClass]
    public class Test45 : SingleWebHostTestBase
    {
        private static Random rand = new Random();

        private const int TimeoutInMs = 15000;

        private const string TestWebApplicationSourcePath = @"TestApps\TestApp45\App";
        private const string TestWebApplicationDestPath = @"TestsPerformanceCollector45";

        [TestInitialize]
        public void TestInitialize()
        {
            var originalDirectory = Path.Combine(
                Directory.GetCurrentDirectory(),
                TestWebApplicationDestPath);

            var applicationDirectory = Path.Combine(
                Directory.GetCurrentDirectory(),
                string.Format("{0}_{1}", "app", this.TestContext.TestName));
            
            CopyFolder(originalDirectory, applicationDirectory);

            Trace.WriteLine("Application directory:" + applicationDirectory);

            // dynamic port range is [49152, 65535]
            int minPort = 49152;
            int maxPort = 65535;

            int iisPort = rand.Next(minPort, maxPort + 1);
            int telemetryPort = rand.Next(minPort, maxPort + 1);
            int quickPulsePort = rand.Next(minPort, maxPort + 1);

            this.StartWebAppHost(
                new SingleWebHostTestConfiguration(
                    new IisExpressConfiguration
                    {
                        ApplicationPool = IisExpressAppPools.Clr4IntegratedAppPool,
                        Path = applicationDirectory,
                        Port = iisPort,
                    })
                {
                    TelemetryListenerPort = telemetryPort,
                    QuickPulseListenerPort = quickPulsePort,
                    AttachDebugger = Debugger.IsAttached,
                    IKey = "fafa4b10-03d3-4bb0-98f4-364f0bdf5df8",
                });

            OverwriteFile(applicationDirectory, "ApplicationInsights.config", new Dictionary<string, string>() { ["{TelemetryEndpointPort}"] = telemetryPort.ToString() });
            OverwriteFile(applicationDirectory, "Web.config", new Dictionary<string, string>() { ["{QuickPulseEndpointPort}"] = quickPulsePort.ToString() });

            try
            {
                base.LaunchAndVerifyApplication();
            }
            catch (Exception ex)
            {
                Trace.WriteLine("Exception occured while verifying application, exception:" + ex.Message);
                this.StopWebAppHost();

                // Throwing to prevent tests from continuing to execute.
                throw ex;
            }
        }

        private static void OverwriteFile(string applicationDirectory, string fileName, Dictionary<string, string> replacements)
        {
            string filePath = Path.Combine(applicationDirectory, fileName);
            string fileContent = File.ReadAllText(filePath);

            foreach (var replacement in replacements)
            {
                fileContent = fileContent.Replace(replacement.Key, replacement.Value);
            }

            File.WriteAllText(filePath, fileContent);
        }

        [TestCleanup]
        public void TestCleanup()
        {
            this.StopWebAppHost();
        }

        [TestMethod]
        [Owner("alkaplan")]
        [DeploymentItem(TestWebApplicationSourcePath, TestWebApplicationDestPath)]
        public void DefaultCounterCollection()
        {
            CommonTests.DefaultCounterCollection(this.Listener);
        }

        [TestMethod]
        [Owner("alkaplan")]
        [DeploymentItem(TestWebApplicationSourcePath, TestWebApplicationDestPath)]
        public void CustomCounterCollection()
        {
            CommonTests.CustomCounterCollection(this.Listener);
        }

        [TestMethod]
        [Owner("alkaplan")]
        [Description("Tests that non existent counters are not collected and wont affect other counters")]
        [DeploymentItem(TestWebApplicationSourcePath, TestWebApplicationDestPath)]
        public void NonExistentCounter()
        {
            CommonTests.NonExistentCounter(this.Listener);
        }

        [TestMethod]
        [Owner("alkaplan")]
        [Description("Tests that non existent counters which use placeholders are not collected and wont affect other counters")]
        [DeploymentItem(TestWebApplicationSourcePath, TestWebApplicationDestPath)]
        public void NonExistentCounterWhichUsesPlaceHolder()
        {
            CommonTests.NonExistentCounterWhichUsesPlaceHolder(this.Listener);
        }

        [TestMethod]
        [Owner("alkaplan")]
        [DeploymentItem(TestWebApplicationSourcePath, TestWebApplicationDestPath)]
        public void NonParsableCounter()
        {
            CommonTests.NonParsableCounter(this.Listener);
        }

        [TestMethod]
        [Owner("alkaplan")]
        [DeploymentItem(TestWebApplicationSourcePath, TestWebApplicationDestPath)]
        public void QuickPulseAggregates()
        {
            CommonTests.QuickPulseAggregates(this.QuickPulseListener, this.HttpClient);
        }

        [TestMethod]
        [Owner("alkaplan")]
        [DeploymentItem(TestWebApplicationSourcePath, TestWebApplicationDestPath)]
        public void QuickPulseMetricsAndDocuments()
        {
            CommonTests.QuickPulseMetricsAndDocuments(this.QuickPulseListener, this);
        }

        [TestMethod]
        [Owner("alkaplan")]
        [DeploymentItem(TestWebApplicationSourcePath, TestWebApplicationDestPath)]
        public void QuickPulseTopCpuProcesses()
        {
            CommonTests.QuickPulseTopCpuProcesses(this.QuickPulseListener, this);
        }

        private static void CopyFolder(string sourcePath, string destinationPath)
        {
            foreach (string dirPath in Directory.GetDirectories(sourcePath, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dirPath.Replace(sourcePath, destinationPath));
            }

            foreach (string newPath in Directory.GetFiles(sourcePath, "*.*", SearchOption.AllDirectories))
            {
                File.Copy(newPath, newPath.Replace(sourcePath, destinationPath), true);
            }
        }
    }
}