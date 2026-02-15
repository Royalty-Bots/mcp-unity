using System;
using System.Threading.Tasks;
using McpUnity.Services;
using McpUnity.Utils;
using Newtonsoft.Json.Linq;
using UnityEditor.TestTools.TestRunner.Api;

namespace McpUnity.Tools
{
    /// <summary>
    /// Starts a Unity Test Runner run and returns a run id immediately.
    /// </summary>
    public class StartTestRunTool : McpToolBase
    {
        private readonly ITestRunnerService _testRunnerService;

        public StartTestRunTool(ITestRunnerService testRunnerService)
        {
            Name = "start_test_run";
            Description = "Starts a Unity test run and returns a run id for polling.";
            IsAsync = true;
            _testRunnerService = testRunnerService;
        }

        public override async void ExecuteAsync(JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            string testModeStr = parameters?["testMode"]?.ToObject<string>() ?? "EditMode";
            string testFilter = parameters?["testFilter"]?.ToObject<string>();
            bool returnOnlyFailures = parameters?["returnOnlyFailures"]?.ToObject<bool>() ?? false;
            bool returnWithLogs = parameters?["returnWithLogs"]?.ToObject<bool>() ?? false;

            TestMode testMode = TestMode.EditMode;
            if (Enum.TryParse(testModeStr, true, out TestMode parsedMode))
            {
                testMode = parsedMode;
            }

            McpLogger.LogInfo($"Executing StartTestRunTool: Mode={testMode}, Filter={testFilter ?? "(none)"}");
            JObject result = await _testRunnerService.StartTestRunAsync(testMode, returnOnlyFailures, returnWithLogs, testFilter);
            tcs.SetResult(result);
        }
    }
}
