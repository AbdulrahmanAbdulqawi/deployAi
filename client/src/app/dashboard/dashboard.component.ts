import { Component, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { ProjectsStore } from '../core/stores/projects.store';
import { ProjectCardComponent } from '../shared/project-card/project-card.component';
import { EmptyStateComponent } from '../shared/empty-state/empty-state.component';
import { ButtonComponent } from '../shared/ui/button/button.component';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [ProjectCardComponent, EmptyStateComponent, ButtonComponent],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss'
})
export class DashboardComponent implements OnInit {
  constructor(
    readonly store: ProjectsStore,
    private readonly router: Router
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

  openProject(projectId: string): void {
    void this.router.navigate(['/projects', projectId]);
  }

  openFix(projectId: string): void {
    const project = this.store.projects().find((item) => item.id === projectId);
    const deploymentId = project?.latestDeployment?.id;
    if (!deploymentId) {
      return;
    }

    void this.router.navigate(['/projects', projectId, 'deploy', deploymentId]);
  }

  retry(): void {
    this.store.load();
  }
}
