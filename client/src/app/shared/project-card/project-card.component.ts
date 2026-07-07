import { Component, EventEmitter, Input, Output } from '@angular/core';
import { StatusBadgeComponent } from '../status-badge/status-badge.component';
import { plainTargetSummary } from '../../core/utils/target-config';
import { ProjectSummary } from '../../core/models/api.models';
import { ButtonComponent } from '../ui/button/button.component';
import { IconComponent } from '../ui/icon/icon.component';

@Component({
  selector: 'app-project-card',
  standalone: true,
  imports: [StatusBadgeComponent, ButtonComponent, IconComponent],
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
