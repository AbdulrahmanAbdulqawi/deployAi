import { Injectable, computed, inject, signal } from '@angular/core';
import { ApiService } from '../../core/services/api.service';
import { AiSetupPreferenceService } from '../../core/services/ai-setup-preference.service';
import { ProjectsStore } from '../../core/stores/projects.store';
import { ToastService } from '../../shared/ui/toast/toast.service';
import {
  DeploymentReadinessResult,
  DeploymentSummary,
  ProjectDetail,
  ProjectServicesResponse,
  ProviderName
} from '../../core/models/api.models';
import { parseTargetConfig } from '../../core/utils/target-config';

export interface LiveUrl {
  label: string;
  url: string;
}

/**
 * Loads a project's shared data once per workspace visit and reloads only when the route's
 * project id actually changes, instead of every tab independently calling getProject(). Scoped
 * (not providedIn: 'root') so each ProjectWorkspaceComponent instance gets its own state.
 */
@Injectable()
export class ProjectWorkspaceContext {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  private readonly aiSetup = inject(AiSetupPreferenceService);
  private readonly projectsStore = inject(ProjectsStore);

  readonly projectId = signal<string | null>(null);
  readonly project = signal<ProjectDetail | null>(null);
  readonly services = signal<ProjectServicesResponse | null>(null);
  readonly latestDeployment = signal<DeploymentSummary | null>(null);
  readonly deploymentReadiness = signal<DeploymentReadinessResult | null>(null);
  readonly loadingDeploymentReadiness = signal(false);
  readonly loading = signal(false);

  readonly overallStatus = computed(() => this.latestDeployment()?.status ?? 'idle');
  readonly hasSplitOrigin = computed(() => this.deploymentReadiness()?.usesSplitOrigin ?? false);
  readonly aiSetupEnabled = computed(() => this.aiSetup.enabled());
  readonly publishing = computed(() => this.projectsStore.deployingProjectId() === this.projectId());

  readonly lastDeployedLabel = computed<string | null>(() => {
    const deployment = this.latestDeployment();
    const when = deployment?.completedAt ?? deployment?.startedAt;
    return when ? this.relativeTime(when) : null;
  });

  readonly coolifyBranchMismatch = computed<string | null>(() => {
    const project = this.project();
    if (!project) {
      return null;
    }

    for (const target of project.targets) {
      if (target.providerName !== ProviderName.Coolify) {
        continue;
      }
      const config = parseTargetConfig(target.config);
      const coolifyBranch = config.coolifyGitBranch;
      if (coolifyBranch && coolifyBranch !== project.defaultBranch) {
        return `Coolify rebuilds from branch "${coolifyBranch}", but this app uses "${project.defaultBranch}".`;
      }
    }
    return null;
  });

  // Deployment-target URLs only — the Settings tab additionally polls live per-service status
  // (getProjectServiceStatus) and can surface a service's URL even without a deployment record;
  // that fallback stays local to the Settings tab rather than duplicating polling here.
  readonly liveUrls = computed<LiveUrl[]>(() => {
    const urls: LiveUrl[] = [];
    const sync = this.project()?.environmentSync;
    const deployment = this.latestDeployment();

    const byRole = (role: string) =>
      deployment?.targets.find((t) => t.role?.toLowerCase() === role)?.deployUrl;

    const websiteUrl =
      sync?.resolvedWebsiteUrl ||
      byRole('website') ||
      deployment?.targets.find((t) => t.providerName === ProviderName.Vercel)?.deployUrl;
    if (websiteUrl) {
      urls.push({ label: 'Website', url: websiteUrl });
    }

    const apiUrl =
      sync?.resolvedApiUrl ||
      byRole('server') ||
      deployment?.targets.find((t) => t.providerName === ProviderName.Railway)?.deployUrl;
    if (apiUrl && apiUrl !== websiteUrl) {
      urls.push({ label: 'API', url: apiUrl });
    }

    if (urls.length === 0) {
      const coolifyUrl = deployment?.targets.find((t) => t.providerName === ProviderName.Coolify)?.deployUrl;
      if (coolifyUrl) {
        urls.push({ label: 'Live site', url: coolifyUrl });
      }
    }

    return urls;
  });

  /** No-op when already loaded for this project id — callers can call this on every tab activation. */
  load(projectId: string): void {
    if (this.projectId() === projectId && this.project()) {
      return;
    }
    this.projectId.set(projectId);
    this.project.set(null);
    this.services.set(null);
    this.latestDeployment.set(null);
    this.deploymentReadiness.set(null);
    this.loading.set(true);

    this.aiSetup.hydrateForProject(projectId);

    this.api.getProject(projectId).subscribe({
      next: (project) => {
        this.project.set(project);
        this.loading.set(false);
        if (this.aiSetup.enabled()) {
          this.loadDeploymentReadiness(projectId, project.defaultBranch);
        }
      },
      error: (err) => {
        this.loading.set(false);
        this.toast.error(err?.error?.error?.message ?? 'Could not load that app.');
      }
    });

    this.api.getProjectServices(projectId).subscribe({
      next: (response) => this.services.set(response),
      error: (err) => this.toast.error(err?.error?.error?.message ?? 'Could not load services.')
    });

    this.api.listDeployments(projectId).subscribe({
      next: (response) => this.latestDeployment.set(response.deployments[0] ?? null),
      error: () => this.latestDeployment.set(null)
    });
  }

  reload(): void {
    const id = this.projectId();
    if (!id) {
      return;
    }
    this.projectId.set(null);
    this.load(id);
  }

  publish(): void {
    const projectId = this.projectId();
    if (!projectId) {
      return;
    }
    const readiness = this.deploymentReadiness();
    if (this.aiSetupEnabled() && readiness?.usesSplitOrigin && !readiness.isReady) {
      this.toast.error('Generate split-origin deployment files before publishing.');
      return;
    }
    this.projectsStore.triggerDeploy(projectId);
  }

  private relativeTime(value: string): string {
    const then = new Date(value).getTime();
    const diffMs = Date.now() - then;
    const seconds = Math.round(diffMs / 1000);
    if (seconds < 60) {
      return 'just now';
    }
    const minutes = Math.round(seconds / 60);
    if (minutes < 60) {
      return `${minutes}m ago`;
    }
    const hours = Math.round(minutes / 60);
    if (hours < 24) {
      return `${hours}h ago`;
    }
    const days = Math.round(hours / 24);
    if (days < 30) {
      return `${days}d ago`;
    }
    return new Date(value).toLocaleDateString();
  }

  private loadDeploymentReadiness(projectId: string, defaultBranch: string): void {
    this.loadingDeploymentReadiness.set(true);
    this.api.getProjectDeploymentReadiness(projectId, defaultBranch).subscribe({
      next: (result) => {
        this.deploymentReadiness.set(result);
        this.loadingDeploymentReadiness.set(false);
      },
      error: () => {
        this.deploymentReadiness.set(null);
        this.loadingDeploymentReadiness.set(false);
      }
    });
  }
}
