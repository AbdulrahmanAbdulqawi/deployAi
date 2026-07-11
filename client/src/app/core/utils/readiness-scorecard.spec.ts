import {
  buildReadinessScorecard,
  ReadinessCheckStatus,
  usesCoolifySetupScaffold
} from './readiness-scorecard';
import { DeploymentReadinessResult } from '../models/api.models';

describe('buildReadinessScorecard', () => {
  it('returns hidden scorecard when split-origin is not used', () => {
    const result = buildReadinessScorecard({
      isReady: true,
      usesSplitOrigin: false,
      missingFiles: [],
      warnings: []
    });

    expect(result.visible).toBe(false);
    expect(result.items).toEqual([]);
  });

  it('marks blocking missing files as failed checks', () => {
    const readiness: DeploymentReadinessResult = {
      isReady: false,
      usesSplitOrigin: true,
      missingFiles: [
        {
          path: 'client/src/environments/environment.prod.ts',
          reason: 'Production API URL is not configured.',
          severity: 'blocking'
        }
      ],
      warnings: []
    };

    const result = buildReadinessScorecard(readiness);

    expect(result.visible).toBe(true);
    expect(result.isReady).toBe(false);
    expect(result.blockingCount).toBe(1);
    expect(result.items.some(item =>
      item.id === 'file-client/src/environments/environment.prod.ts' &&
      item.status === ReadinessCheckStatus.Failed
    )).toBe(true);
  });

  it('includes readiness complete item when repository is ready', () => {
    const result = buildReadinessScorecard({
      isReady: true,
      usesSplitOrigin: true,
      missingFiles: [],
      warnings: []
    });

    expect(result.isReady).toBe(true);
    expect(result.scorePercent).toBe(100);
    expect(result.items.some(item => item.id === 'deployment-ready')).toBe(true);
  });

  it('describes Coolify full-stack hosting in the scorecard', () => {
    const result = buildReadinessScorecard({
      isReady: true,
      usesSplitOrigin: true,
      websiteProviderName: 'coolify',
      serverProviderName: 'coolify',
      missingFiles: [],
      warnings: []
    });

    expect(result.items.some(item =>
      item.detail?.includes('Coolify full-stack setup')
    )).toBe(true);
    expect(result.items.some(item =>
      item.detail?.includes('Coolify full-stack files')
    )).toBe(true);
  });

  it('hides Vercel/Railway scaffold for Coolify full-stack readiness', () => {
    expect(usesCoolifySetupScaffold({
      isReady: false,
      usesSplitOrigin: true,
      websiteProviderName: 'coolify',
      serverProviderName: 'coolify',
      missingFiles: [],
      warnings: []
    })).toBe(false);

    expect(usesCoolifySetupScaffold({
      isReady: false,
      usesSplitOrigin: true,
      websiteProviderName: 'vercel',
      serverProviderName: 'railway',
      missingFiles: [],
      warnings: []
    })).toBe(true);
  });
});
