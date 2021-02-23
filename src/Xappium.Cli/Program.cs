﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;
using Xappium.Android;
using Xappium.Apple;
using Xappium.BuildSystem;
using Xappium.Tools;

[assembly: InternalsVisibleTo("Xappium.Cli.Tests")]
namespace Xappium
{
    internal class Program
    {
        private const string ConfigFileName = "uitest.json";

        private string baseWorkingDirectory = Path.Combine(Environment.CurrentDirectory, "UITest");

        private FileInfo UITestProjectPathInfo => string.IsNullOrEmpty(UITestProjectPath) ? null : new FileInfo(UITestProjectPath);

        private FileInfo DeviceProjectPathInfo => string.IsNullOrEmpty(DeviceProjectPath) ? null : new FileInfo(DeviceProjectPath);

        public static Task<int> Main(string[] args)
            => CommandLineApplication.ExecuteAsync<Program>(args);

        [Option(Description = "Specifies the csproj path of the UI Test project",
            LongName = "uitest-project-path",
            ShortName = "uitest")]
        public string UITestProjectPath { get; }

        [Option(Description = "Specifies the Head Project csproj path for your iOS or Android project.",
            LongName = "app-project-path",
            ShortName = "app")]
        public string DeviceProjectPath { get; }

        [Option(Description = "Specifies the target platform such as iOS or Android.",
            LongName = "platform",
            ShortName = "p")]
        public string Platform { get; }

        [Option(Description = "Specifies the Build Configuration to use on the Platform head and UITest project",
            LongName = "configuration",
            ShortName = "c")]
        public string Configuration { get; } = "Release";

        [Option(Description = "Specifies a UITest.json configuration path that overrides what may be in the UITest project build output directory",
            LongName = "uitest-configuration",
            ShortName = "ui-config")]
        public string ConfigurationPath { get; }

