import { describe, expect, it } from 'vitest';
import { parseTokensFromCallbackUrl } from './token-store.js';

describe('parseTokensFromCallbackUrl', () => {
  it('extracts tokens from callback URL query params', () => {
    const url =
      'http://localhost:4200/auth/callback?accessToken=abc123&refreshToken=def456&expiresIn=900';
    const tokens = parseTokensFromCallbackUrl(url);
    expect(tokens).toEqual({
      accessToken: 'abc123',
      refreshToken: 'def456',
    });
  });

  it('returns null when tokens are missing', () => {
    expect(parseTokensFromCallbackUrl('http://localhost:4200/auth/callback')).toBeNull();
    expect(parseTokensFromCallbackUrl('not-a-url')).toBeNull();
  });
});
