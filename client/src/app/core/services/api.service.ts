import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { timeout, Observable } from 'rxjs';
import {
  CredentialSummary,
  DatabaseRequirementProfile,
  DeploymentDetail,
  DeploymentLogLine,
  DeploymentVerificationResult,
  DeploymentVerificationScope,
  DeploymentPlan,
  DeploymentPlanPart,
  DeploymentReadinessResult,
  DeploymentFixResult,
  DeploymentFixStreamEvent,
  DeploymentSetupResult,
  DeploymentSetupStreamEvent,
  DeploymentSetupMergeResult,
  DeploymentSummary,
  GitHubBranch,
  GitHubContentDirectory,
  FrontendBuildProfile,
  ServerBuildProfile,
  GitHubRepo,
  EnvironmentSyncResult,
  EnvironmentSyncState,
  ProjectDetail,
  NotificationPreferencesResponse,
  ProjectServicesResponse,
  ProjectServiceStatus,
  DataServiceInfo,
  ProjectSummary,
  ProviderInfo,
  ProviderProject,
  ProviderEnvVar,
  CoolifyInfrastructure,
  CoolifyInfrastructureResource,
  CoolifyBuildPack,
  TriggerDeploymentResponse,
  UseBranchDeployResult,
  StorageConnectionSummary,
  StorageBucket,
  EnvSchemaVar,
  EnvVariable,
  MissingConfigurationItem
} from '../models/api.models';

@Injectable({ providedIn: 'root' })
export class ApiService {
  private static readonly claudeAgentTimeoutMs = 45 * 60 * 1000;
  constructor(private readonly http: HttpClient) {}

  health() {
    return this.http.get<{ status: string; service: string }>('/api/health');
  }

  getProviders() {
    return this.http.get<{ providers: ProviderInfo[] }>('/api/providers');
  }

  getCredentials() {
    return this.http.get<{ credentials: CredentialSummary[] }>('/api/credentials');
  }

  addCredential(providerName: string, token: string, label?: string, instanceUrl?: string) {
    return this.http.post<CredentialSummary>('/api/credentials', { providerName, token, label, instanceUrl });
  }

  deleteCredential(id: string) {
    return this.http.delete(`/api/credentials/${id}`);
  }

  listProviderProjects(credentialId: string) {
    return this.http.get<{ projects: ProviderProject[] }>(`/api/credentials/${credentialId}/projects`);
  }

  listRepos(page = 1, perPage = 30, search?: string) {
    const params: Record<string, string | number> = { page, perPage };
    if (search) {
      params['search'] = search;
    }
    return this.http.get<{ repos: GitHubRepo[]; page: number; hasMore: boolean }>('/api/github/repos', { params });
  }

  listBranches(owner: string, repo: string) {
    return this.http.get<{ branches: GitHubBranch[] }>(`/api/github/repos/${owner}/${repo}/branches`);
  }

  listRepoContents(owner: string, repo: string, path: string, gitRef: string) {
    const params: Record<string, string> = { ref: gitRef };
    if (path) {
      params['path'] = path;
    }
    return this.http.get<{ path: string; directories: GitHubContentDirectory[] }>(
      `/api/github/repos/${owner}/${repo}/contents`,
      { params }
    );
  }

  detectBuildProfile(owner: string, repo: string, path: string, gitRef: string) {
    const params: Record<string, string> = { ref: gitRef };
    if (path) {
      params['path'] = path;
    }
    return this.http.get<FrontendBuildProfile>(
      `/api/github/repos/${owner}/${repo}/build-profile`,
      { params }
    );
  }

  detectServerBuildProfile(owner: string, repo: string, path: string, gitRef: string) {
    const params: Record<string, string> = { ref: gitRef };
    if (path) {
      params['path'] = path;
    }
    return this.http.get<ServerBuildProfile>(
      `/api/github/repos/${owner}/${repo}/server-build-profile`,
      { params }
    );
  }

  detectDatabaseRequirements(owner: string, repo: string, path: string, gitRef: string) {
    const params: Record<string, string> = { ref: gitRef };
    if (path) {
      params['path'] = path;
    }
    return this.http.get<DatabaseRequirementProfile>(
      `/api/github/repos/${owner}/${repo}/database-requirements`,
      { params }
    );
  }

