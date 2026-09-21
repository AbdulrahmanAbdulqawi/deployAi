import type { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import type { DeployAIApiClient } from '../api/client.js';
import { formatToolError, formatToolSuccess } from '../errors.js';

type ToolHandler = (args: Record<string, unknown>) => Promise<unknown>;

export function registerTool(
  server: McpServer,
  name: string,
  description: string,
  schema: z.ZodRawShape,
  handler: ToolHandler
): void {
  server.tool(name, description, schema, async (args) => {
    try {
      const result = await handler(args as Record<string, unknown>);
      return formatToolSuccess(result);
    } catch (error) {
      return formatToolError(error);
    }
  });
}

export type ToolRegistrar = (server: McpServer, client: DeployAIApiClient) => void;
