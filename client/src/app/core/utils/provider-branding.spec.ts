import { ProviderName } from '../models/api.models';
import { providerSubtitle } from './provider-branding';
import { providerLabel } from './target-config';

describe('provider-branding', () => {
  it('labels Coolify providers', () => {
    expect(providerLabel(ProviderName.Coolify)).toBe('Coolify');
    expect(providerSubtitle(ProviderName.Coolify)).toContain('Coolify');
  });
});
