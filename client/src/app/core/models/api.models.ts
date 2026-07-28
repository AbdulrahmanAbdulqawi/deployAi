export interface ApiError {
  error: {
    code: string;
    message: string;
  };
}

export interface ProviderInfo {
  name: string;
  displayName: string;
  apiStyle: string;
}

export enum ProviderName {
  Vercel = 'vercel',
  Railway = 'railway',
  Coolify = 'coolify',
}

export interface CredentialSummary {
  id: string;
  providerName: string;
  label: string;
  isValid: boolean;
  lastValidatedAt?: string;
  instanceUrl?: string;
}

export interface GitHubRepo {
  fullName: string;
  defaultBranch: string;
  private: boolean;
}

export interface GitHubBranch {
  name: string;
}

export interface FrontendBuildProfile {
  rootDirectory: string;
  buildCommand: string;
  installCommand: string;
  outputDirectory: string;
  framework?: string;
}

export interface ServerBuildProfile {
  rootDirectory: string;
  buildCommand?: string;
  installCommand?: string;
  startCommand?: string;
  framework?: string;
  dockerfilePath?: string;
  serviceDirectory?: string;
}

export interface DatabaseRequirementProfile {
  requiresPostgres: boolean;
  requiresRedis: boolean;
  connectionStringKeys: string[];
  postgresDatabaseName?: string;
}

export interface DeploymentPlanPart {
  role: string;
  providerName: string;
  rootDirectory?: string;
  serviceDirectory?: string;
  buildCommand?: string;
  installCommand?: string;
  startCommand?: string;
  outputDirectory?: string;
  framework?: string;
  dockerfilePath?: string;
  databaseEngine?: string;
}

export interface ClarifyingQuestionOption {
  id: string;
  label: string;
  description: string;
  resolvesToParts: DeploymentPlanPart[];
}

export interface ClarifyingQuestion {
  prompt: string;
  options: ClarifyingQuestionOption[];
}

export interface DeploymentPlan {
  parts: DeploymentPlanPart[];
  confidence: 'high' | 'low';
  plainSummary: string;
  planKind?: DeploymentPlanKind;
  clarifyingQuestion?: ClarifyingQuestion;
}

export enum DeploymentPlanKind {
  Default = 'default',
  CoolifyFullStack = 'coolify-fullstack',
  /** One Docker Compose resource serving the whole app from a single origin. */
  CoolifyCompose = 'coolify-compose',
  CoolifySingle = 'coolify-single',
}

export interface GitHubContentDirectory {
  name: string;
  path: string;
}

export interface ProviderProject {
  id: string;
  name: string;
  url?: string;
  gitBranch?: string;
}

export interface CoolifyInfrastructureResource {
  id: string;
  name: string;
}

export interface CoolifyInfrastructure {
  projects: CoolifyInfrastructureResource[];
  servers: CoolifyInfrastructureResource[];
  githubApps: CoolifyInfrastructureResource[];
}

export enum CoolifyBuildPack {
  Nixpacks = 'nixpacks',
  Static = 'static',
  Dockerfile = 'dockerfile',
  DockerCompose = 'dockercompose',
  Railpack = 'railpack',
}

export interface ProviderEnvVar {
  id: string;
  key: string;
  value?: string;
  type: string;
  targets: string[];
  valueHidden: boolean;
}

export interface ProjectTarget {
  id?: string;
  providerName: string;
  credentialId: string;
  providerProjectId: string;
  config?: string;
}

export interface ProjectSummary {
  id: string;
  name: string;
  logoKey?: string | null;
  githubRepoFullName: string;
  defaultBranch: string;
  targets: { providerName: string }[];
  latestDeployment?: {
    id: string;
    status: string;
    completedAt?: string;
    canRequestClaudeFix?: boolean;
    fixTargetId?: string;
  };
}

export interface ProjectDetail extends Omit<ProjectSummary, 'latestDeployment' | 'targets'> {
  targets: ProjectTarget[];
  autoDeployEnabled?: boolean;
  health?: ProjectHealthState | null;
  environmentSync?: EnvironmentSyncState | null;
}

