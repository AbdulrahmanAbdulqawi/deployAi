import { DeploymentDetail, ProviderName } from '../models/api.models';
import { canSyncEnvironmentUrls } from './environment-sync-eligibility';

describe('canSyncEnvironmentUrls', () => {
  const baseDeployment = (targets: DeploymentDetail['targets']): DeploymentDetail => ({
    id: 'dep-1',
    projectId: 'proj-1',
    branch: 'main',
    status: 'success',
    triggeredBy: 'user',
    targets
  });

  it('returns false when deployment is missing', () => {
    expect(canSyncEnvironmentUrls(null)).toBeFalse();
    expect(canSyncEnvironmentUrls(undefined)).toBeFalse();
  });

  it('returns false when deployment is not complete', () => {
    const deployment = baseDeployment([
      { providerName: ProviderName.Vercel, status: 'success' } as DeploymentDetail['targets'][number]
    ]);
    expect(canSyncEnvironmentUrls(deployment, false)).toBeFalse();
  });

  it('returns true for successful Vercel and Railway targets', () => {
    const deployment = baseDeployment([
      { providerName: ProviderName.Vercel, status: 'success' } as DeploymentDetail['targets'][number],
      { providerName: ProviderName.Railway, status: 'success' } as DeploymentDetail['targets'][number]
    ]);
    expect(canSyncEnvironmentUrls(deployment)).toBeTrue();
  });

  it('returns false when only one of Vercel or Railway succeeded', () => {
    const deployment = baseDeployment([
      { providerName: ProviderName.Vercel, status: 'success' } as DeploymentDetail['targets'][number],
      { providerName: ProviderName.Railway, status: 'failed' } as DeploymentDetail['targets'][number]
    ]);
    expect(canSyncEnvironmentUrls(deployment)).toBeFalse();
  });

  it('returns true for two successful Coolify targets', () => {
    const deployment = baseDeployment([
      { providerName: ProviderName.Coolify, status: 'success' } as DeploymentDetail['targets'][number],
      { providerName: ProviderName.Coolify, status: 'success' } as DeploymentDetail['targets'][number]
    ]);
    expect(canSyncEnvironmentUrls(deployment)).toBeTrue();
  });

  it('returns false when only one Coolify target succeeded', () => {
    const deployment = baseDeployment([
      { providerName: ProviderName.Coolify, status: 'success' } as DeploymentDetail['targets'][number],
      { providerName: ProviderName.Coolify, status: 'in_progress' } as DeploymentDetail['targets'][number]
    ]);
    expect(canSyncEnvironmentUrls(deployment)).toBeFalse();
  });
});
