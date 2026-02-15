using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using McpUnity.Unity;
using McpUnity.Utils;
using UnityEngine;
using UnityEditor;
using UnityEditor.TestTools.TestRunner.Api;
using Newtonsoft.Json.Linq;

namespace McpUnity.Services
{
    /// <summary>
    /// Service for accessing Unity Test Runner functionality
    /// Implements ICallbacks for TestRunnerApi.
    /// </summary>
    public class TestRunnerService : ITestRunnerService, ICallbacks
    {
        private class TestRunRecord
        {
            public string RunId;
            public TestMode TestMode;
            public string Status;
            public DateTime CreatedAtUtc;
            public DateTime? CompletedAtUtc;
            public JObject Result;
            public string ErrorMessage;
        }

        private const int MaxStoredRuns = 50;
        private readonly TestRunnerApi _testRunnerApi;
        private TaskCompletionSource<JObject> _tcs;
        private bool _returnOnlyFailures;
        private bool _returnWithLogs;
        private List<ITestResultAdaptor> _results;
        private readonly Dictionary<string, TestRunRecord> _runsById = new Dictionary<string, TestRunRecord>();
        private readonly object _runLock = new object();
        private string _activeRunId;

        /// <summary>
        /// Constructor
        /// </summary>
        public TestRunnerService()
        {
            _testRunnerApi = ScriptableObject.CreateInstance<TestRunnerApi>();
            _results = new List<ITestResultAdaptor>();
            _testRunnerApi.RegisterCallbacks(this);
        }

        /// <summary>
        /// Async retrieval of all tests using TestRunnerApi callbacks
        /// </summary>
        /// <param name="testModeFilter">Optional test mode filter (EditMode, PlayMode, or empty for all)</param>
        /// <returns>List of test items matching the specified test mode, or all tests if no mode specified</returns>
        public async Task<List<ITestAdaptor>> GetAllTestsAsync(string testModeFilter = "")
        {
            var tests = new List<ITestAdaptor>();
            var tasks = new List<Task<List<ITestAdaptor>>>();

            if (string.IsNullOrEmpty(testModeFilter) || testModeFilter.Equals("EditMode", StringComparison.OrdinalIgnoreCase))
            {
                tasks.Add(RetrieveTestsAsync(TestMode.EditMode));
            }
            if (string.IsNullOrEmpty(testModeFilter) || testModeFilter.Equals("PlayMode", StringComparison.OrdinalIgnoreCase))
            {
                tasks.Add(RetrieveTestsAsync(TestMode.PlayMode));
            }

            var results = await Task.WhenAll(tasks);

            foreach (var result in results)
            {
                tests.AddRange(result);
            }

            return tests;
        }

        /// <summary>
        /// Executes tests and returns a JSON summary.
        /// </summary>
        /// <param name="testMode">The test mode to run (EditMode or PlayMode).</param>
        /// <param name="returnOnlyFailures">If true, only failed test results are included in the output.</param>
        /// <param name="returnWithLogs">If true, all logs are included in the output.</param>
        /// <param name="testFilter">A filter string to select specific tests to run.</param>
        /// <returns>Task that resolves with test results when tests are complete</returns>
        public async Task<JObject> ExecuteTestsAsync(TestMode testMode, bool returnOnlyFailures, bool returnWithLogs, string testFilter = "")
        {
            var filter = new Filter { testMode = testMode };

            InitializeActiveRun(null, testMode, returnOnlyFailures, returnWithLogs);

            if (!string.IsNullOrEmpty(testFilter))
            {
                filter.testNames = new[] { testFilter };
            }

            _testRunnerApi.Execute(new ExecutionSettings(filter));

            return await WaitForCompletionAsync(
                McpUnitySettings.Instance.RequestTimeoutSeconds);
        }

