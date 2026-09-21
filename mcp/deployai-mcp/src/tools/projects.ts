import type { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import type { DeployAIApiClient } from '../api/client.js';
import { registerTool } from './register.js';

export function registerProjectTools(server: McpServer, client: DeployAIApiClient): void {
  registerTool(
    server,
    'list_projects',
    'List all DeployAI projects for the authenticated user.',
    {},
    async () => client.listProjects()
  );

  registerTool(
    server,
    'get_project',
    'Get full details for a DeployAI project including deploy targets.',
    {
      project_id: z.string().describe('The project UUID.'),
    },
    async (args) => client.getProject(String(args.project_id))
  );
}
