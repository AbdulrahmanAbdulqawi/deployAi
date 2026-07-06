import { Component, EventEmitter, Input, Output } from '@angular/core';
import { StatusBadgeComponent } from '../status-badge/status-badge.component';
import { ButtonComponent } from '../ui/button/button.component';
import { IconComponent } from '../ui/icon/icon.component';
import { LiveLogPanelComponent, ActivityLine } from '../live-log-panel/live-log-panel.component';

@Component({
  selector: 'app-provider-status-card',
  standalone: true,
  imports: [StatusBadgeComponent, ButtonComponent, IconComponent, LiveLogPanelComponent],
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
  @Input() expanded = false;
  @Input() lines: ActivityLine[] = [];
  @Input() connecting = false;
  @Input() canRedeploy = false;
  @Input() redeploying = false;

  @Output() expandedChange = new EventEmitter<boolean>();
  @Output() redeploy = new EventEmitter<void>();

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

  get providerDisplayName(): string {
    return this.providerName === 'vercel' ? 'Vercel' : 'Railway';
  }

  toggleExpanded(): void {
    this.expandedChange.emit(!this.expanded);
  }

  onRedeploy(event: Event): void {
    event.stopPropagation();
    this.redeploy.emit();
  }
}
