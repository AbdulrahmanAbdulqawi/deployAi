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

export interface CredentialSummary {
  id: string;
  providerName: string;
  label: string;
  isValid: boolean;
  lastValidatedAt?: string;
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
  clarifyingQuestion?: ClarifyingQuestion;
}

export interface GitHubContentDirectory {
  name: string;
  path: string;
}

export interface ProviderProject {
  id: string;
  name: string;
  url?: string;
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
  environmentSync?: EnvironmentSyncState | null;
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
  hasRailwayServer: boolean;
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
  durationSeconds?: number;
  startedAt?: string;
  completedAt?: string;
  targets: {
    providerName: string;
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

export interface DeploymentFixResult {
  branchName: string;
  pullRequestNumber: number;
  pullRequestUrl: string;
  committedFiles: string[];
}

export interface DeploymentDetail {
  id: string;
  projectId: string;
  branch: string;
  gitCommitSha?: string;
  gitCommitMessage?: string;
  status: string;
  startedAt?: string;
  completedAt?: string;
  targets: {
    id: string;
    deployTargetId: string;
    providerName: string;
    status: string;
    deployUrl?: string;
    startedAt?: string;
    completedAt?: string;
    failureAnalysis?: DeploymentFailureAnalysis | null;
  }[];
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
  | { type: 'log'; message: string }
  | ({ type: 'complete' } & DeploymentFixResult)
  | { type: 'error'; code: string; message: string };

export type DeploymentSetupStreamEvent =
  | { type: 'log'; message: string }
  | ({ type: 'complete' } & DeploymentSetupResult)
  | { type: 'error'; code: string; message: string };

export interface DeploymentSetupMergeResult {
  merged: boolean;
  envSync: 'completed' | 'pending' | 'skipped';
  envSyncReason?: string | null;
  railwayKeysApplied: string[];
  vercelKeysApplied: string[];
}
