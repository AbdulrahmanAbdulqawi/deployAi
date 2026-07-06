import { Component, OnDestroy, OnInit, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { ApiService } from '../core/services/api.service';
import { DeploymentStore } from '../core/stores/deployment.store';
import { DeploymentSummary } from '../core/models/api.models';
import { StatusBadgeComponent } from '../shared/status-badge/status-badge.component';
import { EmptyStateComponent } from '../shared/empty-state/empty-state.component';
import { ProviderStatusCardComponent } from '../shared/provider-status-card/provider-status-card.component';
import { LiveLogPanelComponent } from '../shared/live-log-panel/live-log-panel.component';
import { ButtonComponent } from '../shared/ui/button/button.component';

@Component({
  selector: 'app-history',
  standalone: true,
  imports: [
    StatusBadgeComponent,
    EmptyStateComponent,
    ProviderStatusCardComponent,
    LiveLogPanelComponent,
    ButtonComponent
  ],
  templateUrl: './history.component.html',
  styleUrl: './history.component.scss'
})
export class HistoryComponent implements OnInit, OnDestroy {
  readonly deployments = signal<DeploymentSummary[]>([]);
  readonly loading = signal(true);
  readonly selectedId = signal<string | null>(null);

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
    await this.deploymentStore.load(deploymentId);
  }

  closeReplay(): void {
    this.selectedId.set(null);
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
}
