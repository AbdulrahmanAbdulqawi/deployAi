import { Component, EventEmitter, Input, OnChanges, OnDestroy, Output, SimpleChanges } from '@angular/core';
import { DatePipe } from '@angular/common';
import { StatusBadgeComponent } from '../status-badge/status-badge.component';
import { ButtonComponent } from '../ui/button/button.component';
import { IconComponent } from '../ui/icon/icon.component';
import { LiveLogPanelComponent, ActivityLine } from '../live-log-panel/live-log-panel.component';
import { ElapsedTimer, ElapsedTimerService } from '../../core/services/elapsed-timer.service';
import { durationMsFromTimestamps, formatDurationMs } from '../../core/utils/duration';
import { providerLabel } from '../../core/utils/target-config';
import { isCoolifyProvider, isVercelProvider, providerLiveStatusText, providerSubtitle } from '../../core/utils/provider-branding';

@Component({
  selector: 'app-provider-status-card',
  standalone: true,
  imports: [DatePipe, StatusBadgeComponent, ButtonComponent, IconComponent, LiveLogPanelComponent],
  templateUrl: './provider-status-card.component.html',
  styleUrl: './provider-status-card.component.scss'
})
export class ProviderStatusCardComponent implements OnChanges, OnDestroy {
  @Input({ required: true }) roleLabel = '';
  @Input({ required: true }) providerName = '';
  @Input({ required: true }) status = 'pending';
  @Input() deployUrl?: string;
  @Input() startedAt?: string;
  @Input() completedAt?: string;
  @Input() checkedAt?: string;
  @Input() showProviderName = true;
  @Input() expanded = false;
  @Input() lines: ActivityLine[] = [];
  @Input() connecting = false;
  @Input() canRedeploy = false;
  @Input() redeploying = false;
  @Input() live = false;
  @Input() showOpenLink = true;

  @Output() expandedChange = new EventEmitter<boolean>();
  @Output() redeploy = new EventEmitter<void>();

  readonly elapsedMs;

  private readonly timer: ElapsedTimer;

  constructor(elapsedTimerService: ElapsedTimerService) {
    this.timer = elapsedTimerService.create();
    this.elapsedMs = this.timer.elapsedMs;
  }

  ngOnChanges(_changes: SimpleChanges): void {
    this.syncTimer();
  }

  ngOnDestroy(): void {
    this.timer.destroy();
  }

  get elapsedLabel(): string {
    if (this.redeploying || (this.status === 'in_progress' && this.startedAt)) {
      return this.formatWorkingLabel(this.elapsedMs());
    }

    const durationMs = durationMsFromTimestamps(this.startedAt, this.completedAt);
    if (durationMs !== null && (this.status === 'success' || this.status === 'failed')) {
      return `Finished in ${formatDurationMs(durationMs)}`;
    }

    return '';
  }

  get providerDisplayName(): string {
    return providerLabel(this.providerName);
  }

  get providerSubtitle(): string {
    return providerSubtitle(this.providerName);
  }

  get isVercel(): boolean {
    return isVercelProvider(this.providerName);
  }

  get isCoolify(): boolean {
    return isCoolifyProvider(this.providerName);
  }

  get statusLabel(): string {
    switch (this.status) {
      case 'in_progress':
        return 'Working…';
      case 'success':
        return 'Live';
      case 'failed':
        return 'Needs attention';
      default:
        return 'Waiting';
    }
  }

  get liveStatusText(): string {
    return providerLiveStatusText(this.providerName, this.status);
  }

  toggleExpanded(): void {
    this.expandedChange.emit(!this.expanded);
  }

  onRedeploy(event: Event): void {
    event.stopPropagation();
    this.redeploy.emit();
  }

  private syncTimer(): void {
    if (this.redeploying || (this.status === 'in_progress' && this.startedAt)) {
      this.timer.start(this.redeploying ? undefined : this.startedAt);
      return;
    }

    this.timer.stop();
  }

  private formatWorkingLabel(ms: number): string {
    const totalSeconds = Math.max(0, Math.floor(ms / 1000));
    if (totalSeconds < 60) {
      return `Working… ${totalSeconds}s`;
    }
    const minutes = Math.floor(totalSeconds / 60);
    const seconds = totalSeconds % 60;
    return seconds === 0 ? `Working… ${minutes}m` : `Working… ${minutes}m ${seconds}s`;
  }
}
