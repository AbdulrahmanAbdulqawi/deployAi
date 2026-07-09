import { DeploymentDetail } from '../models/api.models';
import { computeDeploymentProgress } from './deployment-progress';

function createDeployment(
  status: string,
  targets: DeploymentDetail['targets']
): DeploymentDetail {
  return {
    id: 'deployment-1',
    projectId: 'project-1',
    branch: 'main',
    status,
    targets
  };
}

describe('computeDeploymentProgress', () => {
  it('returns null when deployment is missing', () => {
    expect(computeDeploymentProgress(null)).toBeNull();
  });

  it('reports preparing for pending split-origin deployment', () => {
    const progress = computeDeploymentProgress(createDeployment('pending', [
      { id: '1', deployTargetId: 'a', providerName: 'railway', status: 'pending' },
      { id: '2', deployTargetId: 'b', providerName: 'vercel', status: 'pending' }
    ]));

    expect(progress?.stage).toBe('preparing');
    expect(progress?.percent).toBe(5);
  });

  it('reports api deploying stage', () => {
    const progress = computeDeploymentProgress(createDeployment('in_progress', [
      { id: '1', deployTargetId: 'a', providerName: 'railway', status: 'in_progress' },
      { id: '2', deployTargetId: 'b', providerName: 'vercel', status: 'pending' }
    ]));

    expect(progress?.stage).toBe('api_deploying');
    expect(progress?.percent).toBe(30);
    expect(progress?.label).toContain('API');
  });

  it('reports website deploying after api success', () => {
    const progress = computeDeploymentProgress(createDeployment('in_progress', [
      { id: '1', deployTargetId: 'a', providerName: 'railway', status: 'success' },
      { id: '2', deployTargetId: 'b', providerName: 'vercel', status: 'in_progress' }
    ]));

    expect(progress?.stage).toBe('website_deploying');
    expect(progress?.percent).toBe(70);
  });

  it('handles website-only deployment', () => {
    const progress = computeDeploymentProgress(createDeployment('in_progress', [
      { id: '2', deployTargetId: 'b', providerName: 'vercel', status: 'in_progress' }
    ]));

    expect(progress?.stage).toBe('website_deploying');
    expect(progress?.percent).toBe(50);
  });

  it('marks complete deployment at 100%', () => {
    const progress = computeDeploymentProgress(createDeployment('success', [
      { id: '1', deployTargetId: 'a', providerName: 'railway', status: 'success' },
      { id: '2', deployTargetId: 'b', providerName: 'vercel', status: 'success' }
    ]));

    expect(progress?.stage).toBe('complete');
    expect(progress?.percent).toBe(100);
    expect(progress?.mode).toBe('complete');
  });

  it('marks failed deployment', () => {
    const progress = computeDeploymentProgress(createDeployment('failed', [
      { id: '1', deployTargetId: 'a', providerName: 'railway', status: 'failed' },
      { id: '2', deployTargetId: 'b', providerName: 'vercel', status: 'success' }
    ]));

    expect(progress?.stage).toBe('failed');
    expect(progress?.mode).toBe('failed');
  });
});
