import { Component, computed } from '@angular/core';
import { NotificationPreferences, SessionStore } from '../../core/stores/session.store';

@Component({
  selector: 'app-notifications',
  standalone: true,
  templateUrl: './notifications.component.html',
  styleUrl: './notifications.component.scss'
})
export class NotificationsComponent {
  readonly prefs = computed(() => this.session.notificationPreferences() ?? defaultPreferences);

  constructor(private readonly session: SessionStore) {
    if (!this.session.notificationPreferences()) {
      this.session.setNotificationPreferences({ ...defaultPreferences });
    }
  }

  setPref(key: keyof NotificationPreferences, value: boolean): void {
    this.session.setNotificationPreferences({
      ...this.prefs(),
      [key]: value
    });
  }
}

const defaultPreferences: NotificationPreferences = {
  emailOnComplete: true,
  emailOnFailure: true
};