        /// <summary>
        /// Starts a test run and returns a run id immediately.
        /// </summary>
        public Task<JObject> StartTestRunAsync(TestMode testMode, bool returnOnlyFailures, bool returnWithLogs, string testFilter = "")
        {
            var filter = new Filter { testMode = testMode };
            if (!string.IsNullOrEmpty(testFilter))
            {
                filter.testNames = new[] { testFilter };
            }

            string runId = Guid.NewGuid().ToString("N");
            string activeRunId;

            lock (_runLock)
            {
                activeRunId = _activeRunId;
            }

            if (!string.IsNullOrEmpty(activeRunId) && GetRunStatus(activeRunId) == "running")
            {
                return Task.FromResult<JObject>(new JObject
                {
                    ["success"] = false,
                    ["type"] = "text",
                    ["message"] = $"Another test run is already in progress: {activeRunId}",
                    ["error"] = "test_run_in_progress",
                    ["runId"] = activeRunId
                });
            }

            InitializeActiveRun(runId, testMode, returnOnlyFailures, returnWithLogs);
            _testRunnerApi.Execute(new ExecutionSettings(filter));

            return Task.FromResult<JObject>(new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Test run started: {runId}",
                ["runId"] = runId,
                ["status"] = "running",
                ["testMode"] = testMode.ToString()
            });
        }

        /// <summary>
        /// Gets the status of a previously started run.
        /// </summary>
        public JObject GetTestRunStatus(string runId)
        {
            if (string.IsNullOrEmpty(runId))
            {
                return McpUnitySocketHandler.CreateErrorResponse("Missing runId", "invalid_request");
            }

            TestRunRecord run;
            lock (_runLock)
            {
                if (!_runsById.TryGetValue(runId, out run))
                {
                    return McpUnitySocketHandler.CreateErrorResponse($"Unknown runId: {runId}", "not_found");
                }
            }

            var statusResponse = new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["runId"] = runId,
                ["status"] = run.Status,
                ["testMode"] = run.TestMode.ToString(),
                ["createdAtUtc"] = run.CreatedAtUtc.ToString("o"),
                ["message"] = $"Test run {run.Status}: {runId}"
            };

            if (run.CompletedAtUtc.HasValue)
            {
                statusResponse["completedAtUtc"] = run.CompletedAtUtc.Value.ToString("o");
            }

            if (!string.IsNullOrEmpty(run.ErrorMessage))
            {
                statusResponse["errorMessage"] = run.ErrorMessage;
            }

            if (run.Result != null)
            {
                statusResponse["result"] = run.Result;
            }

