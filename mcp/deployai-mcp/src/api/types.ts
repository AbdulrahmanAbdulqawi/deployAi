export interface ApiErrorBody {
  error: {
    code: string;
    message: string;
  };
}

export enum DeploymentVerificationScope {
  Website = 'website',
  Server = 'server',
  Both = 'both',
}

export enum DeploymentFailureCategory {
  CodeBuild = 'code_build',
  Infrastructure = 'infrastructure',
  Unknown = 'unknown',
}

export enum VerificationCheckStatus {
  Passed = 'passed',
  Failed = 'failed',
  Warning = 'warning',
  Skipped = 'skipped',
}

export interface GitHubRepo {
  fullName: string;
  defaultBranch: string;
  private: boolean;
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

export interface DeploymentPlan {
  parts: DeploymentPlanPart[];
  confidence: 'high' | 'low';
  plainSummary: string;
  clarifyingQuestion?: {
    prompt: string;
    options: {
      id: string;
      label: string;
      description: string;
      resolvesToParts: DeploymentPlanPart[];
    }[];
  };
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

export interface ProjectTarget {
  id?: string;
  providerName: string;
  credentialId: string;
  providerProjectId: string;
  config?: string;
}

export interface ProjectDetail extends Omit<ProjectSummary, 'latestDeployment' | 'targets'> {
  targets: ProjectTarget[];
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
  category: DeploymentFailureCategory;
  summary: string;
  errorExcerpt?: string;
  referencedFiles: string[];
  errorCount?: number;
  canRequestClaudeFix: boolean;
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
  durationSeconds?: number;
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

export interface DeploymentVerificationCheck {
  id: string;
  target: 'website' | 'server' | 'connection';
  label: string;
  status: VerificationCheckStatus;
  message: string;
  url?: string | null;
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
  missingFiles: MissingDeploymentFile[];
  warnings: string[];
}

export interface DeploymentSetupResult {
  branchName: string;
  pullRequestNumber: number;
  pullRequestUrl: string;
  committedFiles: string[];
}

export interface DeploymentFixResult {
  branchName: string;
  pullRequestNumber: number;
  pullRequestUrl: string;
  committedFiles: string[];
  durationSeconds?: number;
}

export interface DeploymentSetupMergeResult {
  merged: boolean;
  envSync: 'completed' | 'pending' | 'skipped';
  envSyncReason?: string | null;
  railwayKeysApplied: string[];
  vercelKeysApplied: string[];
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

export interface CredentialSummary {
  id: string;
  providerName: string;
  label: string;
  isValid: boolean;
  lastValidatedAt?: string;
}

export interface ProviderProject {
  id: string;
  name: string;
  url?: string;
}

export interface StreamAggregateResult<TComplete> {
  progress: string[];
  result: TComplete;
}

export interface RefreshTokenResponse {
  accessToken: string;
  refreshToken: string;
  expiresIn: number;
}

/** Mirrors DeployAI.Core.Providers.CoolifyInventory. Read-only; see the coolify_inventory tool. */
export interface CoolifyInventoryApplication {
  uuid: string;
  name: string;
  buildPack: string | null;
  status: string | null;
  gitRepository: string | null;
  gitBranch: string | null;
  domains: string[];
}

export interface CoolifyInventoryDatabase {
  uuid: string;
  name: string;
  engine: string | null;
  status: string | null;
}

export interface CoolifyInventoryEnvironment {
  uuid: string;
  name: string;
  applications: CoolifyInventoryApplication[];
  databases: CoolifyInventoryDatabase[];
}

export interface CoolifyInventoryProject {
  uuid: string;
  name: string;
  environments: CoolifyInventoryEnvironment[];
  /** True when Coolify refused to list this project's environments — not the same as none. */
  environmentsInconclusive: boolean;
}

export interface CoolifyInventory {
  projects: CoolifyInventoryProject[];
  /** Resources whose environment matched no listed project; reported rather than dropped. */
  unplacedApplications: CoolifyInventoryApplication[];
  unplacedDatabases: CoolifyInventoryDatabase[];
}
