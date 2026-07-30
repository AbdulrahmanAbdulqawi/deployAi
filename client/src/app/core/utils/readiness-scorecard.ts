import { DeploymentReadinessResult, MissingDeploymentFile } from '../models/api.models';
import { isCoolifyProvider } from './provider-branding';

export enum ReadinessCheckStatus {
  Passed = 'passed',
  Failed = 'failed',
  Warning = 'warning',
  Skipped = 'skipped'
}

export interface ReadinessCheckItem {
  id: string;
  label: string;
  detail?: string;
  status: ReadinessCheckStatus;
  severity: 'blocking' | 'recommended' | 'warning' | 'info';
}

export interface ReadinessScorecardSummary {
  visible: boolean;
  isReady: boolean;
  passedCount: number;
  totalCount: number;
  scorePercent: number;
  blockingCount: number;
  items: ReadinessCheckItem[];
}

export function buildReadinessScorecard(
  readiness: DeploymentReadinessResult | null | undefined
): ReadinessScorecardSummary {
  if (!hasSetupRequirements(readiness)) {
    return emptyScorecard();
  }

  const items: ReadinessCheckItem[] = [
    {
      id: 'split-origin-detected',
      label: 'Frontend and API detected',
      detail: shapeDetail(readiness),
      status: ReadinessCheckStatus.Passed,
      severity: 'info'
    }
  ];

  for (const file of readiness.missingFiles) {
    items.push(mapMissingFile(file));
  }

  for (const [index, warning] of readiness.warnings.entries()) {
    items.push({
      id: `warning-${index}`,
      label: warning,
      status: ReadinessCheckStatus.Warning,
      severity: 'warning'
    });
  }

  if (readiness.isReady && readiness.missingFiles.length === 0) {
    items.push({
      id: 'deployment-ready',
      label: 'Repository setup looks complete',
      detail: readiness.usesSingleOriginCompose
        ? 'The compose file and its per-service Dockerfiles are in place.'
        : isCoolifyFullStackReadiness(readiness)
          ? 'Required Coolify full-stack files are in place.'
          : 'Required split-origin files are in place.',
      status: ReadinessCheckStatus.Passed,
      severity: 'info'
    });
  }

  const scoredItems = items.filter(item => item.status !== ReadinessCheckStatus.Skipped);
  const passedCount = scoredItems.filter(item => item.status === ReadinessCheckStatus.Passed).length;
  const totalCount = scoredItems.length;
  const blockingCount = readiness.missingFiles.filter(file => file.severity === 'blocking').length;

  return {
    visible: true,
    isReady: readiness.isReady,
    passedCount,
    totalCount,
    scorePercent: totalCount === 0 ? 100 : Math.round((passedCount / totalCount) * 100),
    blockingCount,
    items
  };
}

function mapMissingFile(file: MissingDeploymentFile): ReadinessCheckItem {
  const status = file.severity === 'blocking'
    ? ReadinessCheckStatus.Failed
    : ReadinessCheckStatus.Warning;

  return {
    id: `file-${file.path}`,
    label: file.path,
    detail: file.reason,
    status,
    severity: file.severity
  };
}

function emptyScorecard(): ReadinessScorecardSummary {
  return {
    visible: false,
    isReady: true,
    passedCount: 0,
    totalCount: 0,
    scorePercent: 100,
    blockingCount: 0,
    items: []
  };
}

function isCoolifyFullStackReadiness(readiness: DeploymentReadinessResult): boolean {
  return isCoolifyProvider(readiness.websiteProviderName ?? '') &&
    isCoolifyProvider(readiness.serverProviderName ?? '');
}

export function usesCoolifySetupScaffold(readiness: DeploymentReadinessResult | null | undefined): boolean {
  return hasSetupRequirements(readiness);
}

/**
 * Whether this deployment shape needs files in the repository before it can be published.
 *
 * Both shapes do, and the API reports on both — but every check here read usesSplitOrigin alone, so
 * a compose app scored as having nothing to satisfy. Its missing docker-compose.coolify.yml was
 * fetched, evaluated, marked blocking and then never rendered, and the wizard let the deploy through
 * to build the client directory by itself.
 */
export function hasSetupRequirements(
  readiness: DeploymentReadinessResult | null | undefined
): readiness is DeploymentReadinessResult {
  return !!readiness && (readiness.usesSplitOrigin || !!readiness.usesSingleOriginCompose);
}

function shapeDetail(readiness: DeploymentReadinessResult): string {
  if (readiness.usesSingleOriginCompose) {
    return 'This project deploys as one Docker Compose app (site, API and database together).';
  }

  if (isCoolifyFullStackReadiness(readiness)) {
    return 'This project uses a Coolify full-stack setup (website + API on your server).';
  }

  return 'This project uses a split-origin setup (website on Vercel, API on Railway).';
}
