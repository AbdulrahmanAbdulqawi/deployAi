import { Component, OnInit, computed, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { ProjectsStore } from '../core/stores/projects.store';
import { ProjectCardComponent } from '../shared/project-card/project-card.component';
import { EmptyStateComponent } from '../shared/empty-state/empty-state.component';
import { ButtonComponent } from '../shared/ui/button/button.component';
import { InputComponent } from '../shared/ui/input/input.component';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [ProjectCardComponent, EmptyStateComponent, ButtonComponent, InputComponent, FormsModule],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss'
})
export class DashboardComponent implements OnInit {
  readonly search = signal('');

  readonly filteredProjects = computed(() => {
    const query = this.search().trim().toLowerCase();
    const projects = this.store.projects();
    if (!query) {
      return projects;
    }
    return projects.filter((project) => project.name.toLowerCase().includes(query));
  });

  readonly noSearchMatches = computed(
    () => this.search().trim().length > 0 && this.filteredProjects().length === 0
  );

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
