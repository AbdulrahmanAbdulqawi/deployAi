import { Component, EventEmitter, Input, Output } from '@angular/core';
import { StatusBadgeComponent } from '../status-badge/status-badge.component';
import { plainTargetSummary } from '../../core/utils/target-config';
import { ProjectSummary } from '../../core/models/api.models';
import { ButtonComponent } from '../ui/button/button.component';
import { DropdownMenuComponent, DropdownMenuItem } from '../ui/dropdown/dropdown-menu.component';

@Component({
  selector: 'app-project-card',
  standalone: true,
  imports: [StatusBadgeComponent, ButtonComponent, DropdownMenuComponent],
  templateUrl: './project-card.component.html',
  styleUrl: './project-card.component.scss'
})
export class ProjectCardComponent {
  @Input({ required: true }) project!: ProjectSummary;
  @Input() publishing = false;
  @Output() publish = new EventEmitter<string>();
  @Output() open = new EventEmitter<string>();
  @Output() history = new EventEmitter<string>();
  @Output() delete = new EventEmitter<string>();

  readonly moreItems: DropdownMenuItem[] = [
    { id: 'open', label: 'Open', icon: 'folder' },
    { id: 'history', label: 'History', icon: 'clock' },
    { id: 'delete', label: 'Delete', icon: 'trash', destructive: true }
  ];

  get targetSummary(): string {
    return plainTargetSummary(this.project.targets);
  }

  onMoreAction(action: string): void {
    switch (action) {
      case 'open':
        this.open.emit(this.project.id);
        break;
      case 'history':
        this.history.emit(this.project.id);
        break;
      case 'delete':
        this.delete.emit(this.project.id);
        break;
      default:
        break;
    }
  }
}
