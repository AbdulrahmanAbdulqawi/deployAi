import { Component, Input } from '@angular/core';
import { IconComponent, IconName, IconStatusClass } from '../ui/icon/icon.component';

export type StatusBadgeKind = 'deployment' | 'service';

@Component({
  selector: 'app-status-badge',
  standalone: true,
  imports: [IconComponent],
  templateUrl: './status-badge.component.html',
  styleUrl: './status-badge.component.scss'
})
export class StatusBadgeComponent {
  @Input({ required: true }) status = 'pending';
  @Input() live = false;
  @Input() kind: StatusBadgeKind = 'deployment';
  @Input() labelOverride?: string;

  get label(): string {
    if (this.labelOverride) {
      return this.labelOverride;
    }
    if (this.kind === 'service') {
      switch (this.status) {
        case 'running':
          return 'Running';
        case 'deploying':
          return 'Starting';
        case 'failed':
          return 'Failed';
        case 'not_deployed':
          return 'Stopped';
        case 'unknown':
          return '…';
        default:
          return '…';
      }
    }

    switch (this.status) {
      case 'success':
        return 'Live';
      case 'in_progress':
        return 'Deploying';
      case 'failed':
        return 'Failed';
      case 'partial':
        return 'Partial';
      case 'cancelled':
        return 'Cancelled';
      case 'pending':
        return 'Pending';
      case 'idle':
        return 'New';
      default:
        return 'Draft';
    }
  }

  get statusClass(): string {
    if (this.kind === 'service') {
      switch (this.status) {
        case 'running':
          return 'live';
        case 'deploying':
          return 'working';
        case 'failed':
          return 'failed';
        case 'not_deployed':
          return 'idle';
        default:
          return 'working';
      }
    }

    switch (this.status) {
      case 'success':
        return 'live';
      case 'in_progress':
      case 'pending':
        return 'working';
      case 'failed':
        return 'failed';
      case 'cancelled':
        return 'idle';
      case 'partial':
        return 'attention';
      default:
        return 'idle';
    }
  }

  get iconName(): IconName {
    switch (this.statusClass) {
      case 'live':
        return 'check';
      case 'working':
        return 'loader';
      case 'failed':
        return 'x';
      case 'attention':
        return 'alert';
      default:
        return 'info';
    }
  }

  get iconStatusClass(): IconStatusClass {
    switch (this.statusClass) {
      case 'live':
        return 'success';
      case 'working':
        return 'accent';
      case 'failed':
        return 'danger';
      case 'attention':
        return 'warning';
      default:
        return 'muted';
    }
  }

  get isLive(): boolean {
    if (this.live) {
      return true;
    }
    return this.kind === 'service'
      ? this.status === 'deploying'
      : this.status === 'in_progress';
  }

  get showDot(): boolean {
    return !this.labelOverride;
  }
}
