import { Injectable, computed, inject, signal } from '@angular/core';
import { ApiService } from '../services/api.service';
import { ProjectSummary } from '../models/api.models';
import { ToastService } from '../../shared/ui/toast/toast.service';
import { Router } from '@angular/router';

const CACHE_KEY = 'deployai_projects_cache';

@Injectable({ providedIn: 'root' })
export class ProjectsStore {
  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  private readonly router = inject(Router);

  readonly projects = signal<ProjectSummary[]>(this.readCache());
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly deployingProjectId = signal<string | null>(null);

  readonly isEmpty = computed(() => !this.loading() && this.projects().length === 0);
  readonly hasProjects = computed(() => this.projects().length > 0);

  load(options?: { background?: boolean }): void {
    const background = options?.background ?? false;
    if (!background) {
      this.loading.set(this.projects().length === 0);
    }
    this.error.set(null);

    this.api.getProjects().subscribe({
      next: (response) => {
        this.projects.set(response.projects);
        this.writeCache(response.projects);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(err?.error?.error?.message ?? 'Something went wrong loading your apps.');
        this.loading.set(false);
      }
    });
  }

  refresh(): void {
    this.load({ background: true });
  }

  updateProjectDeployment(projectId: string, deploymentId: string, status: string): void {
    this.projects.update(list =>
      list.map(project =>
        project.id === projectId
          ? {
              ...project,
              latestDeployment: {
                id: deploymentId,
                status,
                completedAt: status === 'success' || status === 'failed' ? new Date().toISOString() : undefined
              }
            }
          : project
      )
    );
    this.writeCache(this.projects());
  }

  triggerDeploy(projectId: string): void {
    this.deployingProjectId.set(projectId);
    this.projects.update(list =>
      list.map(project =>
        project.id === projectId
          ? {
              ...project,
              latestDeployment: {
                id: project.latestDeployment?.id ?? 'pending',
                status: 'in_progress'
              }
            }
          : project
      )
    );

    this.api.triggerDeployment(projectId).subscribe({
      next: (response) => {
        this.deployingProjectId.set(null);
        this.updateProjectDeployment(projectId, response.deploymentId, response.status);
        void this.router.navigate(['/projects', projectId, 'deploy', response.deploymentId]);
      },
      error: (err) => {
        this.deployingProjectId.set(null);
        this.toast.error(err?.error?.error?.message ?? 'Publishing did not start. Try again.');
        this.refresh();
      }
    });
  }

  private readCache(): ProjectSummary[] {
    try {
      const raw = sessionStorage.getItem(CACHE_KEY);
      return raw ? (JSON.parse(raw) as ProjectSummary[]) : [];
    } catch {
      return [];
    }
  }

  private writeCache(projects: ProjectSummary[]): void {
    sessionStorage.setItem(CACHE_KEY, JSON.stringify(projects));
  }
}