  getDeploymentPlan(owner: string, repo: string, gitRef: string) {
    return this.http.get<DeploymentPlan>(
      `/api/github/repos/${owner}/${repo}/deployment-plan`,
      { params: { ref: gitRef } }
    );
  }

  scanDeploymentReadiness(owner: string, repo: string, ref: string, parts: DeploymentPlanPart[]) {
    return this.http.post<DeploymentReadinessResult>(
      `/api/github/repos/${owner}/${repo}/deployment-readiness`,
      { ref, parts }
    );
  }

  generateDeploymentSetup(
    owner: string,
    repo: string,
    ref: string,
    parts: DeploymentPlanPart[],
    projectId?: string,
    forceRegenerate = false,
    useAi?: boolean
  ): Observable<DeploymentSetupStreamEvent> {
    return new Observable<DeploymentSetupStreamEvent>((subscriber) => {
      const abortController = new AbortController();
      const timeoutId = window.setTimeout(() => abortController.abort(), ApiService.claudeAgentTimeoutMs);

      const run = async (): Promise<void> => {
        try {
          const token = localStorage.getItem('deployai_access_token');
          const response = await fetch(`/api/github/repos/${owner}/${repo}/deployment-setup`, {
            method: 'POST',
            headers: {
              'Content-Type': 'application/json',
              ...(token ? { Authorization: `Bearer ${token}` } : {})
            },
            body: JSON.stringify({
              gitRef: ref,
              parts,
              projectId: projectId ?? null,
              forceRegenerate,
              useAi: useAi ?? null
            }),
            signal: abortController.signal
          });

          const contentType = response.headers.get('content-type') ?? '';
          if (!response.ok && contentType.includes('application/json')) {
            const body = (await response.json()) as { error?: { code?: string; message?: string } };
            subscriber.next({
              type: 'error',
              code: body.error?.code ?? 'setup_generation_failed',
              message: body.error?.message ?? 'Could not generate deployment files.'
            });
            subscriber.complete();
            return;
          }

          if (!response.body) {
            subscriber.error(new Error('No response body from setup stream.'));
            return;
          }

          const reader = response.body.getReader();
          const decoder = new TextDecoder();
          let buffer = '';

          while (true) {
            const { done, value } = await reader.read();
            if (done) {
              break;
            }

            buffer += decoder.decode(value, { stream: true });
            const lines = buffer.split('\n');
            buffer = lines.pop() ?? '';

            for (const line of lines) {
              const trimmed = line.trim();
              if (!trimmed) {
                continue;
              }

              const event = JSON.parse(trimmed) as DeploymentSetupStreamEvent;
              subscriber.next(event);

              if (event.type === 'complete' || event.type === 'error') {
                subscriber.complete();
                return;
              }
            }
          }

          subscriber.complete();
        } catch (error) {
          if (abortController.signal.aborted) {
            subscriber.next({
              type: 'error',
              code: 'claude_request_timeout',
              message:
                'Setup generation timed out. Try again — large repositories can take several minutes.'
            });
            subscriber.complete();
            return;
          }

          subscriber.error(error);
        } finally {
          window.clearTimeout(timeoutId);
        }
      };

      void run();

      return () => {
        window.clearTimeout(timeoutId);
        abortController.abort();
      };
    });
  }

  mergeDeploymentSetup(owner: string, repo: string, pullRequestNumber: number, projectId?: string) {
    return this.http.post<DeploymentSetupMergeResult>(
      `/api/github/repos/${owner}/${repo}/deployment-setup/merge`,
      { pullRequestNumber, projectId: projectId ?? null }
    );
  }

  useDeploymentSetupBranch(projectId: string, branch: string) {
    return this.http.post<{ branch: string }>(
      `/api/projects/${projectId}/deployment-setup/use-branch`,
      { branch }
    );
  }

  useBranchAndDeploy(projectId: string, branch: string, deploy = true) {
    return this.http.post<UseBranchDeployResult>(
      `/api/projects/${projectId}/use-branch-and-deploy`,
      { branch, deploy }
    );
  }

  getAiSetupPreference(projectId: string) {
    return this.http.get<{ enabled: boolean | null }>(
      `/api/projects/${projectId}/settings/ai-setup`
    );
  }

