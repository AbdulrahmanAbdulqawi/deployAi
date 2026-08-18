import { Component, DestroyRef, OnInit, inject } from '@angular/core';
import { ActivatedRoute, Router, RouterOutlet } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ProjectWorkspaceContext } from './project-workspace-context.service';
import { StatusBadgeComponent } from '../../shared/status-badge/status-badge.component';
import { ButtonComponent } from '../../shared/ui/button/button.component';
import { IconComponent } from '../../shared/ui/icon/icon.component';
import { TabBarComponent, TabBarItem } from '../../shared/ui/tab-bar/tab-bar.component';

const TABS: TabBarItem[] = [
  { label: 'Overview', path: 'overview' },
  { label: 'Activity', path: 'activity' },
  { label: 'Domains', path: 'domains' },
  { label: 'Troubleshoot', path: 'troubleshoot' },
  { label: 'Settings', path: 'settings' }
];

@Component({
  selector: 'app-project-workspace',
  standalone: true,
  imports: [RouterOutlet, StatusBadgeComponent, ButtonComponent, IconComponent, TabBarComponent],
  providers: [ProjectWorkspaceContext],
  templateUrl: './project-workspace.component.html',
  styleUrl: './project-workspace.component.scss'
})
export class ProjectWorkspaceComponent implements OnInit {
  readonly tabs = TABS;
  private readonly destroyRef = inject(DestroyRef);

  constructor(
    readonly context: ProjectWorkspaceContext,
    private readonly route: ActivatedRoute,
    private readonly router: Router
  ) {}

  ngOnInit(): void {
    this.route.paramMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((params) => {
      const id = params.get('id');
      if (id) {
        this.context.load(id);
      }
    });
  }

  goToProjects(): void {
    void this.router.navigate(['/dashboard']);
  }

  publish(): void {
    this.context.publish();
  }
}
