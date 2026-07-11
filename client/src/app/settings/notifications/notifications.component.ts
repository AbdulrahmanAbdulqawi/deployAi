import { Component, OnInit, signal } from '@angular/core';
import { NotificationPreferencesResponse } from '../../core/models/api.models';
import { ApiService } from '../../core/services/api.service';
import { ToastService } from '../../shared/ui/toast/toast.service';

@Component({
  selector: 'app-notifications',
  standalone: true,
  templateUrl: './notifications.component.html',
  styleUrl: './notifications.component.scss'
})
export class NotificationsComponent implements OnInit {
  readonly prefs = signal<NotificationPreferencesResponse>(defaultPreferences);
  readonly loading = signal(true);
  readonly saving = signal(false);

  constructor(
    private readonly api: ApiService,
    private readonly toast: ToastService
  ) {}

  ngOnInit(): void {
    this.api.getNotificationPreferences().subscribe({
      next: (prefs) => {
        this.prefs.set(prefs);
        this.loading.set(false);
      },
      error: () => {
        this.loading.set(false);
        this.toast.error('Could not load notification preferences.');
      }
    });
  }

  setPref(key: 'emailOnComplete' | 'emailOnFailure', value: boolean): void {
    if (this.saving()) {
      return;
    }

    const next = {
      emailOnSuccess: key === 'emailOnComplete' ? value : this.prefs().emailOnSuccess,
      emailOnFailure: key === 'emailOnFailure' ? value : this.prefs().emailOnFailure
    };

    this.saving.set(true);
    this.api.updateNotificationPreferences(next).subscribe({
      next: (prefs) => {
        this.prefs.set(prefs);
        this.saving.set(false);
      },
      error: (err) => {
        this.saving.set(false);
        this.toast.error(err?.error?.error?.message ?? 'Could not save notification preferences.');
      }
    });
  }
}

const defaultPreferences: NotificationPreferencesResponse = {
  emailOnSuccess: true,
  emailOnFailure: true,
  emailOnComplete: true
};