  setAiSetupPreference(projectId: string, enabled: boolean) {
    return this.http.put<{ enabled: boolean }>(
      `/api/projects/${projectId}/settings/ai-setup`,
      { enabled }
    );
  }

  getProjectDeploymentReadiness(projectId: string, ref?: string) {
    const params = ref ? { ref } : undefined;
    return this.http.get<DeploymentReadinessResult>(
      `/api/projects/${projectId}/deployment-readiness`,
      { params }
    );
  }

  getProjects() {
    return this.http.get<{ projects: ProjectSummary[] }>('/api/projects');
  }

  getProject(id: string) {
    return this.http.get<ProjectDetail>(`/api/projects/${id}`);
  }

  createProject(payload: {
    name: string;
    githubRepoFullName: string;
    defaultBranch: string;
    logoKey?: string | null;
    includePostgres?: boolean;
    includeRedis?: boolean;
    targets: { providerName: string; credentialId: string; providerProjectId: string; config?: string }[];
  }) {
    return this.http.post<ProjectDetail>('/api/projects', payload);
  }

  createProjectFromPlan(payload: {
    name: string;
    githubRepoFullName: string;
    defaultBranch: string;
    logoKey?: string | null;
    includePostgres?: boolean;
    includeRedis?: boolean;
    parts: {
      role: string;
      providerName: string;
      credentialId: string;
      providerProjectId: string;
      rootDirectory?: string | null;
      serviceDirectory?: string | null;
      buildCommand?: string | null;
      installCommand?: string | null;
      startCommand?: string | null;
      outputDirectory?: string | null;
      framework?: string | null;
      dockerfilePath?: string | null;
    }[];
  }) {
    return this.http.post<ProjectDetail>('/api/projects/from-plan', payload);
  }

  updateProject(
    id: string,
    payload: {
      name?: string;
      defaultBranch?: string;
      logoKey?: string | null;
      autoDeployEnabled?: boolean;
      targets?: { providerName: string; credentialId: string; providerProjectId: string; config?: string }[];
    }
  ) {
    return this.http.put<ProjectDetail>(`/api/projects/${id}`, payload);
  }

  getNotificationPreferences() {
    return this.http.get<NotificationPreferencesResponse>('/api/notifications/preferences');
  }

  updateNotificationPreferences(payload: { emailOnSuccess: boolean; emailOnFailure: boolean }) {
    return this.http.put<NotificationPreferencesResponse>('/api/notifications/preferences', payload);
  }

  deleteProject(id: string) {
    return this.http.delete(`/api/projects/${id}`);
  }

  provisionRailwayDatabases(projectId: string, payload: { postgres: boolean; redis: boolean }) {
    return this.http.post<ProjectDetail>(`/api/projects/${projectId}/railway-databases`, payload);
  }

  autoProvisionRailwayDatabases(projectId: string) {
    return this.http.post<ProjectDetail>(`/api/projects/${projectId}/railway-databases/auto`, {});
  }

  getProjectServices(projectId: string) {
    return this.http.get<ProjectServicesResponse>(`/api/projects/${projectId}/services`);
  }

  getProjectServiceStatus(projectId: string, targetId: string) {
    return this.http.get<ProjectServiceStatus>(`/api/projects/${projectId}/services/${targetId}/status`);
  }

  redeployProjectService(projectId: string, targetId: string) {
    return this.http.post<{ message: string }>(`/api/projects/${projectId}/services/${targetId}/redeploy`, {});
  }

  redeployDeployTarget(projectId: string, deployTargetId: string, branch?: string) {
    return this.http.post<TriggerDeploymentResponse>(
      `/api/projects/${projectId}/deployments/targets/${deployTargetId}`,
      { branch }
    );
  }

  syncEnvironmentUrls(projectId: string, redeployRailway = true) {
    return this.http.post<EnvironmentSyncResult>(
      `/api/projects/${projectId}/environment/sync`,
      {},
      { params: { redeployRailway: String(redeployRailway) } }
    );
  }

  getEnvironmentSyncStatus(projectId: string) {
    return this.http.get<{ synced: boolean } & Partial<EnvironmentSyncState>>(
      `/api/projects/${projectId}/environment/sync`
    );
  }

  removeProjectService(projectId: string, targetId: string) {
    return this.http.delete<ProjectServicesResponse>(`/api/projects/${projectId}/services/${targetId}`);
  }

