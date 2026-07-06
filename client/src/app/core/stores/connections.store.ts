import { Injectable, inject, signal } from '@angular/core';
import { ApiService } from '../services/api.service';
import { CredentialSummary, ProviderInfo } from '../models/api.models';

@Injectable({ providedIn: 'root' })
export class ConnectionsStore {
  private readonly api = inject(ApiService);

  readonly credentials = signal<CredentialSummary[]>([]);
  readonly providers = signal<ProviderInfo[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  load(): void {
    this.loading.set(true);
    this.error.set(null);

    this.api.getCredentials().subscribe({
      next: (response) => {
        this.credentials.set(response.credentials);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(err?.error?.error?.message ?? 'Could not load connections.');
        this.loading.set(false);
      }
    });

    this.api.getProviders().subscribe({
      next: (response) => this.providers.set(response.providers)
    });
  }

  credentialFor(providerName: string): CredentialSummary | undefined {
    return this.credentials().find(c => c.providerName === providerName);
  }

  isConnected(providerName: string): boolean {
    return this.hasProvider(providerName);
  }

  displayName(providerName: string): string {
    return this.providers().find(p => p.name === providerName)?.displayName ?? providerName;
  }

  hasProvider(providerName: string): boolean {
    return this.credentials().some(c => c.providerName === providerName && c.isValid);
  }

  remove(id: string) {
    return this.api.deleteCredential(id);
  }

  addCredential(providerName: string, token: string, label?: string) {
    return this.api.addCredential(providerName, token, label);
  }
}
