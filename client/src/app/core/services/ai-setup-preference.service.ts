import { Injectable, inject, signal } from '@angular/core';
import { catchError, of } from 'rxjs';
import { ApiService } from './api.service';

const AI_SETUP_KEY = 'deployai-ai-setup-enabled';

@Injectable({ providedIn: 'root' })
export class AiSetupPreferenceService {
  private readonly api = inject(ApiService);

  readonly enabled = signal<boolean>(this.readStored());

  setEnabled(value: boolean): void {
    this.enabled.set(value);
    localStorage.setItem(AI_SETUP_KEY, value ? 'true' : 'false');
  }

  toggle(): void {
    this.setEnabled(!this.enabled());
  }

  /** Load the server-persisted preference for a project, falling back to the
   * locally stored value when the server has no explicit choice recorded. */
  hydrateForProject(projectId: string): void {
    this.api
      .getAiSetupPreference(projectId)
      .pipe(catchError(() => of({ enabled: null as boolean | null })))
      .subscribe((result) => {
        if (result.enabled !== null && result.enabled !== undefined) {
          this.enabled.set(result.enabled);
        }
      });
  }

  /** Update the preference locally and persist it to the project on the server. */
  setEnabledForProject(projectId: string, value: boolean): void {
    this.setEnabled(value);
    this.api
      .setAiSetupPreference(projectId, value)
      .pipe(catchError(() => of(null)))
      .subscribe();
  }

  private readStored(): boolean {
    return localStorage.getItem(AI_SETUP_KEY) === 'true';
  }
}
