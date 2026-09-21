import { exec } from 'node:child_process';
import { promisify } from 'node:util';
import type { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import type { DeployAIApiClient } from '../api/client.js';
import { parseTokensFromCallbackUrl } from '../auth/token-store.js';
import { DeployAIApiError } from '../errors.js';
import { registerTool } from './register.js';

const execAsync = promisify(exec);

export function registerAuthTools(server: McpServer, client: DeployAIApiClient): void {
  registerTool(
    server,
    'mcp_auth',
    'Authenticate to DeployAI. Provide tokens directly, paste a callback URL after GitHub login, or open the browser to sign in.',
    {
      access_token: z.string().optional().describe('JWT access token from DeployAI after login.'),
      refresh_token: z.string().optional().describe('JWT refresh token from DeployAI after login.'),
      callback_url: z
        .string()
        .optional()
        .describe(
          'Full redirect URL from browser after GitHub login (contains accessToken and refreshToken query params).'
        ),
    },
    async (args) => {
      if (args.callback_url) {
        const parsed = parseTokensFromCallbackUrl(String(args.callback_url));
        if (!parsed) {
          throw new DeployAIApiError(
            'invalid_callback_url',
            'Could not parse accessToken and refreshToken from callback_url.'
          );
        }
        await client.setTokens(parsed.accessToken, parsed.refreshToken);
      } else if (args.access_token && args.refresh_token) {
        await client.setTokens(String(args.access_token), String(args.refresh_token));
      } else if (!client.hasTokens()) {
        const loginUrl = `${client.getApiUrl()}/api/auth/github/login`;
        await openBrowser(loginUrl);
        return {
          status: 'browser_opened',
          loginUrl,
          instructions: [
            'Complete GitHub sign-in in the browser.',
            'After redirect, copy the full URL from the address bar (contains accessToken and refreshToken).',
            'Call mcp_auth again with callback_url set to that URL.',
            'Alternatively, copy tokens from browser localStorage (deployai_access_token, deployai_refresh_token) and pass access_token + refresh_token.',
          ],
        };
      }

      await client.listProjects();
      return {
        status: 'authenticated',
        message: 'Successfully authenticated to DeployAI.',
      };
    }
  );
}

async function openBrowser(url: string): Promise<void> {
  const platform = process.platform;
  const command =
    platform === 'win32'
      ? `start "" "${url}"`
      : platform === 'darwin'
        ? `open "${url}"`
        : `xdg-open "${url}"`;

  try {
    await execAsync(command);
  } catch {
    // Browser open is best-effort; instructions still returned.
  }
}
