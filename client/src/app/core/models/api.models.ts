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
  };
}

export interface ProjectDetail extends Omit<ProjectSummary, 'latestDeployment' | 'targets'> {
  targets: ProjectTarget[];
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

export interface DeploymentDetail {
  id: string;
  projectId: string;
  branch: string;
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
