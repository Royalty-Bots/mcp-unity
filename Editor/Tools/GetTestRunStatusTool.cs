using McpUnity.Services;
using McpUnity.Unity;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Retrieves status for a previously started test run.
    /// </summary>
    public class GetTestRunStatusTool : McpToolBase
    {
        private readonly ITestRunnerService _testRunnerService;

        public GetTestRunStatusTool(ITestRunnerService testRunnerService)
        {
            Name = "get_test_run_status";
            Description = "Gets status and result for a started Unity test run.";
            IsAsync = false;
            _testRunnerService = testRunnerService;
        }

        public override JObject Execute(JObject parameters)
        {
            string runId = parameters?["runId"]?.ToObject<string>();
            if (string.IsNullOrEmpty(runId))
            {
                return McpUnitySocketHandler.CreateErrorResponse("Missing runId", "invalid_request");
            }

            return _testRunnerService.GetTestRunStatus(runId);
        }
    }
}