export enum ProjectHealthStatus {
  Healthy = 'healthy',
  Degraded = 'degraded',
  Down = 'down',
  Unknown = 'unknown'
}

export interface ProjectHealthState {
  lastCheckedAt: string;
  status: ProjectHealthStatus;
  passedChecks: number;
  totalChecks: number;
  summary?: string;
  deploymentId?: string;
}

export interface NotificationPreferencesResponse {
  emailOnSuccess: boolean;
  emailOnFailure: boolean;
  emailOnComplete: boolean;
}

export interface EnvironmentSyncState {
  lastSyncedAt: string;
  source: string;
  success: boolean;
  driftDetected: boolean;
  resolvedWebsiteUrl?: string;
  resolvedApiUrl?: string;
  verificationMessages: string[];
  driftDetails: string[];
}

export interface EnvironmentSyncResult {
  success: boolean;
  skipped: boolean;
  skipReason?: string | null;
  driftDetected: boolean;
  resolvedWebsiteUrl?: string | null;
  resolvedApiUrl?: string | null;
  railwayKeysApplied: string[];
  vercelKeysApplied: string[];
  verificationMessages: string[];
  driftDetails: string[];
  source: string;
  completedAt: string;
}

export interface ProjectServiceView {
  id: string;
  providerName: string;
  credentialId: string;
  providerProjectId: string;
  role?: string;
  databaseEngine?: string;
  displayName: string;
  railwayProjectId?: string;
  linkedConnectionKeys: string[];
  canManage: boolean;
  serviceDirectory?: string;
  rootDirectory?: string;
}

export interface ProjectServicesResponse {
  applicationServices: ProjectServiceView[];
  dataServices: ProjectServiceView[];
  /** True when a server target runs on a provider that can provision managed databases (Railway or Coolify). */
  hasManagedServer: boolean;
  includePostgres: boolean;
  includeRedis: boolean;
}

export interface ProjectServiceStatus {
  status: string;
  deployUrl?: string;
  lastDeployedAt?: string;
}

export interface DataServiceTableInfo {
  name: string;
  rowCount: number;
}

export interface DataServiceInfo {
  engine: string;
  metadata: {
    databaseName?: string;
    host?: string;
    port?: number;
    volumeMountPath?: string;
    railwayServiceId?: string;
    railwayProjectId?: string;
    railwayEnvironmentId?: string;
  };
  linkedConnectionKeys: string[];
  connectionSummary?: string;
  railwayUrl?: string;
  inspection?: {
    connected: boolean;
    message?: string;
    tables: DataServiceTableInfo[];
    migrationsApplied: string[];
  };
}

export interface DeploymentSummary {
  id: string;
  branch: string;
  gitCommitSha?: string;
  gitCommitMessage?: string;
  status: string;
  triggeredBy?: string;
  durationSeconds?: number;
  startedAt?: string;
  completedAt?: string;
  targets: {
    providerName: string;
    /** 'website' | 'server' | 'database' | 'storage'. Absent on targets saved before roles were recorded. */
    role?: string | null;
    status: string;
    deployUrl?: string;
  }[];
}

export interface DeploymentFailureAnalysis {
  category: 'code_build' | 'infrastructure' | 'unknown';
  summary: string;
  errorExcerpt?: string;
  referencedFiles: string[];
  errorCount?: number;
  canRequestClaudeFix: boolean;
}

export interface UseBranchDeployResult {
  branch: string;
  deploymentId?: string | null;
  message?: string | null;
}

export interface DeploymentFixResult {
  branchName: string;
  pullRequestNumber: number;
  pullRequestUrl: string;
  committedFiles: string[];
  durationSeconds?: number;
}

export interface DeploymentDetail {
  id: string;
  projectId: string;
  branch: string;
  gitCommitSha?: string;
  gitCommitMessage?: string;
  status: string;
  triggeredBy?: string;
  startedAt?: string;
  completedAt?: string;
  durationSeconds?: number;
  targets: {
    id: string;
    deployTargetId: string;
    providerName: string;
    /** 'website' | 'server' | 'database' | 'storage'. Absent on targets saved before roles were recorded. */
    role?: string | null;
    status: string;
    deployUrl?: string;
    startedAt?: string;
    completedAt?: string;
    failureAnalysis?: DeploymentFailureAnalysis | null;
  }[];
}

