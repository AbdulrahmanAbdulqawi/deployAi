import { Injectable, signal } from '@angular/core';

const AI_SETUP_KEY = 'deployai-ai-setup-enabled';

@Injectable({ providedIn: 'root' })
export class AiSetupPreferenceService {
  readonly enabled = signal<boolean>(this.readStored());

  setEnabled(value: boolean): void {
    this.enabled.set(value);
    localStorage.setItem(AI_SETUP_KEY, value ? 'true' : 'false');
  }

  toggle(): void {
    this.setEnabled(!this.enabled());
  }

  private readStored(): boolean {
    return localStorage.getItem(AI_SETUP_KEY) === 'true';
  }
}
