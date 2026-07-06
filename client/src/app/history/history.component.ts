import { Component, OnDestroy, OnInit, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { ApiService } from '../core/services/api.service';
import { DeploymentStore } from '../core/stores/deployment.store';
import { DeploymentSummary } from '../core/models/api.models';
import { ActivityLine } from '../shared/live-log-panel/live-log-panel.component';
import { StatusBadgeComponent } from '../shared/status-badge/status-badge.component';
import { EmptyStateComponent } from '../shared/empty-state/empty-state.component';
import { ProviderStatusCardComponent } from '../shared/provider-status-card/provider-status-card.component';
import { ButtonComponent } from '../shared/ui/button/button.component';

@Component({
  selector: 'app-history',
  standalone: true,
  imports: [
    StatusBadgeComponent,
    EmptyStateComponent,
    ProviderStatusCardComponent,
    ButtonComponent
  ],
  templateUrl: './history.component.html',
  styleUrl: './history.component.scss'
})
export class HistoryComponent implements OnInit, OnDestroy {
  readonly deployments = signal<DeploymentSummary[]>([]);
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
    this.api.listDeployments(this.projectId).subscribe({
      next: (response) => {
        this.deployments.set(response.deployments);
        this.loading.set(false);
      },
      error: () => this.loading.set(false)
    });
  }

  async ngOnDestroy(): Promise<void> {
    await this.deploymentStore.unload();
  }

  async open(deploymentId: string): Promise<void> {
    this.selectedId.set(deploymentId);
    this.expandedTargets.set({});
    this.redeployMessage.set(null);
    await this.deploymentStore.load(deploymentId);

    const deployment = this.deploymentStore.deployment();
    if (!deployment) {
      return;
    }

    const nextExpanded: Record<string, boolean> = {};
    for (const target of deployment.targets) {
      if (target.status === 'failed') {
        nextExpanded[target.id] = true;
      }
    }
    this.expandedTargets.set(nextExpanded);
  }

  closeReplay(): void {
    this.selectedId.set(null);
    this.expandedTargets.set({});
    this.redeployMessage.set(null);
    void this.deploymentStore.unload();
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

  whereLabel(item: DeploymentSummary): string {
    const urls = item.targets.filter(t => t.deployUrl).map(t => t.deployUrl);
    if (urls.length === 0) {
      return 'No live link saved';
    }
    return urls.length === 1 ? 'Live link saved' : `${urls.length} live links`;
  }

  roleLabel(providerName: string): string {
    return providerName === 'vercel' ? 'Your site' : 'Your API';
  }

  isExpanded(targetId: string): boolean {
    return this.expandedTargets()[targetId] ?? false;
  }

  setTargetExpanded(targetId: string, expanded: boolean): void {
    this.expandedTargets.update(state => ({
      ...state,
      [targetId]: expanded
    }));
  }

  linesForProvider(providerName: string): ActivityLine[] {
    return this.deploymentStore.activity().filter(line => line.providerName === providerName);
  }

  canRedeployTarget(target: { status: string; deployTargetId?: string }): boolean {
    return target.status === 'failed' && !!target.deployTargetId;
  }

  redeployTarget(target: {
    id: string;
    deployTargetId: string;
    providerName: string;
  }): void {
    this.redeployingTargets.update(state => ({ ...state, [target.id]: true }));
    this.redeployMessage.set(null);
    this.api.redeployProjectService(this.projectId, target.deployTargetId).subscribe({
      next: () => {
        this.redeployingTargets.update(state => ({ ...state, [target.id]: false }));
        this.redeployMessage.set(`${this.roleLabel(target.providerName)} redeploy started.`);
        this.expandedTargets.update(state => ({ ...state, [target.id]: true }));
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
