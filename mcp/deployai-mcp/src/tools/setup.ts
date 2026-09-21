import type { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import type { DeployAIApiClient } from '../api/client.js';
import type { DeploymentPlanPart } from '../api/types.js';
import { registerTool } from './register.js';

const deploymentPlanPartSchema = z.object({
  role: z.string(),
  providerName: z.string(),
  rootDirectory: z.string().optional(),
  serviceDirectory: z.string().optional(),
  buildCommand: z.string().optional(),
  installCommand: z.string().optional(),
  startCommand: z.string().optional(),
  outputDirectory: z.string().optional(),
  framework: z.string().optional(),
  dockerfilePath: z.string().optional(),
  databaseEngine: z.string().optional(),
});

export function registerSetupTools(server: McpServer, client: DeployAIApiClient): void {
  registerTool(
    server,
    'generate_deployment_setup',
    'Generate split-origin deployment files using Claude AI or templates. May take several minutes for large repos.',
    {
      owner: z.string().describe('GitHub repository owner.'),
      repo: z.string().describe('GitHub repository name.'),
      git_ref: z.string().describe('Git branch or commit ref.'),
      parts: z.array(deploymentPlanPartSchema).describe('Deployment plan parts.'),
      project_id: z.string().optional().describe('Optional linked DeployAI project UUID.'),
      force_regenerate: z.boolean().optional().describe('Force regeneration even if files exist.'),
      use_ai: z.boolean().optional().describe('Use Claude AI setup (default: project preference or server default).'),
    },
    async (args) =>
      client.generateDeploymentSetup(
        String(args.owner),
        String(args.repo),
        String(args.git_ref),
        args.parts as DeploymentPlanPart[],
        {
          projectId: args.project_id ? String(args.project_id) : undefined,
          forceRegenerate: args.force_regenerate === true,
          useAi: typeof args.use_ai === 'boolean' ? args.use_ai : undefined,
        }
      )
  );

  registerTool(
    server,
    'merge_deployment_setup',
    'Merge a deployment setup pull request and sync environment URLs.',
    {
      owner: z.string().describe('GitHub repository owner.'),
      repo: z.string().describe('GitHub repository name.'),
      pull_request_number: z.number().int().describe('Pull request number to merge.'),
      project_id: z.string().optional().describe('Optional linked DeployAI project UUID.'),
    },
    async (args) =>
      client.mergeDeploymentSetup(
        String(args.owner),
        String(args.repo),
        Number(args.pull_request_number),
        args.project_id ? String(args.project_id) : undefined
      )
  );
}