  getDataServiceInfo(projectId: string, targetId: string) {
    return this.http.get<DataServiceInfo>(`/api/projects/${projectId}/services/${targetId}/data-info`);
  }

  triggerDeployment(projectId: string, branch?: string) {
    return this.http.post<TriggerDeploymentResponse>(`/api/projects/${projectId}/deployments`, { branch });
  }

  listDeployments(projectId: string, page = 1) {
    return this.http.get<{ deployments: DeploymentSummary[]; page: number; hasMore: boolean }>(
      `/api/projects/${projectId}/deployments`,
      { params: { page } }
    );
  }

  getDeployment(id: string) {
    return this.http.get<DeploymentDetail>(`/api/deployments/${id}`);
  }

  verifyDeployment(id: string, scope: DeploymentVerificationScope) {
    return this.http.post<DeploymentVerificationResult>(`/api/deployments/${id}/verify`, {}, { params: { scope } });
  }

  getDeploymentLogs(id: string, target?: string) {
    const params = target ? { target } : undefined;
    return this.http.get<{ logs: DeploymentLogLine[] }>(`/api/deployments/${id}/logs`, { params });
  }

  restoreDeployment(id: string) {
    return this.http.post<{
      deploymentId: string;
      status: string;
      targets: { providerName: string; status: string; message?: string | null }[];
    }>(`/api/deployments/${id}/restore`, {});
  }

  generateDeploymentFix(deploymentId: string, targetId: string): Observable<DeploymentFixStreamEvent> {
    return this.streamDeploymentFix(`/api/deployments/${deploymentId}/targets/${targetId}/fix`, {});
  }

  generateVerificationFix(
    deploymentId: string,
    checkId: string,
    targetId?: string
  ): Observable<DeploymentFixStreamEvent> {
    return this.streamDeploymentFix(`/api/deployments/${deploymentId}/verification-fix`, {
      checkId,
      targetId: targetId ?? null
    });
  }

  private streamDeploymentFix(url: string, body: unknown): Observable<DeploymentFixStreamEvent> {
    return new Observable<DeploymentFixStreamEvent>((subscriber) => {
      const abortController = new AbortController();
      const timeoutId = window.setTimeout(() => abortController.abort(), ApiService.claudeAgentTimeoutMs);

      const run = async (): Promise<void> => {
        try {
          const token = localStorage.getItem('deployai_access_token');
          const response = await fetch(url, {
            method: 'POST',
            headers: {
              'Content-Type': 'application/json',
              ...(token ? { Authorization: `Bearer ${token}` } : {})
            },
            body: JSON.stringify(body),
            signal: abortController.signal
          });

          const contentType = response.headers.get('content-type') ?? '';
          if (!response.ok && contentType.includes('application/json')) {
            const parsed = (await response.json()) as { error?: { code?: string; message?: string } };
            subscriber.next({
              type: 'error',
              code: parsed.error?.code ?? 'fix_generation_failed',
              message: parsed.error?.message ?? 'Could not generate a fix with Claude.'
            });
            subscriber.complete();
            return;
          }

          if (!response.body) {
            subscriber.error(new Error('No response body from fix stream.'));
            return;
          }

          const reader = response.body.getReader();
          const decoder = new TextDecoder();
          let buffer = '';

          while (true) {
            const { done, value } = await reader.read();
            if (done) {
              break;
            }

            buffer += decoder.decode(value, { stream: true });
            const lines = buffer.split('\n');
            buffer = lines.pop() ?? '';

            for (const line of lines) {
              const trimmed = line.trim();
              if (!trimmed) {
                continue;
              }

              const event = JSON.parse(trimmed) as DeploymentFixStreamEvent;
              subscriber.next(event);

              if (event.type === 'complete' || event.type === 'error') {
                subscriber.complete();
                return;
              }
            }
          }

          subscriber.complete();
        } catch (err) {
          if ((err as Error).name === 'AbortError') {
            subscriber.next({
              type: 'error',
              code: 'claude_request_timeout',
              message: 'Fix generation timed out. Try again — large repositories can take several minutes.'
            });
            subscriber.complete();
            return;
          }

          subscriber.error(err);
        } finally {
          window.clearTimeout(timeoutId);
        }
      };

      void run();
      return () => {
        window.clearTimeout(timeoutId);
        abortController.abort();
      };
    });
  }

