import { DeploymentDetail, ProviderName } from '../models/api.models';

export type DeploymentProgressStage =
  | 'preparing'
  | 'api_deploying'
  | 'api_done'
  | 'website_deploying'
  | 'finishing'
  | 'complete'
  | 'failed';

export interface DeploymentProgress {
  stage: DeploymentProgressStage;
  percent: number;
  label: string;
  mode: 'determinate' | 'complete' | 'failed';
}

type DeploymentTarget = DeploymentDetail['targets'][number];

export function computeDeploymentProgress(deployment: DeploymentDetail | null): DeploymentProgress | null {
  if (!deployment) {
    return null;
  }

  const apiTarget = findTarget(deployment.targets, ProviderName.Railway);
  const websiteTarget = findTarget(deployment.targets, ProviderName.Vercel);
  const coolifyTarget = findTarget(deployment.targets, ProviderName.Coolify);
  const hasApi = apiTarget !== null;
  const hasWebsite = websiteTarget !== null;
  const hasCoolify = coolifyTarget !== null;

  if (deployment.status === 'success') {
    return {
      stage: 'complete',
      percent: 100,
      label: 'Deployment complete',
      mode: 'complete'
    };
  }

  if (deployment.status === 'failed' || deployment.status === 'partial') {
    return {
      stage: 'failed',
      percent: 100,
      label: deployment.status === 'partial' ? 'Deployment partially complete' : 'Deployment failed',
      mode: 'failed'
    };
  }

  if (!hasApi && !hasWebsite && !hasCoolify) {
    return {
      stage: 'preparing',
      percent: 5,
      label: 'Preparing deployment…',
      mode: 'determinate'
    };
  }

  if (hasCoolify && !hasApi && !hasWebsite) {
    return computeSingleTargetProgress(coolifyTarget!, 'Coolify');
  }

  if (hasApi && !hasWebsite) {
    return computeSingleTargetProgress(apiTarget!, 'API');
  }

  if (hasWebsite && !hasApi) {
    return computeSingleTargetProgress(websiteTarget!, 'Website');
  }

  return computeSplitOriginProgress(deployment.status, apiTarget!, websiteTarget!);
}

function computeSingleTargetProgress(target: DeploymentTarget, role: string): DeploymentProgress {
  switch (target.status) {
    case 'in_progress':
      return {
        stage: role === 'API' ? 'api_deploying' : role === 'Coolify' ? 'api_deploying' : 'website_deploying',
        percent: 50,
        label: role === 'API'
          ? 'Deploying your API…'
          : role === 'Coolify'
            ? 'Publishing to Coolify…'
            : 'Deploying your website…',
        mode: 'determinate'
      };
    case 'success':
      return {
        stage: 'finishing',
        percent: 95,
        label: 'Finishing up…',
        mode: 'determinate'
      };
    case 'failed':
      return {
        stage: 'failed',
        percent: 100,
        label: `${role} deployment failed`,
        mode: 'failed'
      };
    default:
      return {
        stage: 'preparing',
        percent: 5,
        label: 'Preparing deployment…',
        mode: 'determinate'
      };
  }
}

function computeSplitOriginProgress(
  deploymentStatus: string,
  apiTarget: DeploymentTarget,
  websiteTarget: DeploymentTarget
): DeploymentProgress {
  if (apiTarget.status === 'failed' || websiteTarget.status === 'failed') {
    return {
      stage: 'failed',
      percent: 100,
      label: 'Deployment needs attention',
      mode: 'failed'
    };
  }

  if (apiTarget.status === 'in_progress') {
    return {
      stage: 'api_deploying',
      percent: 30,
      label: 'Deploying your API…',
      mode: 'determinate'
    };
  }

  if (apiTarget.status !== 'success') {
    return {
      stage: 'preparing',
      percent: 5,
      label: 'Preparing deployment…',
      mode: 'determinate'
    };
  }

  if (websiteTarget.status === 'in_progress') {
    return {
      stage: 'website_deploying',
      percent: 70,
      label: 'Deploying your website…',
      mode: 'determinate'
    };
  }

  if (websiteTarget.status === 'success' && deploymentStatus === 'in_progress') {
    return {
      stage: 'finishing',
      percent: 95,
      label: 'Finishing cross-origin wiring…',
      mode: 'determinate'
    };
  }

  if (websiteTarget.status === 'success') {
    return {
      stage: 'complete',
      percent: 100,
      label: 'Deployment complete',
      mode: 'complete'
    };
  }

  return {
    stage: 'api_done',
    percent: 45,
    label: 'API is live — starting website…',
    mode: 'determinate'
  };
}

function findTarget(targets: DeploymentTarget[], providerName: string): DeploymentTarget | null {
  return targets.find(target => target.providerName === providerName) ?? null;
}
