import type { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import type { DeployAIApiClient } from '../api/client.js';
import { registerTool } from './register.js';

export function registerHealthTools(server: McpServer, client: DeployAIApiClient): void {
  registerTool(
    server,
    'health_check',
    'Check DeployAI API connectivity. Does not require authentication.',
    {},
    async () => client.health()
  );
}
