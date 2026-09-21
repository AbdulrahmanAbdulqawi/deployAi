import type { DeployAIConfig } from '../config.js';
import { DeployAIApiError } from '../errors.js';
import { TokenStore } from '../auth/token-store.js';
import { consumeNdjsonStream } from './stream.js';
import type {
  ApiErrorBody,
  CoolifyInventory,
  CredentialSummary,
  DeploymentDetail,
  DeploymentFixResult,
  DeploymentFixStreamEvent,
  DeploymentLogLine,
  DeploymentPlan,
  DeploymentPlanPart,
  DeploymentReadinessResult,
  DeploymentSetupMergeResult,
  DeploymentSetupResult,
  DeploymentSetupStreamEvent,
  DeploymentSummary,
  DeploymentVerificationResult,
  DeploymentVerificationScope,
  GitHubRepo,
  ProjectDetail,
  ProjectSummary,
  ProviderProject,
  RefreshTokenResponse,
  StreamAggregateResult,
  TriggerDeploymentResponse,
} from './types.js';

export class DeployAIApiClient {
  private accessToken: string | null;
  private refreshToken: string | null;
  private readonly tokenStore: TokenStore;

  constructor(private readonly config: DeployAIConfig) {
    this.accessToken = config.accessToken;
    this.refreshToken = config.refreshToken;
    this.tokenStore = new TokenStore(config.tokenStorePath);
  }

  async initialize(): Promise<void> {
    const stored = await this.tokenStore.load();
    if (stored?.accessToken && stored.refreshToken) {
      this.accessToken = stored.accessToken;
      this.refreshToken = stored.refreshToken;
    }
  }

  async setTokens(accessToken: string, refreshToken: string): Promise<void> {
    this.accessToken = accessToken;
    this.refreshToken = refreshToken;
    await this.tokenStore.save(accessToken, refreshToken);
  }

  hasTokens(): boolean {
    return Boolean(this.accessToken && this.refreshToken);
  }

  getApiUrl(): string {
    return this.config.apiUrl;
  }

  async health(): Promise<{ status: string; service: string }> {
    return this.request('/api/health', { auth: false });
  }

  async listProjects(): Promise<{ projects: ProjectSummary[] }> {
    return this.request('/api/projects');
  }

  async getProject(id: string): Promise<ProjectDetail> {
    return this.request(`/api/projects/${id}`);
  }

  async listDeployments(
    projectId: string,
    page = 1
  ): Promise<{ deployments: DeploymentSummary[]; page: number; hasMore: boolean }> {
    return this.request(`/api/projects/${projectId}/deployments?page=${page}`);
  }

  async getDeployment(id: string): Promise<DeploymentDetail> {
    return this.request(`/api/deployments/${id}`);
  }

  async triggerDeployment(
    projectId: string,
    branch?: string
  ): Promise<TriggerDeploymentResponse> {
    return this.request(`/api/projects/${projectId}/deployments`, {
      method: 'POST',
      body: branch ? { branch } : {},
    });
  }

  async getDeploymentLogs(
    id: string,
    target?: string
  ): Promise<{ logs: DeploymentLogLine[] }> {
    const query = target ? `?target=${encodeURIComponent(target)}` : '';
    return this.request(`/api/deployments/${id}/logs${query}`);
  }

  async verifyDeployment(
    id: string,
    scope: DeploymentVerificationScope
  ): Promise<DeploymentVerificationResult> {
    return this.request(`/api/deployments/${id}/verify?scope=${scope}`, {
      method: 'POST',
      body: {},
    });
  }

  async listGitHubRepos(
    page = 1,
    perPage = 30,
    search?: string
  ): Promise<{ repos: GitHubRepo[]; page: number; hasMore: boolean }> {
    const params = new URLSearchParams({
      page: String(page),
      perPage: String(perPage),
    });
    if (search) {
      params.set('search', search);
    }
    return this.request(`/api/github/repos?${params}`);
  }

  async getDeploymentPlan(
    owner: string,
    repo: string,
    gitRef: string
  ): Promise<DeploymentPlan> {
    return this.request(
      `/api/github/repos/${owner}/${repo}/deployment-plan?ref=${encodeURIComponent(gitRef)}`
    );
  }

  async scanDeploymentReadiness(
    owner: string,
    repo: string,
    ref: string,
    parts: DeploymentPlanPart[]
  ): Promise<DeploymentReadinessResult> {
    return this.request(`/api/github/repos/${owner}/${repo}/deployment-readiness`, {
      method: 'POST',
      body: { ref, parts },
    });
  }

  async generateDeploymentSetup(
    owner: string,
    repo: string,
    gitRef: string,
    parts: DeploymentPlanPart[],
    options?: {
      projectId?: string;
      forceRegenerate?: boolean;
      useAi?: boolean;
    }
  ): Promise<StreamAggregateResult<DeploymentSetupStreamEvent & { type: 'complete' }>> {
    return this.streamRequest(`/api/github/repos/${owner}/${repo}/deployment-setup`, {
      gitRef,
      parts,
      projectId: options?.projectId ?? null,
      forceRegenerate: options?.forceRegenerate ?? false,
      useAi: options?.useAi ?? null,
    });
  }

