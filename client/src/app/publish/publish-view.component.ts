import { Component, OnDestroy, OnInit, effect, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { DeploymentStore } from '../core/stores/deployment.store';
import { ApiService } from '../core/services/api.service';
import { roleLabelForProvider } from '../core/utils/target-config';
import { ActivityLine } from '../shared/live-log-panel/live-log-panel.component';
import { EnvironmentSyncState } from '../core/models/api.models';
import { StatusBadgeComponent } from '../shared/status-badge/status-badge.component';
import { ProviderStatusCardComponent } from '../shared/provider-status-card/provider-status-card.component';
import { IconComponent } from '../shared/ui/icon/icon.component';
import { ButtonComponent } from '../shared/ui/button/button.component';
import { LiveLogPanelComponent } from '../shared/live-log-panel/live-log-panel.component';
import { DeploymentFixPanelComponent } from '../shared/deployment-fix-panel/deployment-fix-panel.component';

@Component({
  selector: 'app-publish-view',
  standalone: true,
  imports: [DatePipe, StatusBadgeComponent, ProviderStatusCardComponent, IconComponent, ButtonComponent, LiveLogPanelComponent, DeploymentFixPanelComponent],
  templateUrl: './publish-view.component.html',
  styleUrl: './publish-view.component.scss'
})
export class PublishViewComponent implements OnInit, OnDestroy {
  readonly showDetails = signal(true);
  readonly expandedTargets = signal<Record<string, boolean>>({});
  readonly redeployingTargets = signal<Record<string, boolean>>({});
  readonly redeployMessage = signal<string | null>(null);
  readonly syncingUrls = signal(false);
  readonly syncMessage = signal<string | null>(null);
  readonly environmentSync = signal<EnvironmentSyncState | null>(null);
  readonly githubRepoFullName = signal<string | null>(null);
  readonly projectName = signal<string | null>(null);

  private projectLoadedFor: string | null = null;

  constructor(
    readonly store: DeploymentStore,
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly api: ApiService
  ) {
    effect(() => {
      const deployment = this.store.deployment();
      if (!deployment) {
        return;
      }

      this.loadProjectRepo(deployment.projectId);

      if (!this.store.isComplete()) {
        return;
      }

      const nextExpanded = { ...this.expandedTargets() };
      let changed = false;
      for (const target of deployment.targets) {
        if (target.status === 'failed' && !nextExpanded[target.id]) {
          nextExpanded[target.id] = true;
          changed = true;
        }
      }

      if (changed) {
        this.expandedTargets.set(nextExpanded);
      }

      void this.loadEnvironmentSync(deployment.projectId);
    }, { allowSignalWrites: true });
  }

  ngOnInit(): void {
    const deploymentId = this.route.snapshot.paramMap.get('deploymentId')
      ?? this.route.snapshot.paramMap.get('id')
      ?? '';
    void this.store.load(deploymentId);
  }

  async ngOnDestroy(): Promise<void> {
    await this.store.unload();
  }

  roleLabel(providerName: string): string {
    const name = this.projectName();
    if (name) {
      if (providerName === 'railway') {
        return `${name} API`;
      }
      if (providerName === 'vercel') {
        return `${name} UI`;
      }
    }
    return roleLabelForProvider(providerName);
  }

  phaseHeading(): string {
    const deployment = this.store.deployment();
    if (!deployment) {
      return 'Getting ready…';
    }

    if (this.store.isComplete()) {
      if (deployment.status === 'success') {
        return 'Deployment complete';
      }
      if (deployment.status === 'partial') {
        return 'Partially deployed';
      }
      return "Deployment didn't finish";
    }

    const apiTarget = deployment.targets.find(t => t.providerName === 'railway');
    const siteTarget = deployment.targets.find(t => t.providerName === 'vercel');

    if (apiTarget && apiTarget.status !== 'success' && apiTarget.status !== 'failed') {
      return 'Setting up your API…';
    }

    if (siteTarget && siteTarget.status !== 'success' && siteTarget.status !== 'failed') {
      return 'Connecting your site…';
    }

    return 'Getting ready…';
  }

  elapsedLabel(): string {
    const deployment = this.store.deployment();
    if (!deployment?.startedAt) {
      return '00:00';
    }

    const elapsedMs = Date.now() - new Date(deployment.startedAt).getTime();
    const totalSeconds = Math.max(0, Math.floor(elapsedMs / 1000));
    const minutes = Math.floor(totalSeconds / 60);
    const seconds = totalSeconds % 60;
    return `${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`;
  }

  liveLinks(): { label: string; url: string }[] {
    const deployment = this.store.deployment();
    if (!deployment) {
      return [];
    }

    return deployment.targets
      .filter(t => t.deployUrl)
      .map(t => ({
        label: this.roleLabel(t.providerName),
        url: t.deployUrl!
      }));
  }

  toggleDetails(): void {
    this.showDetails.update(open => !open);
  }

  isExpanded(targetId: string): boolean {
    return this.expandedTargets()[targetId] ?? true;
  }

  setTargetExpanded(targetId: string, expanded: boolean): void {
    this.expandedTargets.update(state => ({
      ...state,
      [targetId]: expanded
    }));
  }

  linesForProvider(providerName: string): ActivityLine[] {
    return this.store.activity().filter(line => line.providerName === providerName);
  }

  canRedeployTarget(target: { status: string; deployTargetId?: string }): boolean {
    return target.status === 'failed' && !!target.deployTargetId && this.store.isComplete();
  }

  redeployTarget(target: {
    id: string;
    deployTargetId: string;
    providerName: string;
  }): void {
    const deployment = this.store.deployment();
    if (!deployment) {
      return;
    }

    this.redeployingTargets.update(state => ({ ...state, [target.id]: true }));
    this.redeployMessage.set(null);
    this.api.redeployDeployTarget(deployment.projectId, target.deployTargetId, deployment.branch).subscribe({
      next: (response) => {
        this.redeployingTargets.update(state => ({ ...state, [target.id]: false }));
        void this.router.navigate(['/projects', deployment.projectId, 'deploy', response.deploymentId]);
      },
      error: (err) => {
        this.redeployingTargets.update(state => ({ ...state, [target.id]: false }));
        this.redeployMessage.set(err?.error?.error?.message ?? 'Could not redeploy that service.');
      }
    });
  }

  isRedeploying(targetId: string): boolean {
    return this.redeployingTargets()[targetId] ?? false;
  }

  repoUrl(): string | null {
    const fullName = this.githubRepoFullName();
    return fullName ? `https://github.com/${fullName}` : null;
  }

  commitUrl(): string | null {
    const fullName = this.githubRepoFullName();
    const sha = this.store.deployment()?.gitCommitSha;
    return fullName && sha ? `https://github.com/${fullName}/commit/${sha}` : null;
  }

  repoOwner(): string | null {
    const fullName = this.githubRepoFullName();
    if (!fullName) return null;
    return fullName.split('/')[0] ?? null;
  }

  repoName(): string | null {
    const fullName = this.githubRepoFullName();
    if (!fullName) return null;
    return fullName.split('/')[1] ?? null;
  }

  canShowFixPanel(target: {
    status: string;
    failureAnalysis?: { canRequestClaudeFix: boolean } | null;
  }): boolean {
    return target.status === 'failed' &&
      !!target.failureAnalysis?.canRequestClaudeFix &&
      this.store.isComplete();
  }

  canSyncUrls(): boolean {
    const deployment = this.store.deployment();
    if (!deployment || !this.store.isComplete()) {
      return false;
    }

    const hasRailway = deployment.targets.some(t => t.providerName === 'railway' && t.status === 'success');
    const hasVercel = deployment.targets.some(t => t.providerName === 'vercel' && t.status === 'success');
    return hasRailway && hasVercel;
  }

  syncUrls(): void {
    const deployment = this.store.deployment();
    if (!deployment) {
      return;
    }

    this.syncingUrls.set(true);
    this.syncMessage.set(null);
    this.api.syncEnvironmentUrls(deployment.projectId).subscribe({
      next: (result) => {
        this.syncingUrls.set(false);
        if (result.skipped) {
          this.syncMessage.set(result.skipReason ?? 'URL sync was skipped.');
          return;
        }

        const summary = result.verificationMessages.length > 0
          ? result.verificationMessages.join(' ')
          : 'Railway and Vercel URLs are aligned.';
        this.syncMessage.set(summary);
        void this.loadEnvironmentSync(deployment.projectId);
      },
      error: (err) => {
        this.syncingUrls.set(false);
        this.syncMessage.set(err?.error?.error?.message ?? 'Could not sync URLs.');
      }
    });
  }

  private loadProjectRepo(projectId: string): void {
    if (this.projectLoadedFor === projectId) {
      return;
    }
    this.projectLoadedFor = projectId;
    this.api.getProject(projectId).subscribe({
      next: (project) => {
        this.githubRepoFullName.set(project.githubRepoFullName);
        this.projectName.set(project.name);
      },
      error: () => {
        this.projectLoadedFor = null;
      }
    });
  }

  private loadEnvironmentSync(projectId: string): void {
    this.api.getEnvironmentSyncStatus(projectId).subscribe({
      next: (status) => {
        if (!status.synced || !status.lastSyncedAt) {
          this.environmentSync.set(null);
          return;
        }

        this.environmentSync.set({
          lastSyncedAt: status.lastSyncedAt,
          source: status.source ?? 'manual',
          success: status.success ?? false,
          driftDetected: status.driftDetected ?? false,
          resolvedWebsiteUrl: status.resolvedWebsiteUrl,
          resolvedApiUrl: status.resolvedApiUrl,
          verificationMessages: status.verificationMessages ?? [],
          driftDetails: status.driftDetails ?? []
        });
      }
    });
  }
}
