import {
  buildReadinessScorecard,
  ReadinessCheckStatus
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
});