  mergeDeploymentFix(owner: string, repo: string, pullRequestNumber: number) {
    return this.http.post<{ merged: boolean }>(
      `/api/github/repos/${owner}/${repo}/deployment-fix/merge`,
      { pullRequestNumber }
    );
  }

  getProviderHealth() {
    return this.http.get<{ providers: { name: string; status: string; message?: string | null }[] }>(
      '/api/health/providers'
    );
  }

  getVercelLoginUrl(returnUrl?: string) {
    const params = returnUrl ? { returnUrl } : undefined;
    return this.http.get<{ url: string }>('/api/auth/vercel/login-url', { params });
  }

  getRailwayLoginUrl(returnUrl?: string) {
    const params = returnUrl ? { returnUrl } : undefined;
    return this.http.get<{ url: string }>('/api/auth/railway/login-url', { params });
  }

  createVercelProject(
    credentialId: string,
    name: string,
    githubRepoFullName: string,
    profile?: FrontendBuildProfile
  ) {
    return this.http.post<{ project: ProviderProject }>('/api/credentials/vercel/projects', {
      credentialId,
      name,
      githubRepoFullName,
      rootDirectory: profile?.rootDirectory,
      outputDirectory: profile?.outputDirectory,
      buildCommand: profile?.buildCommand,
      installCommand: profile?.installCommand,
      framework: profile?.framework
    });
  }

  createRailwayProject(
    credentialId: string,
    name: string,
    githubRepoFullName: string,
    profile?: ServerBuildProfile
  ) {
    return this.http.post<{ project: ProviderProject }>('/api/credentials/railway/projects', {
      credentialId,
      name,
      githubRepoFullName,
      rootDirectory: profile?.rootDirectory,
      buildCommand: profile?.buildCommand,
      installCommand: profile?.installCommand,
      framework: profile?.framework,
      dockerfilePath: profile?.dockerfilePath,
      serviceDirectory: profile?.serviceDirectory,
      startCommand: profile?.startCommand
    });
  }

  listCoolifyInfrastructure(credentialId: string) {
    return this.http.get<CoolifyInfrastructure>(`/api/credentials/${credentialId}/coolify/infrastructure`);
  }

  listCoolifyProjectEnvironments(credentialId: string, projectUuid: string) {
    return this.http.get<{ environments: CoolifyInfrastructureResource[] }>(
      `/api/credentials/${credentialId}/coolify/projects/${projectUuid}/environments`
    );
  }

  createCoolifyProject(
    credentialId: string,
    name: string,
    githubRepoFullName: string,
    gitBranch: string,
    options?: {
      isPrivateRepository?: boolean;
      coolifyProjectUuid?: string;
      coolifyServerUuid?: string;
      coolifyEnvironmentName?: string;
      coolifyGithubAppUuid?: string;
      buildPack?: CoolifyBuildPack;
      rootDirectory?: string;
      outputDirectory?: string;
      buildCommand?: string;
      installCommand?: string;
      startCommand?: string;
      framework?: string;
      dockerfilePath?: string;
      serviceDirectory?: string;
      /** Path to the compose file; its presence is what selects the compose build pack. */
      composeFileLocation?: string;
      customDomain?: string;
      /** Compose service the domain attaches to — the rest stay internal. */
      domainServiceName?: string;
    }
  ) {
    return this.http.post<{ project: ProviderProject }>('/api/credentials/coolify/projects', {
      credentialId,
      name,
      githubRepoFullName,
      gitBranch,
      isPrivateRepository: options?.isPrivateRepository ?? false,
      coolifyProjectUuid: options?.coolifyProjectUuid,
      coolifyServerUuid: options?.coolifyServerUuid,
      coolifyEnvironmentName: options?.coolifyEnvironmentName,
      coolifyGithubAppUuid: options?.coolifyGithubAppUuid,
      buildPack: options?.buildPack,
      rootDirectory: options?.rootDirectory,
      outputDirectory: options?.outputDirectory,
      buildCommand: options?.buildCommand,
      installCommand: options?.installCommand,
      startCommand: options?.startCommand,
      framework: options?.framework,
      dockerfilePath: options?.dockerfilePath,
      serviceDirectory: options?.serviceDirectory,
      composeFileLocation: options?.composeFileLocation,
      customDomain: options?.customDomain,
      domainServiceName: options?.domainServiceName
    });
  }