export type DeploymentVerificationScope = 'website' | 'server' | 'both';
export type VerificationCheckStatus = 'passed' | 'failed' | 'warning' | 'skipped';
export type VerificationSuggestedAction =
  | 'reconnect'
  | 'redeploy_website'
  | 'redeploy_server'
  | 'fix_output_directory';

export interface DeploymentVerificationCheck {
  id: string;
  target: 'website' | 'server' | 'connection';
  label: string;
  status: VerificationCheckStatus;
  message: string;
  url?: string | null;
  suggestedAction?: VerificationSuggestedAction | null;
  canRequestClaudeFix: boolean;
  referencedFiles: string[];
}

export interface DeploymentVerificationResult {
  success: boolean;
  scope: DeploymentVerificationScope;
  completedAt: string;
  checks: DeploymentVerificationCheck[];
}

export interface DeploymentLogLine {
  providerName: string;
  sequence: number;
  line: string;
  loggedAt: string;
}

export interface TriggerDeploymentResponse {
  deploymentId: string;
  status: string;
  targets: { providerName: string; status: string }[];
}

export interface MissingDeploymentFile {
  path: string;
  reason: string;
  severity: 'blocking' | 'recommended' | 'warning';
}

export interface DeploymentReadinessResult {
  isReady: boolean;
  commitSha?: string;
  usesSplitOrigin: boolean;
  websiteProviderName?: string;
  serverProviderName?: string;
  missingFiles: MissingDeploymentFile[];
  warnings: string[];
}

export interface DeploymentSetupResult {
  branchName: string;
  pullRequestNumber: number;
  pullRequestUrl: string;
  committedFiles: string[];
}

export type DeploymentFixStreamEvent =
  | { type: 'started'; startedAt: string }
  | { type: 'log'; message: string }
  | ({ type: 'complete' } & DeploymentFixResult & { durationSeconds?: number })
  | { type: 'error'; code: string; message: string };

export type DeploymentSetupStreamEvent =
  | { type: 'started'; startedAt: string }
  | { type: 'log'; message: string }
  | ({ type: 'complete' } & DeploymentSetupResult & { durationSeconds?: number })
  | { type: 'error'; code: string; message: string };

export interface DeploymentSetupMergeResult {
  merged: boolean;
  envSync: 'completed' | 'pending' | 'skipped';
  envSyncReason?: string | null;
  railwayKeysApplied: string[];
  vercelKeysApplied: string[];
}

export interface StorageConnectionSummary {
  id: string;
  providerName: string;
  label: string;
  isValid: boolean;
  lastValidatedAt?: string | null;
  endpoint?: string | null;
  region?: string | null;
}

export interface StorageProviderInfo {
  name: string;
  displayName: string;
}

export interface StorageBucket {
  name: string;
  createdAt?: string | null;
}

/** An environment variable DeployAI manages for an app — viewable and editable after deploy. */
export interface EnvVariable {
  key: string;
  value: string;
  isSecret: boolean;
}

/**
 * Configuration an application reported missing in its own startup output.
 *
 * `kind` matters: .NET's "Jwt configuration missing" names the section, not which of
 * `Jwt__Key`/`Jwt__Issuer` is absent, so a `section` gives a prefix to complete rather than a key
 * to write. Filling it in blindly would set a variable no app reads.
 */
export interface MissingConfigurationItem {
  name: string;
  kind: 'section' | 'variable';
  evidence: string;
  suggestedValue: string;
}

/** One env var the repo was detected to need, with an optional server-suggested value. */
export interface EnvSchemaVar {
  name: string;
  isSecret: boolean;
  hasDefault: boolean;
  defaultValue?: string | null;
  category: 'generic' | 'domain' | 'storage' | 'database' | 'adminemail';
  sources: string[];
  suggestedValue?: string | null;
}
