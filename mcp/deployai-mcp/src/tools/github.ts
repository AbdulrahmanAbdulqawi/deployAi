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

export function registerGitHubTools(server: McpServer, client: DeployAIApiClient): void {
  registerTool(
    server,
    'list_github_repos',
    'List GitHub repositories available to the authenticated user.',
    {
      page: z.number().int().min(1).optional().describe('Page number (default 1).'),
      per_page: z.number().int().min(1).max(100).optional().describe('Results per page (default 30).'),
      search: z.string().optional().describe('Optional search filter.'),
    },
    async (args) =>
      client.listGitHubRepos(
        Number(args.page ?? 1),
        Number(args.per_page ?? 30),
        args.search ? String(args.search) : undefined
      )
  );

  registerTool(
    server,
    'get_deployment_plan',
    'Auto-classify a GitHub repo into a split-origin deployment plan (Vercel frontend + Railway backend).',
    {
      owner: z.string().describe('GitHub repository owner.'),
      repo: z.string().describe('GitHub repository name.'),
      git_ref: z.string().describe('Git branch or commit ref to analyze.'),
    },
    async (args) =>
      client.getDeploymentPlan(String(args.owner), String(args.repo), String(args.git_ref))
  );

  registerTool(
    server,
    'scan_deployment_readiness',
    'Scan a repo for missing split-origin deployment files (vercel.json, railway.toml, env files, etc.).',
    {
      owner: z.string().describe('GitHub repository owner.'),
      repo: z.string().describe('GitHub repository name.'),
      git_ref: z.string().describe('Git branch or commit ref to scan.'),
      parts: z
        .array(deploymentPlanPartSchema)
        .describe('Deployment plan parts from get_deployment_plan.'),
    },
    async (args) =>
      client.scanDeploymentReadiness(
        String(args.owner),
        String(args.repo),
        String(args.git_ref),
        args.parts as DeploymentPlanPart[]
      )
  );
}
