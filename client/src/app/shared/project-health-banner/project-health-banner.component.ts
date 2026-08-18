import { Component, input } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ProjectHealthState, ProjectHealthStatus } from '../../core/models/api.models';
import { IconComponent, IconStatusClass } from '../ui/icon/icon.component';

@Component({
  selector: 'app-project-health-banner',
  standalone: true,
  imports: [DatePipe, IconComponent],
  templateUrl: './project-health-banner.component.html',
  styleUrl: './project-health-banner.component.scss'
})
export class ProjectHealthBannerComponent {
  readonly health = input<ProjectHealthState | null | undefined>(null);

  statusIcon(): IconStatusClass {
    switch (this.health()?.status) {
      case ProjectHealthStatus.Healthy:
        return 'success';
      case ProjectHealthStatus.Degraded:
        return 'warning';
      case ProjectHealthStatus.Down:
        return 'danger';
      default:
        return 'muted';
    }
  }

  statusLabel(): string {
    switch (this.health()?.status) {
      case ProjectHealthStatus.Healthy:
        return 'Healthy';
      case ProjectHealthStatus.Degraded:
        return 'Needs attention';
      case ProjectHealthStatus.Down:
        return 'Down';
      // Distinct from Unknown, which means no check has ever run. This one means DeployAI checked
      // and could not see — and it fell into "Unknown" before, so an app the platform had entirely
      // lost sight of looked exactly like one it had never looked at. The summary carries the reason.
      case ProjectHealthStatus.Inconclusive:
        return "Couldn't check";
      default:
        return 'Unknown';
    }
  }
}
