import { Component, EventEmitter, Input, Output } from '@angular/core';
import { StatusBadgeComponent } from '../status-badge/status-badge.component';
import { plainTargetSummary } from '../../core/utils/target-config';
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

  get targetSummary(): string {
    return plainTargetSummary(this.project.targets);
  }

  get isDeploying(): boolean {
    return this.publishing || this.project.latestDeployment?.status === 'in_progress';
  }

  get canFix(): boolean {
    return !!this.project.latestDeployment?.canRequestClaudeFix;
  }

  get hasLogo(): boolean {
    return isAppLogoId(this.project.logoKey);
  }

  orbTone(): string {
    const status = this.project.latestDeployment?.status;
    switch (status) {
      case 'success':
        return 'success';
      case 'failed':
        return 'failed';
      case 'in_progress':
        return 'working';
      case 'partial':
        return 'attention';
      default:
        return 'idle';
    }
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
}
