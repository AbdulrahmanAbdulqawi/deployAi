import type { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import type { DeployAIApiClient } from '../api/client.js';
import { DeployAIApiError } from '../errors.js';
import { registerTool } from './register.js';

export function registerFixTools(server: McpServer, client: DeployAIApiClient): void {
  registerTool(
    server,
    'generate_deployment_fix',
    'Generate a Claude fix for a failed build deployment target. Requires failureAnalysis.canRequestClaudeFix.',
    {
      deployment_id: z.string().describe('The deployment UUID.'),
      target_id: z.string().describe('The failed deployment target UUID.'),
    },
    async (args) => {
      const deployment = await client.getDeployment(String(args.deployment_id));
      const target = deployment.targets.find((t) => t.id === args.target_id);
      if (!target) {
        throw new DeployAIApiError('target_not_found', 'Deployment target not found.');
      }
      if (!target.failureAnalysis?.canRequestClaudeFix) {
        throw new DeployAIApiError(
          'fix_not_eligible',
          'This failure is not eligible for Claude fix. Check failureAnalysis.canRequestClaudeFix.'
        );
      }
      return client.generateDeploymentFix(String(args.deployment_id), String(args.target_id));
    }
  );

  registerTool(
    server,
    'generate_verification_fix',
    'Generate a Claude fix for a failed verification check. Requires check.canRequestClaudeFix.',
    {
      deployment_id: z.string().describe('The deployment UUID.'),
      check_id: z.string().describe('Verification check ID (e.g. connection.cors).'),
      target_id: z.string().optional().describe('Optional deployment target UUID.'),
    },
    async (args) => client.generateVerificationFix(
      String(args.deployment_id),
      String(args.check_id),
      args.target_id ? String(args.target_id) : undefined
    )
  );

  registerTool(
    server,
    'merge_deployment_fix',
    'Merge a deployment fix pull request.',
    {
      owner: z.string().describe('GitHub repository owner.'),
      repo: z.string().describe('GitHub repository name.'),
      pull_request_number: z.number().int().describe('Pull request number to merge.'),
    },
    async (args) =>
      client.mergeDeploymentFix(
        String(args.owner),
        String(args.repo),
        Number(args.pull_request_number)
      )
  );
}
