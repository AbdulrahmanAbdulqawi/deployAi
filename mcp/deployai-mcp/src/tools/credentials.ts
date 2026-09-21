import type { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import type { DeployAIApiClient } from '../api/client.js';
import { registerTool } from './register.js';

export function registerCredentialTools(server: McpServer, client: DeployAIApiClient): void {
  registerTool(
    server,
    'list_credentials',
    'List stored provider credentials (Vercel, Railway). Tokens are never returned.',
    {},
    async () => client.listCredentials()
  );

  registerTool(
    server,
    'list_provider_projects',
    'List projects/services available under a provider credential.',
    {
      credential_id: z.string().describe('The credential UUID.'),
    },
    async (args) => client.listProviderProjects(String(args.credential_id))
  );
}
