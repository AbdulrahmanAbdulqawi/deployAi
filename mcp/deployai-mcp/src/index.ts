#!/usr/bin/env node
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { DeployAIApiClient } from './api/client.js';
import { loadConfig } from './config.js';
import { registerAuthTools } from './tools/auth.js';
import { registerCoolifyTools } from './tools/coolify.js';
import { registerCredentialTools } from './tools/credentials.js';
import { registerDeploymentTools } from './tools/deployments.js';
import { registerFixTools } from './tools/fixes.js';
import { registerGitHubTools } from './tools/github.js';
import { registerHealthTools } from './tools/health.js';
import { registerProjectTools } from './tools/projects.js';
import { registerSetupTools } from './tools/setup.js';

const __dirname = dirname(fileURLToPath(import.meta.url));

function loadInstructions(): string {
  try {
    return readFileSync(join(__dirname, '..', 'INSTRUCTIONS.md'), 'utf-8');
  } catch {
    return 'DeployAI MCP server — orchestrate split-origin deployments via the DeployAI API.';
  }
}

async function main(): Promise<void> {
  const config = loadConfig();
  const client = new DeployAIApiClient(config);
  await client.initialize();

  const server = new McpServer(
    {
      name: 'deployai',
      version: '0.1.0',
    },
    {
      instructions: loadInstructions(),
    }
  );

  registerHealthTools(server, client);
  registerAuthTools(server, client);
  registerProjectTools(server, client);
  registerDeploymentTools(server, client);
  registerGitHubTools(server, client);
  registerSetupTools(server, client);
  registerFixTools(server, client);
  registerCredentialTools(server, client);
  registerCoolifyTools(server, client);

  const transport = new StdioServerTransport();
  await server.connect(transport);
}

main().catch((error) => {
  console.error('DeployAI MCP server failed to start:', error);
  process.exit(1);
});
