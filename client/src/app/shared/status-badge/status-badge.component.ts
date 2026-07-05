import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';

@Component({
  selector: 'app-status-badge',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './status-badge.component.html',
  styleUrl: './status-badge.component.scss'
})
export class StatusBadgeComponent {
  @Input({ required: true }) status = 'pending';

  get label(): string {
    switch (this.status) {
      case 'success': return 'Live';
      case 'in_progress': return 'Getting ready';
      case 'failed': return "Didn't go through";
      case 'partial': return 'Needs a quick fix';
      case 'pending': return 'Getting started';
      default: return 'Ready to publish';
    }
  }

  get statusClass(): string {
    switch (this.status) {
      case 'success': return 'live';
      case 'in_progress':
      case 'pending': return 'working';
      case 'failed': return 'failed';
      case 'partial': return 'attention';
      default: return 'idle';
    }
  }
}
