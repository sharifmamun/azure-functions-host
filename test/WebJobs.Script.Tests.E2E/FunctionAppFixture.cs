﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace WebJobs.Script.EndToEndTests
{
    public class FunctionAppFixture : IDisposable
    {
        private bool _disposed;
        private KuduClient _kuduClient;
        private readonly TrackedAssert _assert;
        private readonly ILogger _logger;

        public FunctionAppFixture()
        {
            ILoggerFactory loggerFactory = new LoggerFactory().AddConsole();
            _logger = loggerFactory.CreateLogger<FunctionAppFixture>();

            _assert = new TrackedAssert(Telemetry);

            FunctionDefaultKey = Guid.NewGuid().ToString().ToLower();

            Initialize().Wait();
        }

        public TrackedAssert Assert => _assert;

        public TelemetryClient Telemetry => TelemetryContext.Client;

        public KuduClient KuduClient => _kuduClient;

        public string FunctionDefaultKey { get; }

        public string FunctionAppMasterKey { get; private set; }

        private async Task Initialize()
        {
            Telemetry.TrackEvent(new EventTelemetry("EnvironmentInitialization"));
            _logger.LogInformation("Initializing environment...");

            _kuduClient = new KuduClient($"https://{Settings.SiteName}.scm.azurewebsites.net", Settings.SitePublishingUser, Settings.SitePublishingPassword);
            FunctionAppMasterKey = await _kuduClient.GetFunctionsMasterKey();

            // to run tests against currently deployed site, skip
            // this step (can take over 2 minutes)
            await RedeployTestSite();

            // ensure that our current client key is set as
            // the default key for all http functions
            await InitializeFunctionKeys();

            // after all initialization is done, do a final restart for good measure
            await RestartSite();

            _logger.LogInformation("Environment initialized");
            Telemetry.TrackEvent(new EventTelemetry("EnvironmentInitialized"));

            _logger.LogInformation("Test run starting...");
        }

        private async Task RedeployTestSite()
        {
            await StopSite();

            await AddSettings();

            await UpdateRuntime();

            await UpdateSiteContents();

            await StartSite();

            await WaitForSite();
        }

        private async Task InitializeFunctionKeys()
        {
            _logger.LogInformation("Initializing keys...");

            using (var keysUpdate = Telemetry.StartOperation<RequestTelemetry>("KeysUpdate"))
            {
                List<Function> functions = await _kuduClient.GetFunctions();
                using (var client = new HttpClient())
                {
                    client.BaseAddress = Settings.SiteBaseAddress;

                    // update the default key for all http functions
                    var requests = new List<Task<HttpResponseMessage>>();
                    foreach (var function in functions.Where(f => f.Configuration.Bindings.Any(b => string.Equals(b.Type, "httpTrigger", StringComparison.OrdinalIgnoreCase))))
                    {
                        Task<HttpResponseMessage> requestTask = client.PutAsJsonAsync($"/admin/functions/{function.Name}/keys/default?code={FunctionAppMasterKey}", new { name = "default", value = FunctionDefaultKey });
                        requests.Add(requestTask);
                    }

                    await Task.WhenAll(requests);

                    requests.ForEach(t => t.Result.EnsureSuccessStatusCode());
                }
            }
        }
        
        private async Task WaitForSite()
        {
            _logger.LogInformation("Waiting for site...");

            TimeSpan delay = TimeSpan.FromSeconds(3);
            int attemptsCount = 5;

            using (var client = new HttpClient())
            {
                HttpStatusCode statusCode;
                int attempts = 0;
                do
                {
                    if (attempts++ > 0)
                    {
                        await Task.Delay(delay);
                    }

                    // Workaround for https://github.com/Azure/azure-functions-host/issues/2397 as the base URL
                    // doesn't currently start the host.
                    // var result = await client.GetAsync($"{Settings.SiteBaseAddress}");
                    var functions = await _kuduClient.GetFunctions();
                    var result = await client.GetAsync($"{Settings.SiteBaseAddress}/admin/functions/{functions.First().Name}/status?code={FunctionAppMasterKey}");                    
                    statusCode = result.StatusCode;
                    if (statusCode == HttpStatusCode.OK)
                    {
                        _logger.LogInformation("Site is up and running!");
                        return;
                    }
                }
                while (attempts < attemptsCount);
                
            }

            throw new InvalidOperationException("Wait for site timeout: {delay.TotalSeconds * 5} seconds.");
            
        }

        private async Task AddSettings()
        {
            _logger.LogInformation("Updating app settings...");
            Telemetry.TrackEvent("SettingsUpdate");

            await AddAppSetting(Constants.ServiceBusKey, Environment.GetEnvironmentVariable(Constants.ServiceBusKey));

            _logger.LogInformation("App settings updated");
        }

        private async Task UpdateSiteContents()
        {
            _logger.LogInformation("Updating site contents...");
            Telemetry.TrackEvent("ContentUpdate");

            await _kuduClient.DeleteDirectory("site/wwwroot", true);
            string filePath = Path.ChangeExtension(Path.GetTempFileName(), "zip");
            ZipFile.CreateFromDirectory(Path.Combine(Environment.CurrentDirectory, "Functions"), filePath);

            await _kuduClient.UploadZip("site", filePath);

            _logger.LogInformation("Site contents updated");
        }

        private async Task UpdateRuntime()
        {
            _logger.LogInformation($"Updating runtime from: {Settings.RuntimeExtensionPackageUrl}");
            Telemetry.TrackEvent("RuntimeUpdate", new Dictionary<string, string> { { "runtimeSource", Settings.RuntimeExtensionPackageUrl } });

            string extensionFilePath = Path.ChangeExtension(Path.GetTempFileName(), "zip");
            Uri packageUri = new Uri(Settings.RuntimeExtensionPackageUrl);
            if (packageUri.IsFile)
            {
                File.Copy(Settings.RuntimeExtensionPackageUrl, extensionFilePath);
            }
            else
            {
                using (var client = new HttpClient())
                {
                    HttpResponseMessage response = await client.GetAsync(Settings.RuntimeExtensionPackageUrl);
                    response.EnsureSuccessStatusCode();

                    using (var fileStream = File.OpenWrite(extensionFilePath))
                    {
                        await response.Content.CopyToAsync(fileStream);
                    }
                }
            }

            await _kuduClient.DeleteDirectory("SiteExtensions", true);
            await _kuduClient.UploadZip("/", extensionFilePath);

            _logger.LogInformation($"Updated to function runtime version: {Settings.RuntimeVersion}");
        }

        public async Task RestartSite()
        {
            _logger.LogInformation("Restarting site...");

            await IssueSiteCommand($"/subscriptions/{Settings.SiteSubscriptionId}/resourceGroups/{Settings.SiteResourceGroup}/providers/Microsoft.Web/sites/{Settings.SiteName}/restart?api-version=2015-08-01&softRestart=true&synchronous=true");

            _logger.LogInformation("Site restarted");
        }

        public async Task StopSite()
        {
            _logger.LogInformation("Stopping site...");

            await IssueSiteCommand($"/subscriptions/{Settings.SiteSubscriptionId}/resourceGroups/{Settings.SiteResourceGroup}/providers/Microsoft.Web/sites/{Settings.SiteName}/stop?api-version=2015-08-01");

            _logger.LogInformation("Site stopped");
        }

        public async Task StartSite()
        {
            _logger.LogInformation("Starting site...");

            await IssueSiteCommand($"/subscriptions/{Settings.SiteSubscriptionId}/resourceGroups/{Settings.SiteResourceGroup}/providers/Microsoft.Web/sites/{Settings.SiteName}/start?api-version=2015-08-01");

            _logger.LogInformation("Site started");
        }

        public async Task AddAppSetting(string name, string value)
        {
            string updateUri = $"/subscriptions/{Settings.SiteSubscriptionId}/resourceGroups/{Settings.SiteResourceGroup}/providers/Microsoft.Web/sites/{Settings.SiteName}/config/appSettings";
            string listUri = $"{updateUri}/list";

            // We need to roundtrip these settings
            JObject settings = await IssueSiteCommand($"{listUri}?api-version=2015-08-01", delayInMs: 0);

            // add or overwrite the existing setting
            settings["properties"][name] = value;

            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Put, $"{updateUri}?api-version=2015-08-01")
            {
                Content = new StringContent(settings.ToString(Formatting.None), Encoding.UTF8, "application/json")
            };

            // Site is stopped; no need to wait.
            await IssueSiteCommand(request, delayInMs: 0);
        }

        private async Task<JObject> IssueSiteCommand(string commandUri, int delayInMs = 5000)
        {
            return await IssueSiteCommand(new HttpRequestMessage(HttpMethod.Post, commandUri), delayInMs);
        }

        private async Task<JObject> IssueSiteCommand(HttpRequestMessage request, int delayInMs = 5000)
        {
            string token = await ArmAuthenticationHelpers.AcquireTokenBySPN(Settings.SiteTenantId, Settings.SiteApplicationId, Settings.SiteClientSecret);

            JObject responseContent = null;
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
                client.BaseAddress = new Uri("https://management.azure.com/");

                using (var response = await client.SendAsync(request))
                {
                    response.EnsureSuccessStatusCode();
                    responseContent = await response.Content.ReadAsAsync<JObject>();
                }
            }

            await Task.Delay(delayInMs);

            return responseContent;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _kuduClient?.Dispose();
                    Telemetry.Flush();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }
    }

    [CollectionDefinition(Constants.FunctionAppCollectionName)]
    public class FunctionAppCollection : ICollectionFixture<FunctionAppFixture>
    {
        // This class has no code, and is never created. Its purpose is simply
        // to be the place to apply [CollectionDefinition] and all the
        // ICollectionFixture<> interfaces.
    }

}
