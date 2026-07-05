import { Component, OnInit, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { ApiService } from '../core/services/api.service';
import { DeploymentSummary } from '../core/models/api.models';
import { StatusBadgeComponent } from '../shared/status-badge/status-badge.component';
import { EmptyStateComponent } from '../shared/empty-state/empty-state.component';

@Component({
  selector: 'app-history',
  standalone: true,
  imports: [StatusBadgeComponent, EmptyStateComponent],
  templateUrl: './history.component.html',
  styleUrl: './history.component.scss'
})
export class HistoryComponent implements OnInit {
  readonly deployments = signal<DeploymentSummary[]>([]);
  readonly loading = signal(true);

  private projectId = '';

  constructor(
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly api: ApiService
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

  open(deploymentId: string): void {
    void this.router.navigate(['/publish', deploymentId]);
  }

  back(): void {
    void this.router.navigate(['/projects', this.projectId]);
  }

  formatWhen(value?: string): string {
    if (!value) return 'Just now';
    const date = new Date(value);
    return date.toLocaleString();
  }
}
