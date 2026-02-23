using System;
using System.Threading;
using System.Threading.Tasks;
using McpUnity.Unity;
using UnityEngine;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using UnityEditor.TestTools.TestRunner.Api;
using McpUnity.Services;
using McpUnity.Utils;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for running Unity Test Runner tests
    /// </summary>
    public class RunTestsTool : McpToolBase
    {
        private readonly ITestRunnerService _testRunnerService;

        public RunTestsTool(ITestRunnerService testRunnerService)
        {
            Name = "run_tests";
            Description = "Runs tests using Unity's Test Runner";
            IsAsync = true;
            _testRunnerService = testRunnerService;
        }
        
        /// <summary>
        /// Executes the RunTests tool asynchronously on the main thread.
        /// </summary>
        /// <param name="parameters">Tool parameters, including optional 'testMode' and 'testFilter'.</param>
        /// <param name="tcs">TaskCompletionSource to set the result or exception.</param>
        public override async void ExecuteAsync(JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            // Parse parameters
            string testModeStr = parameters?["testMode"]?.ToObject<string>() ?? "EditMode";
            string testFilter = parameters?["testFilter"]?.ToObject<string>(); // Optional
            bool returnOnlyFailures = parameters?["returnOnlyFailures"]?.ToObject<bool>() ?? false; // Optional
            bool returnWithLogs = parameters?["returnWithLogs"]?.ToObject<bool>() ?? false; // Optional

            TestMode testMode = TestMode.EditMode;
            
            if (Enum.TryParse(testModeStr, true, out TestMode parsedMode))
            {
                testMode = parsedMode;
            }

            McpLogger.LogInfo($"Executing RunTestsTool: Mode={testMode}, Filter={testFilter ?? "(none)"}");

            if (testMode != TestMode.PlayMode)
            {
                JObject editModeResult = await _testRunnerService.ExecuteTestsAsync(testMode, returnOnlyFailures, returnWithLogs, testFilter);
                tcs.SetResult(editModeResult);
                return;
            }

            // PlayMode test runs can interrupt the websocket connection during the mode switch.
            // Start the run first, then poll status from Unity-side state until completion.
            JObject start = await _testRunnerService.StartTestRunAsync(testMode, returnOnlyFailures, returnWithLogs, testFilter);
            bool startSuccess = start?["success"]?.ToObject<bool>() ?? false;
            string runId = start?["runId"]?.ToObject<string>();
            if (!startSuccess || string.IsNullOrEmpty(runId))
            {
                tcs.SetResult(start ?? McpUnitySocketHandler.CreateErrorResponse("Failed to start PlayMode test run", "tool_execution_error"));
                return;
            }

            McpLogger.LogInfo($"RunTestsTool PlayMode polling started for runId={runId}");

            int pollIntervalMs = 1500;
            int timeoutSeconds = Mathf.Max(30, McpUnitySettings.Instance.RequestTimeoutSeconds);
            DateTime deadlineUtc = DateTime.UtcNow.AddSeconds(timeoutSeconds);

            while (DateTime.UtcNow < deadlineUtc)
            {
                JObject status = _testRunnerService.GetTestRunStatus(runId);
                bool statusSuccess = status?["success"]?.ToObject<bool>() ?? false;
                string runStatus = status?["status"]?.ToObject<string>();

                if (statusSuccess && (runStatus == "completed" || runStatus == "failed"))
                {
                    McpLogger.LogInfo($"RunTestsTool PlayMode polling completed for runId={runId} with status={runStatus}");
                    // Return the finished result payload if present to preserve the existing schema.
                    if (status["result"] is JObject resultObj)
                    {
                        tcs.SetResult(resultObj);
                    }
                    else
                    {
                        tcs.SetResult(status);
                    }
                    return;
                }

                await Task.Delay(pollIntervalMs);
            }

            tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                $"Timed out waiting for PlayMode test run {runId} to complete",
                "test_runner_timeout"));
            McpLogger.LogWarning($"RunTestsTool PlayMode polling timed out for runId={runId}");
        }
    }
}
