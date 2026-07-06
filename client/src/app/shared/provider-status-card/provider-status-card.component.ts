import { Component, Input } from '@angular/core';
import { StatusBadgeComponent } from '../status-badge/status-badge.component';
import { ButtonComponent } from '../ui/button/button.component';
import { IconComponent } from '../ui/icon/icon.component';

@Component({
  selector: 'app-provider-status-card',
  standalone: true,
  imports: [StatusBadgeComponent, ButtonComponent, IconComponent],
  templateUrl: './provider-status-card.component.html',
  styleUrl: './provider-status-card.component.scss'
})
export class ProviderStatusCardComponent {
  @Input({ required: true }) roleLabel = '';
  @Input({ required: true }) providerName = '';
  @Input({ required: true }) status = 'pending';
  @Input() deployUrl?: string;
  @Input() startedAt?: string;
  @Input() showProviderName = true;
  @Input() showFailureDetails = false;

  get elapsedLabel(): string {
    if (!this.startedAt || this.status !== 'in_progress') {
      return '';
    }
    const started = new Date(this.startedAt).getTime();
    const seconds = Math.max(0, Math.floor((Date.now() - started) / 1000));
    if (seconds < 60) {
      return `Working… ${seconds}s`;
    }
    const minutes = Math.floor(seconds / 60);
    return `Working… ${minutes}m`;
  }

  toggleFailureDetails(): void {
    this.showFailureDetails = !this.showFailureDetails;
  }
}
