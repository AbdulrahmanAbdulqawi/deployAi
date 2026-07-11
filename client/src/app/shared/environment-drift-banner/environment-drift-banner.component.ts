import { Component, OnInit, input, output, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ApiService } from '../../core/services/api.service';
import { EnvironmentSyncState } from '../../core/models/api.models';
import { ButtonComponent } from '../ui/button/button.component';
import { IconComponent } from '../ui/icon/icon.component';
import { ToastService } from '../ui/toast/toast.service';

@Component({
  selector: 'app-environment-drift-banner',
  standalone: true,
  imports: [DatePipe, ButtonComponent, IconComponent],
  templateUrl: './environment-drift-banner.component.html',
  styleUrl: './environment-drift-banner.component.scss'
})
export class EnvironmentDriftBannerComponent implements OnInit {
  readonly projectId = input.required<string>();
  readonly hasSplitOrigin = input(false);
  readonly synced = output<void>();

  readonly environmentSync = signal<EnvironmentSyncState | null>(null);
  readonly loading = signal(true);
  readonly syncing = signal(false);

  constructor(
    private readonly api: ApiService,
    private readonly toast: ToastService
  ) {}

  ngOnInit(): void {
    this.loadSyncStatus();
  }

  get visible(): boolean {
    return this.hasSplitOrigin() && !this.loading() && (this.environmentSync()?.driftDetected ?? false);
  }

  reconnect(): void {
    if (this.syncing()) {
      return;
    }

    this.syncing.set(true);
    this.api.syncEnvironmentUrls(this.projectId()).subscribe({
      next: (result) => {
        this.syncing.set(false);
        if (result.skipped) {
          this.toast.show(result.skipReason ?? 'Reconnection was skipped.', 'info');
          return;
        }

        this.toast.success('Your site and app are reconnected.');
        this.loadSyncStatus();
        this.synced.emit();
      },
      error: (err) => {
        this.syncing.set(false);
        this.toast.error(err?.error?.error?.message ?? 'Could not reconnect your site and app.');
      }
    });
  }

  private loadSyncStatus(): void {
    this.loading.set(true);
    this.api.getEnvironmentSyncStatus(this.projectId()).subscribe({
      next: (status) => {
        if (!status.synced || !status.lastSyncedAt) {
          this.environmentSync.set(null);
          this.loading.set(false);
          return;
        }

        this.environmentSync.set({
          lastSyncedAt: status.lastSyncedAt,
          source: status.source ?? 'manual',
          success: status.success ?? false,
          driftDetected: status.driftDetected ?? false,
          resolvedWebsiteUrl: status.resolvedWebsiteUrl,
          resolvedApiUrl: status.resolvedApiUrl,
          verificationMessages: status.verificationMessages ?? [],
          driftDetails: status.driftDetails ?? []
        });
        this.loading.set(false);
      },
      error: () => {
        this.environmentSync.set(null);
        this.loading.set(false);
      }
    });
  }
}
