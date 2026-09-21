import type { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import type { DeployAIApiClient } from '../api/client.js';
import { DeploymentVerificationScope } from '../api/types.js';
import { registerTool } from './register.js';

export function registerDeploymentTools(server: McpServer, client: DeployAIApiClient): void {
  registerTool(
    server,
    'list_deployments',
    'List deployments for a project. Returns paginated deployment summaries.',
    {
      project_id: z.string().describe('The project UUID.'),
      page: z.number().int().min(1).optional().describe('Page number (default 1).'),
    },
    async (args) =>
      client.listDeployments(String(args.project_id), Number(args.page ?? 1))
  );

  registerTool(
    server,
    'get_deployment',
    'Get deployment details including per-target status and failure analysis.',
    {
      deployment_id: z.string().describe('The deployment UUID.'),
    },
    async (args) => client.getDeployment(String(args.deployment_id))
  );

  registerTool(
    server,
    'trigger_deployment',
    'Trigger a new deployment for a project. Returns deploymentId — poll get_deployment for status.',
    {
      project_id: z.string().describe('The project UUID.'),
      branch: z.string().optional().describe('Git branch to deploy. Defaults to project default branch.'),
    },
    async (args) =>
      client.triggerDeployment(
        String(args.project_id),
        args.branch ? String(args.branch) : undefined
      )
  );

  registerTool(
    server,
    'get_deployment_logs',
    'Get deployment log lines. Use instead of SignalR for MCP clients.',
    {
      deployment_id: z.string().describe('The deployment UUID.'),
      target: z
        .string()
        .optional()
        .describe('Optional deploy target ID to filter logs.'),
    },
    async (args) =>
      client.getDeploymentLogs(
        String(args.deployment_id),
        args.target ? String(args.target) : undefined
      )
  );

  registerTool(
    server,
    'verify_deployment',
    'Run post-deploy verification checks (website reachability, API health, CORS, split-origin wiring).',
    {
      deployment_id: z.string().describe('The deployment UUID.'),
      scope: z
        .enum([
          DeploymentVerificationScope.Website,
          DeploymentVerificationScope.Server,
          DeploymentVerificationScope.Both,
        ])
        .describe('Which checks to run: website, server, or both.'),
    },
    async (args) =>
      client.verifyDeployment(
        String(args.deployment_id),
        args.scope as DeploymentVerificationScope
      )
  );
}
