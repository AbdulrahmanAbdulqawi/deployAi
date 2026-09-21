import { Component, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { catchError, forkJoin, map, Observable, of, switchMap } from 'rxjs';
import { ApiService } from '../../../core/services/api.service';
import { AiSetupPreferenceService } from '../../../core/services/ai-setup-preference.service';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import {
  DeploymentDetail,
  DeploymentLogLine,
  DeploymentPlanPart,
  DeploymentReadinessResult,
  DeploymentSummary,
  ProjectDetail,
  ProviderName
} from '../../../core/models/api.models';
import { parseTargetConfig, partLabelForTarget, roleLabelForProvider } from '../../../core/utils/target-config';
import { canSyncEnvironmentUrls } from '../../../core/utils/environment-sync-eligibility';
import { IconComponent } from '../../../shared/ui/icon/icon.component';
import { DeploymentSetupPanelComponent } from '../../../shared/deployment-setup-panel/deployment-setup-panel.component';
import { DeploymentVerificationPanelComponent } from '../../../shared/deployment-verification-panel/deployment-verification-panel.component';
import { ProviderStatusCardComponent } from '../../../shared/provider-status-card/provider-status-card.component';
import { DeploymentFixPanelComponent } from '../../../shared/deployment-fix-panel/deployment-fix-panel.component';
import { ActivityLine } from '../../../shared/live-log-panel/live-log-panel.component';

@Component({
  selector: 'app-project-troubleshoot-tab',
  standalone: true,
  imports: [
    FormsModule,
    IconComponent,
    DeploymentSetupPanelComponent,
    DeploymentVerificationPanelComponent,
    ProviderStatusCardComponent,
    DeploymentFixPanelComponent
  ],
  templateUrl: './project-troubleshoot-tab.component.html',
  styleUrl: './project-troubleshoot-tab.component.scss'
})
export class ProjectTroubleshootTabComponent implements OnInit {
  readonly project = signal<ProjectDetail | null>(null);
  readonly deploymentReadiness = signal<DeploymentReadinessResult | null>(null);
  readonly pageLoading = signal(true);
  readonly loadingReadiness = signal(false);
  readonly deployment = signal<DeploymentDetail | null>(null);
  readonly deploymentLogs = signal<DeploymentLogLine[]>([]);
  readonly expandedLogTargets = signal<Record<string, boolean>>({});
  readonly loadError = signal<string | null>(null);
  readonly syncingUrls = signal(false);
  readonly redeployingTargets = signal<Record<string, boolean>>({});

  projectId = '';

  constructor(
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly api: ApiService,
    private readonly toast: ToastService,
    readonly aiSetup: AiSetupPreferenceService
  ) {}

  ngOnInit(): void {
    this.projectId = this.route.snapshot.paramMap.get('id') ?? '';
    this.loadPage();
  }

  setAiSetupEnabled(enabled: boolean): void {
    if (this.projectId) {
      this.aiSetup.setEnabledForProject(this.projectId, enabled);
    } else {
      this.aiSetup.setEnabled(enabled);
    }

    if (enabled) {
      this.loadDeploymentReadiness();
    } else {
      this.deploymentReadiness.set(null);
    }
  }

  showSetupPanel(): boolean {
    const readiness = this.deploymentReadiness();
    return this.aiSetup.enabled() && !!readiness?.usesSplitOrigin;
  }

  canShowVerificationPanel(): boolean {
    const deployment = this.deployment();
    if (!deployment) {
      return false;
    }

    return deployment.targets.some(target => target.status === 'success' && !!target.deployUrl);
  }

  roleLabel(providerName: string): string {
    const name = this.project()?.name;
    if (name) {
      if (providerName === ProviderName.Railway) {
        return `${name} API`;
      }
      if (providerName === ProviderName.Vercel) {
        return `${name} UI`;
      }
      if (providerName === ProviderName.Coolify) {
        return `${name} on Coolify`;
      }
    }

    return roleLabelForProvider(providerName);
  }

  // ---- What's wrong -------------------------------------------------------------------------
  //
  // This screen used to open with the tools — setup files, logs, health checks — and never said
  // what had actually happened. The failure analysis the API already returns was rendered on the
  // live deploy view and on the fix panel, but not here, so the page named "Troubleshoot" was the
  // one page that did not tell you what was wrong. These read that analysis and say it plainly.

  failedTargets(): DeploymentDetail['targets'] {
    return this.deployment()?.targets.filter(t => t.status === 'failed') ?? [];
  }

  workingTargets(): DeploymentDetail['targets'] {
    return this.deployment()?.targets.filter(t => t.status === 'success') ?? [];
  }

  hasTrouble(): boolean {
    return this.failedTargets().length > 0;
  }

  /** The failed target carrying an explanation, which is the one worth leading with. */
  analysedFailure(): DeploymentDetail['targets'][number] | null {
    return this.failedTargets().find(t => !!t.failureAnalysis) ?? this.failedTargets()[0] ?? null;
  }

  /**
   * A sentence naming what broke, using the part's role. Deliberately not "Deployment failed": the
   * user's question is which part of their app stopped working, and a website can be fine while
   * the server is down.
   */
  troubleHeadline(): string {
    const failed = this.failedTargets();
    if (failed.length === 0) {
      return 'Everything is working';
    }

    if (failed.length === 1) {
      return `Your ${this.partLabel(failed[0]).toLowerCase()} didn't start`;
    }

    return `${failed.length} parts of your app didn't start`;
  }

  /**
   * What is still serving. Stated before the failure on purpose — "your website is still up" is
   * the first thing a non-technical user needs, and reading it after the error is too late.
   */
  stillWorkingSummary(): string | null {
    const working = this.workingTargets();
    if (working.length === 0) {
      return null;
    }

    const names = working.map(t => this.partLabel(t).toLowerCase());
    if (names.length === 1) {
      return `Your ${names[0]} is still running.`;
    }

    return `Your ${names.slice(0, -1).join(', ')} and ${names[names.length - 1]} are still running.`;
  }

  partLabel(target: { role?: string | null; providerName: string }): string {
    return partLabelForTarget(target);
  }

  isLogTargetExpanded(targetId: string): boolean {
    return this.expandedLogTargets()[targetId] ?? false;
  }

  setLogTargetExpanded(targetId: string, expanded: boolean): void {
    this.expandedLogTargets.update(state => ({
      ...state,
      [targetId]: expanded
    }));
  }

  linesForProvider(providerName: string): ActivityLine[] {
    return this.deploymentLogs().filter(line => line.providerName === providerName);
  }

  canRedeployTarget(target: DeploymentDetail['targets'][number]): boolean {
    return target.status === 'failed' && !!target.deployTargetId;
  }

  isRedeploying(targetId: string): boolean {
    return this.redeployingTargets()[targetId] ?? false;
  }

  canSyncUrls(): boolean {
    return canSyncEnvironmentUrls(this.deployment());
  }

  repoOwner(): string | null {
    const project = this.project();
    if (!project) {
      return null;
    }

    const [owner] = project.githubRepoFullName.split('/');
    return owner || null;
  }

  repoName(): string | null {
    const project = this.project();
    if (!project) {
      return null;
    }

    const parts = project.githubRepoFullName.split('/');
    return parts[1] || null;
  }

  planParts(): DeploymentPlanPart[] {
    const project = this.project();
    if (!project) {
      return [];
    }

    return project.targets.map(target => {
      const config = parseTargetConfig(target.config);
      return {
        role: config.role ?? target.providerName,
        providerName: target.providerName,
        rootDirectory: config.rootDirectory,
        serviceDirectory: config.serviceDirectory,
        buildCommand: config.buildCommand,
        installCommand: config.installCommand,
        startCommand: config.startCommand,
        outputDirectory: config.outputDirectory,
        framework: config.framework,
        dockerfilePath: config.dockerfilePath
      };
    });
  }

  onSetupComplete(): void {
    this.loadDeploymentReadiness();
  }

  syncUrls(): void {
    this.syncingUrls.set(true);
    this.api.syncEnvironmentUrls(this.projectId).subscribe({
      next: (result) => {
        this.syncingUrls.set(false);
        if (result.skipped) {
          this.toast.show(result.skipReason ?? 'Reconnection was skipped.', 'info');
          return;
        }

        this.toast.success('Your site and app are reconnected.');
      },
      error: (err) => {
        this.syncingUrls.set(false);
        this.toast.error(err?.error?.error?.message ?? 'Could not reconnect your site and app.');
      }
    });
  }

  redeployTarget(target: DeploymentDetail['targets'][number]): void {
    const deployment = this.deployment();
    if (!deployment || !target.deployTargetId) {
      return;
    }

    this.redeployingTargets.update(state => ({ ...state, [target.id]: true }));
    this.api.redeployDeployTarget(this.projectId, target.deployTargetId, deployment.branch).subscribe({
      next: (response) => {
        this.redeployingTargets.update(state => ({ ...state, [target.id]: false }));
        void this.router.navigate(['/projects', this.projectId, 'deploy', response.deploymentId]);
      },
      error: (err) => {
        this.redeployingTargets.update(state => ({ ...state, [target.id]: false }));
        this.toast.error(err?.error?.error?.message ?? 'Could not redeploy that service.');
      }
    });
  }

  private loadPage(): void {
    if (!this.projectId) {
      this.pageLoading.set(false);
      this.loadError.set('Project not found.');
      return;
    }

    this.pageLoading.set(true);
    this.loadError.set(null);

    this.api
      .getAiSetupPreference(this.projectId)
      .pipe(
        catchError(() => of({ enabled: null as boolean | null })),
        switchMap((preference) => {
          if (preference.enabled !== null && preference.enabled !== undefined) {
            this.aiSetup.enabled.set(preference.enabled);
          }

          return this.api.getProject(this.projectId);
        }),
        switchMap((project) =>
          this.loadDeploymentForPage().pipe(
            switchMap((deployment) =>
              forkJoin({
                project: of(project),
                readiness: this.loadReadinessForPage(project),
                deployment: of(deployment),
                logs: deployment
                  ? this.api.getDeploymentLogs(deployment.id).pipe(
                      map(response => response.logs),
                      catchError(() => of([] as DeploymentLogLine[]))
                    )
                  : of([] as DeploymentLogLine[])
              })
            )
          )
        )
      )
      .subscribe({
        next: ({ project, readiness, deployment, logs }) => {
          this.project.set(project);
          this.deploymentReadiness.set(readiness);
          this.deployment.set(deployment);
          this.deploymentLogs.set(logs);
          this.expandedLogTargets.set(this.initialExpandedLogTargets(deployment));
          this.pageLoading.set(false);
        },
        error: (err) => {
          this.pageLoading.set(false);
          const message = err?.error?.error?.message ?? 'Could not load troubleshooting data.';
          this.loadError.set(message);
          this.toast.error(message);
        }
      });
  }

  private loadReadinessForPage(project: ProjectDetail): Observable<DeploymentReadinessResult | null> {
    if (!this.aiSetup.enabled()) {
      return of(null);
    }

    return this.api.getProjectDeploymentReadiness(this.projectId, project.defaultBranch).pipe(
      catchError(() => of(null))
    );
  }

  private loadDeploymentForPage(): Observable<DeploymentDetail | null> {
    const queryDeploymentId = this.route.snapshot.queryParamMap.get('deploymentId');
    if (queryDeploymentId) {
      return this.api.getDeployment(queryDeploymentId).pipe(catchError(() => of(null)));
    }

    return this.api.listDeployments(this.projectId).pipe(
      switchMap((response) => {
        const latest = this.pickVerificationDeployment(response.deployments);
        if (!latest) {
          return of(null);
        }

        return this.api.getDeployment(latest.id).pipe(catchError(() => of(null)));
      }),
      catchError(() => of(null))
    );
  }

  private pickVerificationDeployment(deployments: DeploymentSummary[]): DeploymentSummary | null {
    return deployments.find(item => item.status === 'success' || item.status === 'partial') ?? deployments[0] ?? null;
  }

  private initialExpandedLogTargets(deployment: DeploymentDetail | null): Record<string, boolean> {
    if (!deployment) {
      return {};
    }

    const expanded: Record<string, boolean> = {};
    for (const target of deployment.targets) {
      expanded[target.id] = target.status === 'failed';
    }

    return expanded;
  }

  private loadDeploymentReadiness(): void {
    const project = this.project();
    if (!project) {
      return;
    }

    this.loadingReadiness.set(true);
    this.api.getProjectDeploymentReadiness(this.projectId, project.defaultBranch).subscribe({
      next: (result) => {
        this.deploymentReadiness.set(result);
        this.loadingReadiness.set(false);
      },
      error: () => {
        this.deploymentReadiness.set(null);
        this.loadingReadiness.set(false);
      }
    });
  }
}
