import { Component, EventEmitter, Input, Output } from '@angular/core';
import { StatusBadgeComponent } from '../status-badge/status-badge.component';
import { ProjectSummary } from '../../core/models/api.models';
import { ButtonComponent } from '../ui/button/button.component';
import { AppLogoComponent } from '../app-logo/app-logo.component';
import { isAppLogoId } from '../../core/constants/app-logos';

@Component({
  selector: 'app-project-card',
  standalone: true,
  imports: [StatusBadgeComponent, ButtonComponent, AppLogoComponent],
  templateUrl: './project-card.component.html',
  styleUrl: './project-card.component.scss'
})
export class ProjectCardComponent {
  @Input({ required: true }) project!: ProjectSummary;
  @Input() publishing = false;
  @Output() publish = new EventEmitter<string>();
  @Output() open = new EventEmitter<string>();
  @Output() fix = new EventEmitter<string>();

  get isDeploying(): boolean {
    return this.publishing || this.project.latestDeployment?.status === 'in_progress';
  }

  get canFix(): boolean {
    return !!this.project.latestDeployment?.canRequestClaudeFix;
  }

  get hasLogo(): boolean {
    return isAppLogoId(this.project.logoKey);
  }

  get updatedLabel(): string | null {
    const when = this.project.latestDeployment?.completedAt;
    if (!when) {
      return null;
    }
    return `Updated ${this.relativeTime(when)}`;
  }

  onCardClick(): void {
    this.open.emit(this.project.id);
  }

  onPublishClick(event: MouseEvent): void {
    event.stopPropagation();
    this.publish.emit(this.project.id);
  }

  onFixClick(event: MouseEvent): void {
    event.stopPropagation();
    if (!this.canFix) {
      return;
    }

    this.fix.emit(this.project.id);
  }

  private relativeTime(value: string): string {
    const then = new Date(value).getTime();
    const seconds = Math.round((Date.now() - then) / 1000);
    if (seconds < 60) {
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
    if (days < 30) {
      return `${days}d ago`;
    }
    return new Date(value).toLocaleDateString();
  }
}
