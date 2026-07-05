import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { StatusBadgeComponent } from '../status-badge/status-badge.component';
import { ProjectSummary } from '../../core/models/api.models';

@Component({
  selector: 'app-project-card',
  standalone: true,
  imports: [CommonModule, StatusBadgeComponent],
  templateUrl: './project-card.component.html',
  styleUrl: './project-card.component.scss'
})
export class ProjectCardComponent {
  @Input({ required: true }) project!: ProjectSummary;
  @Output() publish = new EventEmitter<string>();
  @Output() open = new EventEmitter<string>();
  @Output() history = new EventEmitter<string>();

  get providerNames(): string {
    return this.project.targets.map(t => t.providerName).join(', ');
  }
}