            return statusResponse;
        }
        
        /// <summary>
        /// Asynchronously retrieves all test adaptors for the specified test mode.
        /// </summary>
        /// <param name="mode">The test mode to retrieve tests for (EditMode or PlayMode).</param>
        /// <returns>A task that resolves to a list of ITestAdaptor representing all tests in the given mode.</returns>
        private Task<List<ITestAdaptor>> RetrieveTestsAsync(TestMode mode)
        {
            var tcs = new TaskCompletionSource<List<ITestAdaptor>>();
            var tests = new List<ITestAdaptor>();

            _testRunnerApi.RetrieveTestList(mode, adaptor =>
            {
                CollectTestItems(adaptor, tests);
                tcs.SetResult(tests);
            });

            return tcs.Task;
        }
        
        /// <summary>
        /// Recursively collect test items from test adaptors
        /// </summary>
        private void CollectTestItems(ITestAdaptor testAdaptor, List<ITestAdaptor> tests)
        {
            if (testAdaptor.IsSuite)
            {
                // For suites (namespaces, classes), collect all children
                foreach (var child in testAdaptor.Children)
                {
                    CollectTestItems(child, tests);
                }
            }
            else
            {
                tests.Add(testAdaptor);
            }
        }

        #region ICallbacks Implementation

        /// <summary>
        /// Called when the test run starts.
        /// </summary>
        public void RunStarted(ITestAdaptor testsToRun)
        {
            if (_tcs == null)
                return;
            
            _results.Clear();
            McpLogger.LogInfo($"Test run started: {testsToRun?.Name}");
        }

        /// <summary>
        /// Called when an individual test starts.
        /// </summary>
        public void TestStarted(ITestAdaptor test)
        {
            // Optionally implement per-test start logic or logging.
        }

        /// <summary>
        /// Called when an individual test finishes.
        /// </summary>
        public void TestFinished(ITestResultAdaptor result)
        {
            if (_tcs == null)
                return;
            
            _results.Add(result);
        }

        /// <summary>
        /// Called when the test run finishes.
        /// </summary>
        public void RunFinished(ITestResultAdaptor result)
        {
            if (_tcs == null)
                return;
            
            var summary = BuildResultJson(_results, result);
            MarkRunCompleted(summary, null);
            _tcs.TrySetResult(summary);
            _tcs = null;
        }

        #endregion

        #region Helpers

        private async Task<JObject> WaitForCompletionAsync(int timeoutSeconds)
        {
            var delayTask = Task.Delay(TimeSpan.FromSeconds(timeoutSeconds));
            var winner = await Task.WhenAny(_tcs.Task, delayTask);
            
            if (winner != _tcs.Task)
            {
                var timeoutResult =
                    McpUnitySocketHandler.CreateErrorResponse(
                        $"Test run timed out after {timeoutSeconds} seconds",
                        "test_runner_timeout");

                MarkRunCompleted(timeoutResult, "timeout");
                _tcs.TrySetResult(timeoutResult);
            }
            return await _tcs.Task;
        }

        private void InitializeActiveRun(string runId, TestMode testMode, bool returnOnlyFailures, bool returnWithLogs)
        {
            _tcs = new TaskCompletionSource<JObject>();
            _returnOnlyFailures = returnOnlyFailures;
            _returnWithLogs = returnWithLogs;

            if (string.IsNullOrEmpty(runId))
            {
                lock (_runLock)
                {
                    _activeRunId = null;
                }
                return;
            }

            var record = new TestRunRecord
            {
                RunId = runId,
                TestMode = testMode,
                Status = "running",
                CreatedAtUtc = DateTime.UtcNow
            };

            lock (_runLock)
            {
                _activeRunId = runId;
                _runsById[runId] = record;
                PruneRunsIfNeeded();
            }
        }

        private void MarkRunCompleted(JObject summary, string errorMessage)
        {
            lock (_runLock)
            {
                if (string.IsNullOrEmpty(_activeRunId))
                {
                    return;
                }

                if (_runsById.TryGetValue(_activeRunId, out var record))
                {
                    record.Result = summary;
                    record.CompletedAtUtc = DateTime.UtcNow;
                    record.ErrorMessage = errorMessage;
                    record.Status = summary?["error"] != null ? "failed" : "completed";
                }

                _activeRunId = null;
            }
        }

        private string GetRunStatus(string runId)
        {
            lock (_runLock)
            {
                if (_runsById.TryGetValue(runId, out var record))
                {
                    return record.Status;
                }
            }

            return "unknown";
        }

        private void PruneRunsIfNeeded()
        {
            if (_runsById.Count <= MaxStoredRuns)
            {
                return;
            }

            var oldest = _runsById.Values
                .OrderBy(r => r.CreatedAtUtc)
                .Take(_runsById.Count - MaxStoredRuns)
                .ToList();

            foreach (var run in oldest)
            {
                _runsById.Remove(run.RunId);
            }
        }

        private JObject BuildResultJson(List<ITestResultAdaptor> results, ITestResultAdaptor result)
        {
            var arr = new JArray(results
                .Where(r => !r.HasChildren)
                .Where(r => !_returnOnlyFailures || r.ResultState.StartsWith("Failed"))
                .Select(r => new JObject {
                    ["name"]      = r.Name,
                    ["fullName"]  = r.FullName,
                    ["state"]     = r.ResultState,
                    ["message"]   = r.Message,
                    ["duration"]  = r.Duration,
                    ["logs"]      = _returnWithLogs ? r.Output : null,
                    ["stackTrace"] = r.StackTrace
                }));

            int testCount = result.PassCount + result.SkipCount + result.FailCount;
            return new JObject { 
                ["success"]           = true,
                ["type"]              = "text",
                ["message"]           = $"{result.Test.Name} test run completed: {result.PassCount}/{testCount} passed - {result.FailCount}/{testCount} failed - {result.SkipCount}/{testCount} skipped",
                ["resultState"]       = result.ResultState,
                ["durationSeconds"]   = result.Duration,
                ["testCount"]         = results.Count,
                ["passCount"]         = result.PassCount,
                ["failCount"]         = result.FailCount,
                ["skipCount"]         = result.SkipCount,
                ["results"]           = arr
            };
        }

        #endregion
    }
}
