import { partLabelForTarget } from './target-config';
import { ProviderName } from '../models/api.models';

/**
 * `roleLabelForProvider` answers "which provider is this?" and is used where the question is
 * really "which part of the app is this?". Those come apart on Coolify, which hosts a website and
 * a server as separate applications: both answer "Your Coolify app", so a screen naming the broken
 * part cannot name it. CLAUDE.md states the rule directly — dispatch keys off role, never provider
 * name — and this is the helper that lets a caller follow it.
 *
 * Provider is still the fallback, because `role` is absent on targets saved before roles were
 * recorded, and an old deployment must not render a blank label.
 */
describe('partLabelForTarget', () => {
  it('names the part from its role', () => {
    expect(partLabelForTarget({ role: 'website', providerName: ProviderName.Coolify })).toBe('Website');
    expect(partLabelForTarget({ role: 'server', providerName: ProviderName.Coolify })).toBe('Server');
    expect(partLabelForTarget({ role: 'database', providerName: ProviderName.Coolify })).toBe('Database');
    expect(partLabelForTarget({ role: 'storage', providerName: ProviderName.Coolify })).toBe('File storage');
  });

  /** The case provider-name dispatch gets wrong: one provider, two different parts. */
  it('tells two parts on the same provider apart', () => {
    const website = partLabelForTarget({ role: 'website', providerName: ProviderName.Coolify });
    const server = partLabelForTarget({ role: 'server', providerName: ProviderName.Coolify });

    expect(website).not.toBe(server);
  });

  it('falls back to the provider when a legacy target carries no role', () => {
    expect(partLabelForTarget({ providerName: ProviderName.Vercel })).toBe('Website');
    expect(partLabelForTarget({ providerName: ProviderName.Railway })).toBe('Server');
  });

  it('never renders an empty label', () => {
    expect(partLabelForTarget({ providerName: 'something-new' })).toBe('something-new');
    expect(partLabelForTarget({ role: '', providerName: 'something-new' })).toBe('something-new');
  });

  it('ignores an unrecognised role rather than printing it raw', () => {
    expect(partLabelForTarget({ role: 'nonsense', providerName: ProviderName.Vercel })).toBe('Website');
  });
});
