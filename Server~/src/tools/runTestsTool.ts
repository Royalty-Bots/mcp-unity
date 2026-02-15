import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

// Constants for the tool
const toolName = 'run_tests';
const toolDescription = 'Runs Unity\'s Test Runner tests';
const paramsSchema = z.object({
  testMode: z.string().optional().default('EditMode').describe('The test mode to run (EditMode or PlayMode) - defaults to EditMode (optional)'),
  testFilter: z.string().optional().default('').describe('The specific test filter to run (e.g. specific test name or class name, must include namespace) (optional)'),
  returnOnlyFailures: z.boolean().optional().default(true).describe('Whether to show only failed tests in the results (optional)'),
  returnWithLogs: z.boolean().optional().default(false).describe('Whether to return the test logs in the results (optional)')
});

/**
 * Creates and registers the Run Tests tool with the MCP server
 * This tool allows running tests in the Unity Test Runner
 * 
 * @param server The MCP server instance to register with
 * @param mcpUnity The McpUnity instance to communicate with Unity
 * @param logger The logger instance for diagnostic information
 */
export function registerRunTestsTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${toolName}`);
  
  // Register this tool with the MCP server
  server.tool(
    toolName,
    toolDescription,
    paramsSchema.shape,
    async (params: any = {}) => {
      try {
        logger.info(`Executing tool: ${toolName}`, params);
        const result = await toolHandler(mcpUnity, params);
        logger.info(`Tool execution successful: ${toolName}`);
        return result;
      } catch (error) {
        logger.error(`Tool execution failed: ${toolName}`, error);
        throw error;
      }
    }
  );
}

/**
 * Handles running tests in Unity
 * 
 * @param mcpUnity The McpUnity instance to communicate with Unity
 * @param params The parameters for the tool
 * @returns A promise that resolves to the tool execution result
 * @throws McpUnityError if the request to Unity fails
 */
async function toolHandler(mcpUnity: McpUnity, params: any = {}): Promise<CallToolResult> {
  const {
    testMode = 'EditMode',
    testFilter = '',
    returnOnlyFailures = true,
    returnWithLogs = false
  } = params;

  const isPlayMode = testMode.toLowerCase() === 'playmode';
  let response: any;

  if (!isPlayMode) {
    // Keep EditMode behavior synchronous.
    response = await mcpUnity.sendRequest({
      method: toolName,
      params: {
        testMode,
        testFilter,
        returnOnlyFailures,
        returnWithLogs
      }
    });
  } else {
    // PlayMode causes a Unity domain reload that frequently interrupts the socket mid-response.
    // Start run asynchronously, then poll until it completes after reconnect.
    const start = await mcpUnity.sendRequest({
      method: 'start_test_run',
      params: {
        testMode,
        testFilter,
        returnOnlyFailures,
        returnWithLogs
      }
    });

    if (!start.success || !start.runId) {
      throw new McpUnityError(
        ErrorType.TOOL_EXECUTION,
        start.message || 'Failed to start PlayMode test run'
      );
    }

    const runId = start.runId as string;
    const pollIntervalMs = 1500;
    const pollTimeoutMs = 5 * 60 * 1000; // 5 minutes
    const deadline = Date.now() + pollTimeoutMs;
    let lastError: unknown = null;

    while (Date.now() < deadline) {
      try {
        const status = await mcpUnity.sendRequest(
          {
            method: 'get_test_run_status',
            params: { runId }
          },
          { timeout: 10000, queueIfDisconnected: true }
        );

        if (!status.success) {
          throw new McpUnityError(
            ErrorType.TOOL_EXECUTION,
            status.message || `Failed to poll test run status for ${runId}`
          );
        }

        if (status.status === 'completed' || status.status === 'failed') {
          response = status.result || status;
          break;
        }
      } catch (err) {
        // During reconnect windows polling can fail transiently.
        lastError = err;
      }

      await new Promise(resolve => setTimeout(resolve, pollIntervalMs));
    }

    if (!response) {
      const details = lastError instanceof Error ? ` Last error: ${lastError.message}` : '';
      throw new McpUnityError(
        ErrorType.TIMEOUT,
        `Timed out waiting for PlayMode test run ${runId} to complete.${details}`
      );
    }
  }
  
  // Process the test results
  if (!response.success) {
    throw new McpUnityError(
      ErrorType.TOOL_EXECUTION,
      response.message || `Failed to run tests: Mode=${testMode}, Filter=${testFilter || 'none'}`
    );
  }
  
  // Extract test results
  const testResults = response.results || [];
  const testCount = response.testCount || 0;
  const passCount = response.passCount || 0;
  const failCount = response.failCount || 0;
  const skipCount = response.skipCount || 0;
  
  return {
    content: [
      {
        type: 'text',
        text: response.message
      },
      {
        type: 'text',
        text: JSON.stringify({
          testCount,
          passCount,
          failCount,
          skipCount,
          results: testResults
        }, null, 2)
      }
    ]
  };
}
