import { ProviderName } from '../models/api.models';
import { providerLabel } from './target-config';

export function isVercelProvider(providerName: string): boolean {
  return providerName === ProviderName.Vercel;
}

export function isRailwayProvider(providerName: string): boolean {
  return providerName === ProviderName.Railway;
}

export function isCoolifyProvider(providerName: string): boolean {
  return providerName === ProviderName.Coolify;
}

export function providerSubtitle(providerName: string): string {
  if (isVercelProvider(providerName)) {
    return 'What your visitors see';
  }
  if (isRailwayProvider(providerName)) {
    return 'Powers your app behind the scenes';
  }
  if (isCoolifyProvider(providerName)) {
    return 'Runs on your Coolify server';
  }
  return providerLabel(providerName);
}

export function providerLiveStatusText(providerName: string, status: string): string {
  if (isCoolifyProvider(providerName)) {
    switch (status) {
      case 'in_progress':
        return 'Publishing to Coolify…';
      case 'success':
        return 'Your Coolify app is live';
      case 'failed':
        return 'Coolify publish did not finish';
      default:
        return 'Waiting to start…';
    }
  }

  const isSite = isVercelProvider(providerName);
  switch (status) {
    case 'in_progress':
      return isSite ? 'Publishing your website…' : 'Starting your backend…';
    case 'success':
      return isSite ? 'Your website is live' : 'Your backend is live';
    case 'failed':
      return isSite ? "Your website didn't finish" : "Your backend didn't finish";
    default:
      return 'Waiting to start…';
  }
}