  async mergeDeploymentSetup(
    owner: string,
    repo: string,
    pullRequestNumber: number,
    projectId?: string
  ): Promise<DeploymentSetupMergeResult> {
    return this.request(`/api/github/repos/${owner}/${repo}/deployment-setup/merge`, {
      method: 'POST',
      body: { pullRequestNumber, projectId: projectId ?? null },
    });
  }

  async generateDeploymentFix(
    deploymentId: string,
    targetId: string
  ): Promise<StreamAggregateResult<DeploymentFixStreamEvent & { type: 'complete' }>> {
    return this.streamRequest(
      `/api/deployments/${deploymentId}/targets/${targetId}/fix`,
      {}
    );
  }

  async generateVerificationFix(
    deploymentId: string,
    checkId: string,
    targetId?: string
  ): Promise<StreamAggregateResult<DeploymentFixStreamEvent & { type: 'complete' }>> {
    return this.streamRequest(`/api/deployments/${deploymentId}/verification-fix`, {
      checkId,
      targetId: targetId ?? null,
    });
  }

  async mergeDeploymentFix(
    owner: string,
    repo: string,
    pullRequestNumber: number
  ): Promise<{ merged: boolean }> {
    return this.request(`/api/github/repos/${owner}/${repo}/deployment-fix/merge`, {
      method: 'POST',
      body: { pullRequestNumber },
    });
  }

  async listCredentials(): Promise<{ credentials: CredentialSummary[] }> {
    return this.request('/api/credentials');
  }

  /**
   * Everything a Coolify instance runs, read-only. Goes through DeployAI rather than Coolify's
   * API directly, so the token never leaves DeployAI's store and no MCP client needs one.
   */
  async getCoolifyInventory(credentialId: string): Promise<CoolifyInventory> {
    return this.request(`/api/credentials/${credentialId}/coolify/inventory`);
  }

  async listProviderProjects(
    credentialId: string
  ): Promise<{ projects: ProviderProject[] }> {
    return this.request(`/api/credentials/${credentialId}/projects`);
  }

  private async streamRequest<TComplete>(
    path: string,
    body: unknown
  ): Promise<StreamAggregateResult<TComplete & { type: 'complete' }>> {
    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), this.config.streamTimeoutMs);

    try {
      const response = await this.fetchWithAuth(path, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body),
        signal: controller.signal,
      });

      const contentType = response.headers.get('content-type') ?? '';
      if (!response.ok && contentType.includes('application/json')) {
        await this.throwApiError(response);
      }

      if (!response.body) {
        throw new DeployAIApiError('no_response_body', 'No response body from stream endpoint.');
      }

      return consumeNdjsonStream<TComplete & { type: 'complete' }>(response.body);
    } catch (error) {
      if (error instanceof Error && error.name === 'AbortError') {
        throw new DeployAIApiError(
          'claude_request_timeout',
          'AI operation timed out. Large repositories can take several minutes — try again.'
        );
      }
      throw error;
    } finally {
      clearTimeout(timeoutId);
    }
  }

  private async request<T>(
    path: string,
    options: {
      method?: string;
      body?: unknown;
      auth?: boolean;
    } = {}
  ): Promise<T> {
    const response = await this.fetchWithAuth(path, {
      method: options.method ?? 'GET',
      headers: options.body !== undefined ? { 'Content-Type': 'application/json' } : undefined,
      body: options.body !== undefined ? JSON.stringify(options.body) : undefined,
      auth: options.auth,
    });

    if (!response.ok) {
      await this.throwApiError(response);
    }

    if (response.status === 204) {
      return undefined as T;
    }

    return (await response.json()) as T;
  }

  private async fetchWithAuth(
    path: string,
    init: RequestInit & { auth?: boolean } = {}
  ): Promise<Response> {
    const useAuth = init.auth !== false;
    const headers = new Headers(init.headers);

    if (useAuth) {
      if (!this.accessToken) {
        throw new DeployAIApiError(
          'not_authenticated',
          'Not authenticated. Call mcp_auth or set DEPLOYAI_ACCESS_TOKEN and DEPLOYAI_REFRESH_TOKEN.'
        );
      }
      headers.set('Authorization', `Bearer ${this.accessToken}`);
    }

    const url = `${this.config.apiUrl}${path}`;
    let response = await fetch(url, { ...init, headers });

    if (useAuth && response.status === 401 && this.refreshToken) {
      const refreshed = await this.refreshAccessToken();
      if (refreshed) {
        headers.set('Authorization', `Bearer ${this.accessToken}`);
        response = await fetch(url, { ...init, headers });
      }
    }

    return response;
  }

  private async refreshAccessToken(): Promise<boolean> {
    if (!this.refreshToken) {
      return false;
    }

    const response = await fetch(`${this.config.apiUrl}/api/auth/refresh`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refreshToken: this.refreshToken }),
    });

    if (!response.ok) {
      return false;
    }

    const data = (await response.json()) as RefreshTokenResponse;
    await this.setTokens(data.accessToken, data.refreshToken);
    return true;
  }

  private async throwApiError(response: Response): Promise<never> {
    try {
      const body = (await response.json()) as ApiErrorBody;
      if (body.error?.code && body.error?.message) {
        throw new DeployAIApiError(body.error.code, body.error.message);
      }
    } catch (error) {
      if (error instanceof DeployAIApiError) {
        throw error;
      }
    }

    throw new DeployAIApiError(
      'api_error',
      `Request failed with status ${response.status}.`
    );
  }
}
