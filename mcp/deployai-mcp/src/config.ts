import { homedir } from 'node:os';
import { join } from 'node:path';

export interface DeployAIConfig {
  apiUrl: string;
  accessToken: string | null;
  refreshToken: string | null;
  tokenStorePath: string;
  streamTimeoutMs: number;
}

const DEFAULT_API_URL = 'http://localhost:5000';
const STREAM_TIMEOUT_MS = 45 * 60 * 1000;

export function loadConfig(): DeployAIConfig {
  return {
    apiUrl: (process.env.DEPLOYAI_API_URL ?? DEFAULT_API_URL).replace(/\/$/, ''),
    accessToken: process.env.DEPLOYAI_ACCESS_TOKEN ?? null,
    refreshToken: process.env.DEPLOYAI_REFRESH_TOKEN ?? null,
    tokenStorePath:
      process.env.DEPLOYAI_TOKEN_STORE ??
      join(homedir(), '.deployai', 'mcp-tokens.json'),
    streamTimeoutMs: STREAM_TIMEOUT_MS,
  };
}
