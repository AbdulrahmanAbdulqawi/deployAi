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

  it('shows a compose app what it is missing', () => {
    // A compose plan is not split-origin, so every check here scored it as having no requirements
    // at all. Its missing compose file was fetched, evaluated and marked blocking by the API, then
    // dropped on the floor — the plan card showed nothing and the deploy went ahead to build the
    // client directory on its own.
    const result = buildReadinessScorecard({
      isReady: false,
      usesSplitOrigin: false,
      usesSingleOriginCompose: true,
      missingFiles: [
        {
          path: 'docker-compose.coolify.yml',
          reason: 'A Docker Compose file is required.',
          severity: 'blocking'
        }
      ],
      warnings: []
    });

    expect(result.visible).toBe(true);
    expect(result.blockingCount).toBe(1);
    expect(result.items.some(item =>
      item.label === 'docker-compose.coolify.yml' &&
      item.status === ReadinessCheckStatus.Failed
    )).toBe(true);
  });

  it('offers the setup panel to a compose app', () => {
    expect(usesCoolifySetupScaffold({
      isReady: false,
      usesSplitOrigin: false,
      usesSingleOriginCompose: true,
      missingFiles: [],
      warnings: []
    })).toBe(true);
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

  it('shows setup scaffold for Coolify full-stack readiness', () => {
    expect(usesCoolifySetupScaffold({
      isReady: false,
      usesSplitOrigin: true,
      websiteProviderName: 'coolify',
      serverProviderName: 'coolify',
      missingFiles: [],
      warnings: []
    })).toBe(true);

    expect(usesCoolifySetupScaffold({
      isReady: false,
      usesSplitOrigin: true,
      websiteProviderName: 'vercel',
      serverProviderName: 'railway',
      missingFiles: [],
      warnings: []
    })).toBe(true);

    expect(usesCoolifySetupScaffold({
      isReady: true,
      usesSplitOrigin: false,
      missingFiles: [],
      warnings: []
    })).toBe(false);
  });
});
