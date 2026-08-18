import { ProjectTarget } from '../models/api.models';
import { pickDomainTargetId } from './domain-target';

describe('pickDomainTargetId', () => {
  const target = (overrides: Partial<ProjectTarget>): ProjectTarget => ({
    id: 'target-id',
    providerName: 'vercel',
    credentialId: 'cred-1',
    providerProjectId: 'proj-1',
    ...overrides
  });

  it('returns null when there are no targets', () => {
    expect(pickDomainTargetId([])).toBeNull();
  });

  it('picks the target whose config role is "website"', () => {
    const api = target({ id: 'api', config: JSON.stringify({ role: 'server' }) });
    const website = target({ id: 'website', config: JSON.stringify({ role: 'website' }) });
    expect(pickDomainTargetId([api, website])).toBe('website');
  });

  it('falls back to the first target when none is marked as website', () => {
    const first = target({ id: 'first' });
    const second = target({ id: 'second' });
    expect(pickDomainTargetId([first, second])).toBe('first');
  });

  it('falls back to the first target when config is not valid JSON', () => {
    const broken = target({ id: 'broken', config: 'not-json' });
    expect(pickDomainTargetId([broken])).toBe('broken');
  });
});
