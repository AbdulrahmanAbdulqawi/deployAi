import { Component, DestroyRef, OnDestroy, OnInit, effect, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { DeploymentStore } from '../core/stores/deployment.store';
import { ApiService } from '../core/services/api.service';
import { roleLabelForProvider } from '../core/utils/target-config';
import { ActivityLine } from '../shared/live-log-panel/live-log-panel.component';
import { EnvironmentSyncState, ProjectTarget } from '../core/models/api.models';
import { DomainPanelComponent } from '../project/domains/domain-panel.component';
import { StatusBadgeComponent } from '../shared/status-badge/status-badge.component';
import { ProviderStatusCardComponent } from '../shared/provider-status-card/provider-status-card.component';
import { IconComponent } from '../shared/ui/icon/icon.component';
import { ButtonComponent } from '../shared/ui/button/button.component';
import { LiveLogPanelComponent } from '../shared/live-log-panel/live-log-panel.component';
import { DeploymentFixPanelComponent } from '../shared/deployment-fix-panel/deployment-fix-panel.component';
import { OperationProgressComponent } from '../shared/operation-progress/operation-progress.component';
import { ToastService } from '../shared/ui/toast/toast.service';
import { ElapsedTimer, ElapsedTimerService } from '../core/services/elapsed-timer.service';
import { durationMsFromTimestamps, formatDurationMs, formatDurationSeconds } from '../core/utils/duration';
import { canSyncEnvironmentUrls } from '../core/utils/environment-sync-eligibility';
import type { OperationProgressMode } from '../shared/operation-progress/operation-progress.component';

/**
 * The browser-facing part is where a domain belongs. A compose app deploys as one website-role
 * target; a split-origin plan has the site and the API as separate targets and only the site gets
 * the domain here. The role lives inside the target's config blob rather than on the target.
 */
function pickDomainTargetId(targets: ProjectTarget[]): string | null {
  const website = targets.find((target) => {
    try {
      return target.config ? JSON.parse(target.config)?.role === 'website' : false;
    } catch {
      return false;
    }
  });

  return website?.id ?? targets[0]?.id ?? null;
}

@Component({
  selector: 'app-publish-view',
  standalone: true,
  imports: [
    DatePipe,
    StatusBadgeComponent,
    ProviderStatusCardComponent,
    IconComponent,
    ButtonComponent,
    LiveLogPanelComponent,
    DeploymentFixPanelComponent,
    OperationProgressComponent,
    DomainPanelComponent
  ],  templateUrl: './publish-view.component.html',
  styleUrl: './publish-view.component.scss'
})
export class PublishViewComponent implements OnInit, OnDestroy {
  readonly showDetails = signal(true);
  readonly expandedTargets = signal<Record<string, boolean>>({});
  readonly redeployingTargets = signal<Record<string, boolean>>({});
  readonly syncingUrls = signal(false);
  readonly environmentSync = signal<EnvironmentSyncState | null>(null);
  readonly githubRepoFullName = signal<string | null>(null);
  readonly projectName = signal<string | null>(null);
  readonly copiedUrl = signal<string | null>(null);
  /** The part of the project a domain attaches to — the one facing the browser. */
  readonly domainTargetId = signal<string | null>(null);

  private projectLoadedFor: string | null = null;
  private copiedTimer?: ReturnType<typeof setTimeout>;
  private readonly deployTimer: ElapsedTimer;
  private readonly destroyRef = inject(DestroyRef);

  constructor(
    readonly store: DeploymentStore,
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly api: ApiService,
    private readonly toast: ToastService,
    elapsedTimerService: ElapsedTimerService
  ) {
    this.deployTimer = elapsedTimerService.create();
    this.destroyRef.onDestroy(() => this.deployTimer.destroy());

    effect(() => {
      const deployment = this.store.deployment();
      if (!deployment) {
        this.deployTimer.reset();
        return;
      }

      this.loadProjectRepo(deployment.projectId);

      if (this.store.isComplete()) {
        this.deployTimer.stop();
      } else if (deployment.startedAt) {
        this.deployTimer.start(deployment.startedAt);
      } else {
        this.deployTimer.start();
      }

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

  goBack(): void {
    const projectId = this.store.deployment()?.projectId;
    if (projectId) {
      void this.router.navigate(['/projects', projectId]);
      return;
    }
    void this.router.navigate(['/dashboard']);
  }

  openTroubleshoot(): void {
    const deployment = this.store.deployment();
    if (!deployment) {
      return;
    }

    void this.router.navigate(
      ['/projects', deployment.projectId, 'troubleshoot'],
      { queryParams: { deploymentId: deployment.id } }
    );
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
      return 'Preparing deployment…';
    }

    if (this.store.isComplete()) {
      if (deployment.status === 'success') {
        return 'Deployment complete';
      }
      if (deployment.status === 'partial') {
        return 'Partially deployed';
      }
      return 'Deployment incomplete';
    }

    // Keyed off role, with provider only as a fallback. Matching purely on 'railway'/'vercel'
    // meant a Coolify deployment matched neither and sat on "Preparing deployment…" for its
    // whole run, however far along it actually was.
    const apiTarget = deployment.targets.find(t => t.role?.toLowerCase() === 'server')
      ?? deployment.targets.find(t => t.providerName === 'railway');
    const siteTarget = deployment.targets.find(t => t.role?.toLowerCase() === 'website')
      ?? deployment.targets.find(t => t.providerName === 'vercel');

    if (apiTarget && apiTarget.status !== 'success' && apiTarget.status !== 'failed') {
      return 'Deploying your API…';
    }

    if (siteTarget && siteTarget.status !== 'success' && siteTarget.status !== 'failed') {
      return 'Deploying your site…';
    }

    // Anything still running that we could not label by role — a publish-only app, say —
    // is better described as in progress than as still preparing.
    const running = deployment.targets.find(
      t => t.status !== 'success' && t.status !== 'failed' && t.status !== 'pending');
    if (running) {
      return 'Publishing…';
    }

    return 'Preparing deployment…';
  }

  deployProgressMode(): OperationProgressMode {
    const progress = this.store.deployProgress();
    if (!progress) {
      return 'indeterminate';
    }
    return progress.mode;
  }

  deployProgressPercent(): number {
    return this.store.deployProgress()?.percent ?? 0;
  }

  deployProgressLabel(): string {
    return this.store.deployProgress()?.label ?? this.phaseHeading();
  }

  deployElapsedMs(): number {
    return this.deployTimer.elapsedMs();
  }

  durationLabel(): string | null {
    const deployment = this.store.deployment();
    if (!deployment) {
      return null;
    }

    if (deployment.durationSeconds !== undefined && deployment.durationSeconds !== null) {
      return formatDurationSeconds(deployment.durationSeconds);
    }

    const ms = durationMsFromTimestamps(deployment.startedAt, deployment.completedAt);
    return ms === null ? null : formatDurationMs(ms);
  }

  finishedAt(): string | null {    return this.store.deployment()?.completedAt ?? null;
  }

  liveServices(): { label: string; url: string; status: string; providerName: string }[] {
    const deployment = this.store.deployment();
    if (!deployment) {
      return [];
    }

    return deployment.targets
      .filter(t => t.deployUrl)
      .map(t => ({
        label: this.roleLabel(t.providerName),
        url: t.deployUrl!,
        status: t.status,
        providerName: t.providerName
      }));
  }

  providerLabelMap(): Record<string, string> {
    return {
      vercel: this.roleLabel('vercel'),
      railway: this.roleLabel('railway')
    };
  }

  displayUrl(url: string): string {
    return url.replace(/^https?:\/\//i, '').replace(/\/$/, '');
  }

  async copyUrl(url: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(url);
      this.copiedUrl.set(url);
      clearTimeout(this.copiedTimer);
      this.copiedTimer = setTimeout(() => this.copiedUrl.set(null), 2000);
      this.toast.success('Link copied');
    } catch {
      this.toast.error('Could not copy link');
    }
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
    this.api.redeployDeployTarget(deployment.projectId, target.deployTargetId, deployment.branch).subscribe({
      next: (response) => {
        this.redeployingTargets.update(state => ({ ...state, [target.id]: false }));
        void this.router.navigate(['/projects', deployment.projectId, 'deploy', response.deploymentId]);
      },
      error: (err) => {
        this.redeployingTargets.update(state => ({ ...state, [target.id]: false }));
        this.toast.error(err?.error?.error?.message ?? 'Could not redeploy that service.');
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
    return canSyncEnvironmentUrls(this.store.deployment(), this.store.isComplete());
  }

  syncUrls(): void {
    const deployment = this.store.deployment();
    if (!deployment) {
      return;
    }

    this.syncingUrls.set(true);
    this.api.syncEnvironmentUrls(deployment.projectId).subscribe({
      next: (result) => {
        this.syncingUrls.set(false);
        if (result.skipped) {
          this.toast.show(result.skipReason ?? 'Reconnection was skipped.', 'info');
          return;
        }

        this.toast.success('Your site and app are reconnected.');
        void this.loadEnvironmentSync(deployment.projectId);
      },
      error: (err) => {
        this.syncingUrls.set(false);
        this.toast.error(err?.error?.error?.message ?? 'Could not reconnect your site and app.');
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
        this.domainTargetId.set(pickDomainTargetId(project.targets ?? []));
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
