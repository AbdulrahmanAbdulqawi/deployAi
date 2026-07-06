import { Injectable, signal } from '@angular/core';

const ACCESS_TOKEN_KEY = 'deployai_access_token';

export interface SessionUser {
  githubConnected: boolean;
}

export interface NotificationPreferences {
  emailOnComplete: boolean;
  emailOnFailure: boolean;
}

@Injectable({ providedIn: 'root' })
export class SessionStore {
  readonly isAuthenticated = signal(!!localStorage.getItem(ACCESS_TOKEN_KEY));
  readonly user = signal<SessionUser | null>({ githubConnected: true });
  readonly notificationPreferences = signal<NotificationPreferences | null>(null);

  setAuthenticated(value: boolean): void {
    this.isAuthenticated.set(value);
    if (value && !this.user()) {
      this.user.set({ githubConnected: true });
    }
  }

  setNotificationPreferences(prefs: NotificationPreferences): void {
    this.notificationPreferences.set(prefs);
  }

  clear(): void {
    this.isAuthenticated.set(false);
    this.user.set(null);
    this.notificationPreferences.set(null);
  }
}
