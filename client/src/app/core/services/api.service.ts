import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import {
  CredentialSummary,
  DatabaseRequirementProfile,
  DeploymentDetail,
  DeploymentLogLine,
  DeploymentPlan,
  DeploymentSummary,
  GitHubBranch,
  GitHubContentDirectory,
  FrontendBuildProfile,
  ServerBuildProfile,
  GitHubRepo,
  ProjectDetail,
  ProjectServicesResponse,
  ProjectServiceStatus,
  DataServiceInfo,
  ProjectSummary,
  ProviderInfo,
  ProviderProject,
  ProviderEnvVar,
  TriggerDeploymentResponse
} from '../models/api.models';

@Injectable({ providedIn: 'root' })
export class ApiService {
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

  addCredential(providerName: string, token: string, label?: string) {
    return this.http.post<CredentialSummary>('/api/credentials', { providerName, token, label });
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
      targets?: { providerName: string; credentialId: string; providerProjectId: string; config?: string }[];
    }
  ) {
    return this.http.put<ProjectDetail>(`/api/projects/${id}`, payload);
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
}
