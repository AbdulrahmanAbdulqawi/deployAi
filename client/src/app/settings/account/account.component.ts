import { Component, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AccountSecret } from '../../core/models/api.models';
import { ApiService } from '../../core/services/api.service';
import { AuthService } from '../../core/services/auth.service';
import { SessionStore } from '../../core/stores/session.store';
import { ButtonComponent } from '../../shared/ui/button/button.component';
import { StatusBadgeComponent } from '../../shared/status-badge/status-badge.component';

@Component({
  selector: 'app-account',
  standalone: true,
  imports: [FormsModule, ButtonComponent, StatusBadgeComponent],
  templateUrl: './account.component.html',
  styleUrl: './account.component.scss'
})
export class AccountComponent implements OnInit {
  readonly loading = signal(false);
  readonly saving = signal(false);
  readonly saved = signal(false);
  readonly error = signal<string | null>(null);

  /** True once a key is stored. The key itself never comes back, so this is all the form knows. */
  readonly privateKeyStored = signal(false);

  appId = '';
  privateKey = '';

  constructor(
    readonly session: SessionStore,
    private readonly api: ApiService,
    private readonly auth: AuthService
  ) {}

  ngOnInit(): void {
    this.load();
  }

  private load(): void {
    this.loading.set(true);
    this.api.getAccountSecrets().subscribe({
      next: (response) => {
        this.apply(response.secrets);
        this.loading.set(false);
      },
      error: () => {
        this.error.set('Could not load your saved credentials.');
        this.loading.set(false);
      }
    });
  }

  private apply(secrets: AccountSecret[]): void {
    const appId = secrets.find(s => s.name === 'GITHUB_APP_ID');
    const key = secrets.find(s => s.name === 'GITHUB_APP_PRIVATE_KEY');
    this.appId = appId?.value ?? '';
    this.privateKeyStored.set(key?.isSet ?? false);
    this.privateKey = '';
  }

  save(): void {
    this.saving.set(true);
    this.saved.set(false);
    this.error.set(null);

    // The key field is left blank when one is already stored, and a blank field must not wipe it —
    // so it is only sent when the user actually typed something. Clearing is explicit, below.
    const payload: { gitHubAppId?: string; gitHubAppPrivateKey?: string } = {
      gitHubAppId: this.appId.trim()
    };
    if (this.privateKey.trim()) {
      payload.gitHubAppPrivateKey = this.privateKey.trim();
    }

    this.api.saveAccountSecrets(payload).subscribe({
      next: (response) => {
        this.apply(response.secrets);
        this.saving.set(false);
        this.saved.set(true);
      },
      error: () => {
        this.error.set('Could not save your credentials.');
        this.saving.set(false);
      }
    });
  }

  forgetPrivateKey(): void {
    this.saving.set(true);
    this.api.saveAccountSecrets({ gitHubAppPrivateKey: '' }).subscribe({
      next: (response) => {
        this.apply(response.secrets);
        this.saving.set(false);
      },
      error: () => {
        this.error.set('Could not remove the private key.');
        this.saving.set(false);
      }
    });
  }

  signOut(): void {
    this.auth.logout();
  }
}
