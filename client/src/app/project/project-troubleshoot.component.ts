import { Component, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { catchError, forkJoin, map, Observable, of, switchMap } from 'rxjs';
import { ApiService } from '../core/services/api.service';
import { AiSetupPreferenceService } from '../core/services/ai-setup-preference.service';
import { ToastService } from '../shared/ui/toast/toast.service';
import {
  DeploymentDetail,
  DeploymentLogLine,
  DeploymentPlanPart,
  DeploymentReadinessResult,
  DeploymentSummary,
  ProjectDetail,
  ProviderName
} from '../core/models/api.models';
import { parseTargetConfig, roleLabelForProvider } from '../core/utils/target-config';
import { IconComponent } from '../shared/ui/icon/icon.component';
import { DeploymentSetupPanelComponent } from '../shared/deployment-setup-panel/deployment-setup-panel.component';
import { DeploymentVerificationPanelComponent } from '../shared/deployment-verification-panel/deployment-verification-panel.component';
import { ProviderStatusCardComponent } from '../shared/provider-status-card/provider-status-card.component';
import { ActivityLine } from '../shared/live-log-panel/live-log-panel.component';

@Component({
  selector: 'app-project-troubleshoot',
  standalone: true,
  imports: [
    FormsModule,
    IconComponent,
    DeploymentSetupPanelComponent,
    DeploymentVerificationPanelComponent,
    ProviderStatusCardComponent
  ],
  templateUrl: './project-troubleshoot.component.html',
  styleUrl: './project-troubleshoot.component.scss'
})
export class ProjectTroubleshootComponent implements OnInit {
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
    const deployment = this.deployment();
    if (!deployment) {
      return false;
    }

    const hasRailway = deployment.targets.some(t => t.providerName === 'railway' && t.status === 'success');
    const hasVercel = deployment.targets.some(t => t.providerName === 'vercel' && t.status === 'success');
    return hasRailway && hasVercel;
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

  goToProject(): void {
    void this.router.navigate(['/projects', this.projectId]);
  }

  goToProjects(): void {
    void this.router.navigate(['/dashboard']);
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
