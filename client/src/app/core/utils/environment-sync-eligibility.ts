import { DeploymentDetail, ProviderName } from '../models/api.models';

export function canSyncEnvironmentUrls(
  deployment: DeploymentDetail | null | undefined,
  isComplete = true
): boolean {
  if (!deployment || !isComplete) {
    return false;
  }

  const hasSuccessfulVercelRailwayPair =
    deployment.targets.some(
      target => target.providerName === ProviderName.Railway && target.status === 'success'
    ) &&
    deployment.targets.some(
      target => target.providerName === ProviderName.Vercel && target.status === 'success'
    );

  const hasSuccessfulCoolifyStack =
    deployment.targets.filter(
      target => target.providerName === ProviderName.Coolify && target.status === 'success'
    ).length >= 2;

  return hasSuccessfulVercelRailwayPair || hasSuccessfulCoolifyStack;
}
