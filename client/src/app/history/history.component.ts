import { Component, OnInit, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { ApiService } from '../core/services/api.service';
import { DeploymentSummary } from '../core/models/api.models';
import { EmptyStateComponent } from '../shared/empty-state/empty-state.component';
import { IconComponent, IconName, IconStatusClass } from '../shared/ui/icon/icon.component';

@Component({
  selector: 'app-history',
  standalone: true,
  imports: [
    EmptyStateComponent,
    IconComponent
  ],
  templateUrl: './history.component.html',
  styleUrl: './history.component.scss'
})
export class HistoryComponent implements OnInit {
  readonly deployments = signal<DeploymentSummary[]>([]);
  readonly projectName = signal('');
  readonly searchQuery = signal('');
  readonly loading = signal(true);

  private projectId = '';

  constructor(
    private readonly route: ActivatedRoute,
    private readonly router: Router,
    private readonly api: ApiService
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
      },
      error: () => this.loading.set(false)
    });
  }

  filteredDeployments(): DeploymentSummary[] {
    const query = this.searchQuery().trim().toLowerCase();
    const items = this.deployments();
    if (!query) {
      return items;
    }
    return items.filter(item =>
      item.branch.toLowerCase().includes(query) ||
      (item.gitCommitMessage?.toLowerCase().includes(query) ?? false) ||
      (item.gitCommitSha?.toLowerCase().includes(query) ?? false)
    );
  }

  groupedDeployments(): { key: string; label: string; items: DeploymentSummary[] }[] {
    const groups = new Map<string, DeploymentSummary[]>();
    for (const item of this.filteredDeployments()) {
      const raw = item.completedAt || item.startedAt;
      const date = raw ? new Date(raw) : new Date();
      const key = `${date.getFullYear()}-${date.getMonth()}-${date.getDate()}`;
      const bucket = groups.get(key);
      if (bucket) {
        bucket.push(item);
      } else {
        groups.set(key, [item]);
      }
    }
    return [...groups.entries()].map(([key, items]) => ({
      key,
      label: this.dayLabel(items[0].completedAt || items[0].startedAt),
      items
    }));
  }

  successRate(): number {
    const items = this.deployments();
    if (items.length === 0) {
      return 0;
    }
    const succeeded = items.filter(item => item.status === 'success').length;
    return Math.round((succeeded / items.length) * 100);
  }

  lastDeployRelative(): string {
    const items = this.deployments();
    if (items.length === 0) {
      return '—';
    }
    return this.relativeTime(items[0].completedAt || items[0].startedAt);
  }

  onSearch(event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.searchQuery.set(value);
  }

  open(deploymentId: string): void {
    void this.router.navigate(['/projects', this.projectId, 'deploy', deploymentId]);
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

  relativeTime(value?: string): string {
    if (!value) {
      return 'just now';
    }
    const then = new Date(value).getTime();
    const seconds = Math.round((Date.now() - then) / 1000);
    if (seconds < 45) {
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
    if (days < 7) {
      return `${days}d ago`;
    }
    const weeks = Math.round(days / 7);
    if (weeks < 5) {
      return `${weeks}w ago`;
    }
    return new Date(value).toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
  }

  formatDuration(seconds?: number): string | null {
    if (seconds === undefined || seconds === null) {
      return null;
    }
    if (seconds < 60) {
      return `${seconds}s`;
    }
    const minutes = Math.floor(seconds / 60);
    const rest = seconds % 60;
    return rest ? `${minutes}m ${rest}s` : `${minutes}m`;
  }

  private dayLabel(value?: string): string {
    const date = value ? new Date(value) : new Date();
    const today = new Date();
    const yesterday = new Date();
    yesterday.setDate(today.getDate() - 1);
    const sameDay = (a: Date, b: Date): boolean =>
      a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate();
    if (sameDay(date, today)) {
      return 'Today';
    }
    if (sameDay(date, yesterday)) {
      return 'Yesterday';
    }
    return date.toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric', year: 'numeric' });
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
}
