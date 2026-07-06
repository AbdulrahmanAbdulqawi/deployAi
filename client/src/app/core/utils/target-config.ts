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
  if (providerName === 'vercel') return 'Vercel';
  if (providerName === 'railway') return 'Railway';
  return providerName;
}

export function roleLabelForProvider(providerName: string): string {
  if (providerName === 'vercel') return 'Your site';
  if (providerName === 'railway') return 'Your API';
  return providerName;
}

export function plainTargetSummary(targets: { providerName: string }[]): string {
  const parts: string[] = [];
  if (targets.some(t => t.providerName === 'vercel')) {
    parts.push('Site');
  }
  if (targets.some(t => t.providerName === 'railway')) {
    parts.push('API');
  }
  const other = targets
    .filter(t => t.providerName !== 'vercel' && t.providerName !== 'railway')
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
