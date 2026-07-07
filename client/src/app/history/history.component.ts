import { Component, OnDestroy, OnInit, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { ApiService } from '../core/services/api.service';
import { DeploymentStore } from '../core/stores/deployment.store';
import { DeploymentDetail, DeploymentSummary } from '../core/models/api.models';
import { ActivityLine, LiveLogPanelComponent } from '../shared/live-log-panel/live-log-panel.component';
import { EmptyStateComponent } from '../shared/empty-state/empty-state.component';
import { ButtonComponent } from '../shared/ui/button/button.component';
import { IconComponent, IconName, IconStatusClass } from '../shared/ui/icon/icon.component';
import { providerLabel } from '../core/utils/target-config';

@Component({
  selector: 'app-history',
  standalone: true,
  imports: [
    EmptyStateComponent,
    ButtonComponent,
    IconComponent,
    LiveLogPanelComponent
  ],
  templateUrl: './history.component.html',
  styleUrl: './history.component.scss'
})
export class HistoryComponent implements OnInit, OnDestroy {
  readonly deployments = signal<DeploymentSummary[]>([]);
  readonly projectName = signal('');
  readonly searchQuery = signal('');
  readonly loading = signal(true);
  readonly selectedId = signal<string | null>(null);
  readonly expandedTargets = signal<Record<string, boolean>>({});
  readonly redeployingTargets = signal<Record<string, boolean>>({});
  readonly redeployMessage = signal<string | null>(null);

  private projectId = '';

  constructor(
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly api: ApiService,
    readonly deploymentStore: DeploymentStore
  ) {}

  ngOnInit(): void {
    this.projectId = this.route.snapshot.paramMap.get('id') ?? '';

    this.api.getProject(this.projectId).subscribe({
      next: (project) => this.projectName.set(project.name),
      error: () => undefined
    });

    this.api.listDeployments(this.projectId).subscribe({
      next: (response) => {
        this.deployments.set(response.deployments);
        this.loading.set(false);
        if (response.deployments.length > 0) {
          void this.open(response.deployments[0].id);
        }
      },
      error: () => this.loading.set(false)
    });
  }

  async ngOnDestroy(): Promise<void> {
    await this.deploymentStore.unload();
  }

  filteredDeployments(): DeploymentSummary[] {
    const query = this.searchQuery().trim().toLowerCase();
    const items = this.deployments();
    if (!query) {
      return items;
    }
    return items.filter(item => item.branch.toLowerCase().includes(query));
  }

  onSearch(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.searchQuery.set(value);
  }

  async open(deploymentId: string): Promise<void> {
    this.selectedId.set(deploymentId);
    this.expandedTargets.set({});
    this.redeployMessage.set(null);
    await this.deploymentStore.load(deploymentId);
  }

  back(): void {
    void this.router.navigate(['/projects', this.projectId]);
  }

  formatWhen(value?: string): string {
    if (!value) {
      return 'Just now';
    }
    return new Date(value).toLocaleString();
  }

  statusIcon(status: string): IconName {
    switch (status) {
      case 'success':
        return 'check';
      case 'failed':
        return 'x';
      case 'in_progress':
        return 'refresh';
      default:
        return 'info';
    }
  }

  statusIconClass(status: string): IconStatusClass {
    switch (status) {
      case 'success':
        return 'success';
      case 'failed':
        return 'danger';
      case 'in_progress':
        return 'accent';
      default:
        return 'muted';
    }
  }

  statusLabel(status: string): string {
    switch (status) {
      case 'success':
        return 'Success';
      case 'failed':
        return 'Failed';
      case 'in_progress':
        return 'In Progress';
      case 'partial':
        return 'Partial';
      case 'cancelled':
        return 'Cancelled';
      default:
        return 'Pending';
    }
  }

  providerLabel = providerLabel;

  targetRoleLabel(providerName: string): string {
    return providerName === 'vercel' ? 'Static Build' : 'Containerization';
  }

  targetStatusText(target: DeploymentDetail['targets'][number]): string {
    switch (target.status) {
      case 'success':
        return target.deployUrl ? '200 OK' : 'Live';
      case 'failed':
        return 'Exit Code 1';
      case 'in_progress':
        return 'Building…';
      default:
        return 'Pending';
    }
  }

  shortId(id: string): string {
    return `#${id.slice(-4).toUpperCase()}`;
  }

  allLines(): ActivityLine[] {
    return this.deploymentStore.activity();
  }

  failedTarget(): DeploymentDetail['targets'][number] | null {
    const deployment = this.deploymentStore.deployment();
    if (!deployment) {
      return null;
    }
    return deployment.targets.find(t => t.status === 'failed' && t.deployTargetId) ?? null;
  }

  resolutionHint(deployment: DeploymentDetail): string {
    const failed = deployment.targets.filter(t => t.status === 'failed');
    if (failed.length === 0) {
      return deployment.status === 'success' ? 'Deployment succeeded' : 'Review logs';
    }
    if (failed.some(t => t.providerName === 'railway')) {
      return 'Check Railway credentials or env vars';
    }
    return 'Review build logs and redeploy';
  }

  redeployTarget(target: {
    id: string;
    deployTargetId: string;
    providerName: string;
  }): void {
    const deployment = this.deploymentStore.deployment();
    if (!deployment) {
      return;
    }

    this.redeployingTargets.update(state => ({ ...state, [target.id]: true }));
    this.redeployMessage.set(null);
    this.api.redeployDeployTarget(this.projectId, target.deployTargetId, deployment.branch).subscribe({
      next: (response) => {
        this.redeployingTargets.update(state => ({ ...state, [target.id]: false }));
        void this.router.navigate(['/projects', this.projectId, 'deploy', response.deploymentId]);
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
}