  listProjectEnvVars(credentialId: string, projectId: string) {
    return this.http.get<{ envVars: ProviderEnvVar[] }>(
      `/api/credentials/${credentialId}/projects/${encodeURIComponent(projectId)}/env`
    );
  }

  upsertProjectEnvVar(
    credentialId: string,
    projectId: string,
    payload: { key: string; value: string; type?: string; targets?: string[] }
  ) {
    return this.http.post<{ envVar: ProviderEnvVar }>(
      `/api/credentials/${credentialId}/projects/${encodeURIComponent(projectId)}/env`,
      payload
    );
  }

  deleteProjectEnvVar(credentialId: string, projectId: string, envVarId: string) {
    return this.http.delete(
      `/api/credentials/${credentialId}/projects/${encodeURIComponent(projectId)}/env/${envVarId}`
    );
  }

  // Object storage lives under /api/storage rather than /api/credentials: a bucket is not a
  // deploy target, and the two are kept apart so storage can never reach a deploy-target picker.
  listStorageConnections() {
    return this.http.get<{ connections: StorageConnectionSummary[] }>('/api/storage/connections');
  }

  createStorageConnection(payload: {
    providerName?: string;
    endpoint: string;
    region?: string;
    accessKey: string;
    secretKey: string;
    label?: string;
  }) {
    return this.http.post<StorageConnectionSummary>('/api/storage/connections', payload);
  }

  deleteStorageConnection(id: string) {
    return this.http.delete(`/api/storage/connections/${id}`);
  }

  listStorageBuckets(id: string) {
    return this.http.get<{ buckets: StorageBucket[] }>(`/api/storage/connections/${id}/buckets`);
  }

  /** Detected env vars for a repo, with server-side suggestions (generated secrets, storage keys). */
  getEnvSchema(owner: string, repo: string, gitRef: string, serverPath?: string) {
    const params: Record<string, string> = { ref: gitRef };
    if (serverPath) {
      params['serverPath'] = serverPath;
    }
    return this.http.get<{ vars: EnvSchemaVar[] }>(
      `/api/github/repos/${owner}/${repo}/env-schema`,
      { params }
    );
  }

  /** Persists env vars (encrypted) and pushes them onto the project's Coolify app. */
  setComposeEnvironment(projectId: string, variables: { key: string; value: string; isSecret: boolean }[]) {
    return this.http.post<{ applied: string[] }>(
      `/api/projects/${projectId}/environment/compose`,
      { variables }
    );
  }

  /** The env vars DeployAI manages for this app — for viewing and editing after deploy. */
  getEnvironment(projectId: string) {
    return this.http.get<{ variables: EnvVariable[] }>(
      `/api/projects/${projectId}/environment`
    );
  }

  /**
   * Configuration a target's container said it was missing, read from its own startup output.
   *
   * `readable: false` means the logs could not be read at all — a stopped container is the usual
   * cause, and it is exactly the state this is for — so it must not be shown as "nothing missing".
   */
  getMissingConfiguration(projectId: string, targetId: string) {
    return this.http.get<{
      missing: MissingConfigurationItem[];
      readable: boolean;
      reason?: string;
      message?: string;
    }>(`/api/projects/${projectId}/environment/missing`, { params: { targetId } });
  }

  /** Removes a single env var from the live app and DeployAI's store. */
  deleteEnvironmentVariable(projectId: string, key: string) {
    return this.http.delete<{ deleted: string }>(
      `/api/projects/${projectId}/environment/${encodeURIComponent(key)}`
    );
  }

  /**
   * Copies a storage connection out of an app that already has one. Hetzner only issues S3
   * credentials through their Console, so this is as close to automatic as the connection gets.
   */
  importStorageConnection(payload: { credentialId: string; providerProjectId: string; label?: string }) {
    return this.http.post<{ connection: StorageConnectionSummary; bucket: string }>(
      '/api/storage/connections/import',
      payload
    );
  }

  createStorageBucket(id: string, name: string) {
    return this.http.post<{ bucket: StorageBucket }>(
      `/api/storage/connections/${id}/buckets`,
      { name }
    );
  }
}
