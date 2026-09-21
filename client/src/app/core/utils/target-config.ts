import { ProviderName } from '../models/api.models';

export interface TargetConfig {
  rootDirectory?: string;
  role?: string;
  outputDirectory?: string;
  buildCommand?: string;
  installCommand?: string;
  startCommand?: string;
  framework?: string;
  dockerfilePath?: string;
  serviceDirectory?: string;
  databaseEngine?: string;
  linkedServiceName?: string;
  railwayProjectId?: string;
  coolifyGitBranch?: string;
  coolifyBuildPack?: string;
  includePostgres?: boolean;
  includeRedis?: boolean;
}

export function parseTargetConfig(config?: string): TargetConfig {
  if (!config) {
    return {};
  }

  try {
    return JSON.parse(config) as TargetConfig;
  } catch {
    return {};
  }
}

export function databaseEngineLabel(engine?: string): string {
  if (engine === 'postgres') return 'PostgreSQL';
  if (engine === 'redis') return 'Redis';
  return engine ?? 'Database';
}

export function providerLabel(providerName: string): string {
  if (providerName === ProviderName.Vercel) return 'Vercel';
  if (providerName === ProviderName.Railway) return 'Railway';
  if (providerName === ProviderName.Coolify) return 'Coolify';
  return providerName;
}

/**
 * Names the part of the app a target is — website, server, database, file storage — for anywhere
 * the user needs to know *what* broke rather than *where* it runs.
 *
 * Prefer this over `roleLabelForProvider` whenever the question is about the app. Provider-name
 * dispatch cannot answer it: Coolify hosts a website and a server as two applications, so both
 * come back "Your Coolify app" and a screen reporting a failure cannot say which one failed.
 * CLAUDE.md states the rule — dispatch keys off role, never provider name.
 *
 * Provider is the fallback only because `role` is absent on targets saved before roles were
 * recorded; a legacy deployment must still render something rather than a blank.
 */
export function partLabelForTarget(target: { role?: string | null; providerName: string }): string {
  switch (target.role) {
    case 'website':
      return 'Website';
    case 'server':
      return 'Server';
    case 'database':
      return 'Database';
    case 'storage':
      return 'File storage';
  }

  // No usable role: infer from the provider, which is only unambiguous for the single-purpose ones.
  if (target.providerName === ProviderName.Vercel) return 'Website';
  if (target.providerName === ProviderName.Railway) return 'Server';
  return target.providerName;
}

export function roleLabelForProvider(providerName: string): string {
  if (providerName === ProviderName.Vercel) return 'Your site';
  if (providerName === ProviderName.Railway) return 'Your API';
  if (providerName === ProviderName.Coolify) return 'Your Coolify app';
  return providerName;
}

export function plainTargetSummary(targets: { providerName: string }[]): string {
  const parts: string[] = [];
  if (targets.some(t => t.providerName === ProviderName.Vercel)) {
    parts.push('Site');
  }
  if (targets.some(t => t.providerName === ProviderName.Railway)) {
    parts.push('API');
  }
  if (targets.some(t => t.providerName === ProviderName.Coolify)) {
    parts.push('Coolify');
  }
  const other = targets
    .filter(t =>
      t.providerName !== ProviderName.Vercel &&
      t.providerName !== ProviderName.Railway &&
      t.providerName !== ProviderName.Coolify)
    .map(t => t.providerName);
  return [...parts, ...other].join(' + ') || 'App';
}

export function serviceStatusLabel(status?: string): string {
  switch (status) {
    case 'running':
      return 'Running';
    case 'deploying':
      return 'Starting';
    case 'failed':
      return 'Needs attention';
    case 'not_deployed':
      return 'Not started';
    default:
      return 'Checking…';
  }
}
