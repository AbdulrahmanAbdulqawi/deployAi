import type { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import type { DeployAIApiClient } from '../api/client.js';
import { registerTool } from './register.js';

/**
 * Coolify tools. Read-only on purpose: writes to a Coolify instance go through DeployAI's
 * deployment flow, which encodes what it has learned the hard way (never writing custom
 * labels, idempotent creation, env-var flag semantics). A tool that PATCHed applications
 * directly would bypass all of it.
 */
export function registerCoolifyTools(server: McpServer, client: DeployAIApiClient): void {
  registerTool(
    server,
    'coolify_inventory',
    'Everything a Coolify connection runs: projects, their environments, and each application ' +
      '(build pack, Coolify status, repo, branch, domains) and database. Read-only. Projects whose ' +
      'environments could not be listed are flagged environmentsInconclusive rather than shown empty; ' +
      'resources that match no environment are returned under unplaced* rather than dropped.',
    {
      credential_id: z.string().describe('The Coolify credential UUID (see list_credentials).'),
    },
    async (args) => client.getCoolifyInventory(String(args.credential_id))
  );
}
