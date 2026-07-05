import { Component, OnInit, signal } from '@angular/core';
import { Router } from '@angular/router';
import { ApiService } from '../core/services/api.service';
import { ProjectSummary } from '../core/models/api.models';
import { ProjectCardComponent } from '../shared/project-card/project-card.component';
import { EmptyStateComponent } from '../shared/empty-state/empty-state.component';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [ProjectCardComponent, EmptyStateComponent],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss'
})
export class DashboardComponent implements OnInit {
  readonly projects = signal<ProjectSummary[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  constructor(
    private readonly api: ApiService,
    private readonly router: Router
  ) {}

  ngOnInit(): void {
    this.api.getProjects().subscribe({
      next: (response) => {
        this.projects.set(response.projects);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(err?.error?.error?.message ?? 'Something went wrong loading your apps.');
        this.loading.set(false);
      }
    });
  }

  createProject(): void {
    void this.router.navigate(['/projects/new']);
  }

  publish(projectId: string): void {
    this.api.triggerDeployment(projectId).subscribe({
      next: (response) => void this.router.navigate(['/publish', response.deploymentId]),
      error: (err) => this.error.set(err?.error?.error?.message ?? 'Publishing did not start. Try again.')
    });
  }

  openHistory(projectId: string): void {
    void this.router.navigate(['/projects', projectId, 'history']);
  }

  openProject(projectId: string): void {
    void this.router.navigate(['/projects', projectId]);
  }
}
