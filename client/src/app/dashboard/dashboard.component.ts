import { Component, OnInit, signal } from '@angular/core';
import { Router } from '@angular/router';
import { ApiService } from '../core/services/api.service';
import { ProjectsStore } from '../core/stores/projects.store';
import { ProjectCardComponent } from '../shared/project-card/project-card.component';
import { EmptyStateComponent } from '../shared/empty-state/empty-state.component';
import { ButtonComponent } from '../shared/ui/button/button.component';
import { ConfirmDialogComponent } from '../shared/confirm-dialog/confirm-dialog.component';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [ProjectCardComponent, EmptyStateComponent, ButtonComponent, ConfirmDialogComponent],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss'
})
export class DashboardComponent implements OnInit {
  readonly showDeleteConfirm = signal(false);
  readonly deleting = signal(false);
  readonly deleteProjectId = signal<string | null>(null);
  readonly deleteProjectName = signal('');

  constructor(
    readonly store: ProjectsStore,
    private readonly router: Router,
    private readonly api: ApiService
  ) {}
  ngOnInit(): void {
    this.store.load();
  }

  createProject(): void {
    void this.router.navigate(['/projects/new']);
  }

  publish(projectId: string): void {
    this.store.triggerDeploy(projectId);
  }

  openHistory(projectId: string): void {
    void this.router.navigate(['/projects', projectId, 'history']);
  }

  openProject(projectId: string): void {
    void this.router.navigate(['/projects', projectId]);
  }

  requestDelete(projectId: string): void {
    const project = this.store.projects().find((item) => item.id === projectId);
    this.deleteProjectId.set(projectId);
    this.deleteProjectName.set(project?.name ?? 'this app');
    this.showDeleteConfirm.set(true);
  }

  confirmDelete(): void {
    const projectId = this.deleteProjectId();
    if (!projectId) {
      return;
    }

    this.deleting.set(true);
    this.showDeleteConfirm.set(false);
    this.api.deleteProject(projectId).subscribe({
      next: () => {
        this.deleting.set(false);
        this.deleteProjectId.set(null);
        this.store.load();
      },
      error: () => {
        this.deleting.set(false);
        this.deleteProjectId.set(null);
        this.store.load();
      }
    });
  }

  cancelDelete(): void {
    this.showDeleteConfirm.set(false);
    this.deleteProjectId.set(null);
  }

  retry(): void {
    this.store.load();
  }
}