        [SuppressMessage("CodeQuality", "IDE0051:Remove unused private members",
            Justification = "Called by McMaster")]
        private async Task<int> OnExecuteAsync()
        {
            IDisposable appium = null;
            try
            {
                if (!Node.IsInstalled)
                    throw new Exception("Your environment does not appear to have Node installed. This is required to run Appium");

                if (Directory.Exists(baseWorkingDirectory))
                    Directory.Delete(baseWorkingDirectory, true);

                Directory.CreateDirectory(baseWorkingDirectory);

                ValidatePaths();

                var headBin = Path.Combine(baseWorkingDirectory, "bin", "device");
                var uiTestBin = Path.Combine(baseWorkingDirectory, "bin", "uitest");

                Directory.CreateDirectory(headBin);
                Directory.CreateDirectory(uiTestBin);

                // HACK: the iOS SDK fails if the path does not end with a path separator.
                headBin += Path.DirectorySeparatorChar;
                uiTestBin += Path.DirectorySeparatorChar;

                await CSProjFile.Load(DeviceProjectPathInfo, new DirectoryInfo(uiTestBin), Platform)
                    .Build(Configuration)
                    .ConfigureAwait(false);
                await BuildUITestProject(uiTestBin).ConfigureAwait(false);

                GenerateTestConfig(headBin, uiTestBin);

                Appium.Install();
                appium = await Appium.Run(baseWorkingDirectory);

                try
                {
                    await DotNetTool.Test(UITestProjectPathInfo.FullName, uiTestBin, Configuration?.Trim(), Path.Combine(baseWorkingDirectory, "Results"));
                }
                catch(Exception)
                {
                    throw;
                }
                finally
                {
                    appium.Dispose();
                    appium = null;
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(ex.Message);
                Console.ResetColor();
                return 1;
            }
            finally
            {
                if(appium != null)
                {
                    appium.Dispose();
                }
            }

            return 0;
        }

        private void ValidatePaths()
        {
            if (UITestProjectPathInfo.Extension != ".csproj")
                throw new InvalidOperationException($"The path '{UITestProjectPath}' does not specify a valid csproj");
            else if (DeviceProjectPathInfo.Extension != ".csproj")
                throw new InvalidOperationException($"The path '{DeviceProjectPath}' does not specify a valid csproj");
            else if (!UITestProjectPathInfo.Exists)
                throw new FileNotFoundException($"The specified UI Test project path does not exist: '{UITestProjectPath}'");
            else if (!DeviceProjectPathInfo.Exists)
                throw new FileNotFoundException($"The specified Platform head project path does not exist: '{DeviceProjectPath}'");
        }

        private async Task BuildUITestProject(string uiTestBin)
        {
            var props = new Dictionary<string, string>
            {
                { "OutputPath", uiTestBin }
            };

            if (!string.IsNullOrEmpty(Configuration))
                props.Add("Configuration", Configuration);

            await NuGet.Restore(UITestProjectPathInfo.FullName).ConfigureAwait(false);
            await MSBuild.Build(UITestProjectPathInfo.FullName, baseWorkingDirectory, props).ConfigureAwait(false);
        }

        private void GenerateTestConfig(string headBin, string uiTestBin)
        {
            var binDir = new DirectoryInfo(headBin);
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                AllowTrailingCommas = true,
                IgnoreNullValues = true,
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            };

            var appPath = Platform switch
            {
                "Android" => binDir.GetFiles().First(x => x.Name.EndsWith("-Signed.apk")).FullName,
                "iOS" => binDir.GetDirectories().First(x => x.Name.EndsWith(".app")).FullName,
                _ => throw new PlatformNotSupportedException()
            };

            var config = new TestConfiguration();
            var testConfig = Path.Combine(uiTestBin, ConfigFileName);
            if (!string.IsNullOrEmpty(ConfigurationPath))
            {
                if (!File.Exists(ConfigurationPath))
                    throw new FileNotFoundException($"Could not locate the specified uitest configuration at: '{ConfigurationPath}'");
                config = JsonSerializer.Deserialize<TestConfiguration>(File.ReadAllText(ConfigurationPath), options);
            }
            else if(File.Exists(testConfig))
            {
                config = JsonSerializer.Deserialize<TestConfiguration>(File.ReadAllText(testConfig), options);
            }

            if (config.Capabilities is null)
                config.Capabilities = new Dictionary<string, string>();

            if (config.Settings is null)
                config.Settings = new Dictionary<string, string>();

            config.Platform = Platform;
            config.AppPath = appPath;

            if (string.IsNullOrEmpty(config.ScreenshotsPath))
                config.ScreenshotsPath = Path.Combine(baseWorkingDirectory, "Screenshots");

            switch(Platform)
            {
                case "Android":
                    // Ensure WebDrivers are installed
                    SdkManager.InstallWebDriver();

                    // Check for connected device
                    if (!Adb.DeviceIsConnected())
                    {
                        var sdkVersion = 29;
                        // Ensure SDK Installed
                        SdkManager.EnsureSdkIsInstalled(sdkVersion);

                        // Ensure Emulator Exists
                        if (!Emulator.ListEmulators().Any(x => x == AvdManager.DefaultUITestEmulatorName))
                            AvdManager.InstallEmulator(sdkVersion);

                        // Start Emulator
                        Emulator.StartEmulator(AvdManager.DefaultUITestEmulatorName);
                    }

                    var emulator = Adb.ListDevices().First();
                    config.DeviceName = emulator.Name;
                    config.UDID = emulator.Id;
                    config.OSVersion = $"{emulator.SdkVersion}";
                    break;
                case "iOS":
                    AppleSimulator.ShutdownAllSimulators();
                    var device = AppleSimulator.GetSimulator();
                    if (device is null)
                        throw new NullReferenceException("Unable to locate the Device");

                    config.DeviceName = device.Name;
                    config.UDID = device.Udid;
                    config.OSVersion = device.OSVersion;
                    break;
            }

            var jsonOutput = JsonSerializer.Serialize(config, options);
            File.WriteAllText(testConfig, jsonOutput);
            Console.WriteLine(jsonOutput);
        }
    }
}
