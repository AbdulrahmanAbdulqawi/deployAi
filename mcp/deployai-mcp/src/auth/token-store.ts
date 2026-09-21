import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname } from 'node:path';

export interface StoredTokens {
  accessToken: string;
  refreshToken: string;
  updatedAt: string;
}

export class TokenStore {
  constructor(private readonly filePath: string) {}

  async load(): Promise<StoredTokens | null> {
    try {
      const raw = await readFile(this.filePath, 'utf-8');
      const parsed = JSON.parse(raw) as StoredTokens;
      if (!parsed.accessToken || !parsed.refreshToken) {
        return null;
      }
      return parsed;
    } catch {
      return null;
    }
  }

  async save(accessToken: string, refreshToken: string): Promise<void> {
    await mkdir(dirname(this.filePath), { recursive: true });
    const payload: StoredTokens = {
      accessToken,
      refreshToken,
      updatedAt: new Date().toISOString(),
    };
    await writeFile(this.filePath, JSON.stringify(payload, null, 2), 'utf-8');
  }

  async clear(): Promise<void> {
    await this.save('', '');
  }
}

export function parseTokensFromCallbackUrl(callbackUrl: string): {
  accessToken: string;
  refreshToken: string;
} | null {
  try {
    const url = new URL(callbackUrl);
    const accessToken = url.searchParams.get('accessToken');
    const refreshToken = url.searchParams.get('refreshToken');
    if (!accessToken || !refreshToken) {
      return null;
    }
    return { accessToken, refreshToken };
  } catch {
    return null;
  }
}
