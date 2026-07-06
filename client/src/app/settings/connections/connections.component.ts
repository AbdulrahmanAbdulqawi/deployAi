import { Component, OnInit, computed, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { ApiService } from '../../core/services/api.service';
import { CredentialSummary } from '../../core/models/api.models';
import { ConnectionsStore } from '../../core/stores/connections.store';
import { ButtonComponent } from '../../shared/ui/button/button.component';
import { InputComponent } from '../../shared/ui/input/input.component';
import { ToastService } from '../../shared/ui/toast/toast.service';
import { ConfirmDialogComponent } from '../../shared/confirm-dialog/confirm-dialog.component';
import { StatusBadgeComponent } from '../../shared/status-badge/status-badge.component';

type ProviderKey = 'vercel' | 'railway';

@Component({
  selector: 'app-connections',
  standalone: true,
  imports: [FormsModule, ButtonComponent, InputComponent, ConfirmDialogComponent, StatusBadgeComponent],
  templateUrl: './connections.component.html',
  styleUrl: './connections.component.scss'
})
export class ConnectionsComponent implements OnInit {
  readonly saving = signal(false);
  readonly connectingOAuth = signal(false);
  readonly connectingRailwayOAuth = signal(false);
  readonly showAdvanced = signal(false);
  readonly showAdvancedRailway = signal(false);
  readonly savingRailway = signal(false);
  readonly confirmRemoveId = signal<string | null>(null);
  readonly providerHealth = signal<{ name: string; status: string; message?: string | null }[]>([]);

  readonly healthAlert = computed(() =>
    this.providerHealth().find(p => p.status === 'degraded' || p.status === 'incident')
  );

  readonly vercelCredential = computed(() => this.store.credentialFor('vercel'));
  readonly railwayCredential = computed(() => this.store.credentialFor('railway'));
  readonly vercelConnected = computed(() => this.store.isConnected('vercel'));
  readonly railwayConnected = computed(() => this.store.isConnected('railway'));

  token = '';
  label = 'Personal';
  railwayToken = '';
  railwayLabel = 'Personal';
  private returnUrl: string | null = null;

  constructor(
    readonly store: ConnectionsStore,
    private readonly api: ApiService,
    private readonly router: Router,
    private readonly route: ActivatedRoute,
    private readonly toast: ToastService
  ) {}

  ngOnInit(): void {
    this.returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
    this.api.getProviderHealth().subscribe({
      next: (response) => this.providerHealth.set(response.providers),
      error: () => this.providerHealth.set([])
    });
    const vercelStatus = this.route.snapshot.queryParamMap.get('vercel');
    if (vercelStatus === 'connected') {
      this.toast.success('Connected to Vercel');
      this.navigateBackIfNeeded();
    } else if (vercelStatus === 'error' || vercelStatus === 'invalid_state') {
      this.toast.error('Vercel connection did not go through. Try again.');
    }

    const railwayStatus = this.route.snapshot.queryParamMap.get('railway');
    if (railwayStatus === 'connected') {
      this.toast.success('Connected to Railway');
      this.navigateBackIfNeeded();
    } else if (railwayStatus === 'error' || railwayStatus === 'invalid_state') {
      this.toast.error('Railway connection did not go through. Try again.');
    }

    this.store.load();
  }

  introMessage(): string {
    if (this.vercelConnected() && this.railwayConnected()) {
      return 'Vercel and Railway are connected. Reconnect anytime to refresh access.';
    }
    if (this.vercelConnected()) {
      return 'Vercel is connected. Connect Railway to deploy databases and backends.';
    }
    if (this.railwayConnected()) {
      return 'Railway is connected. Connect Vercel to deploy frontends.';
    }
    return 'Connect Vercel and Railway to deploy your apps.';
  }

  statusFor(credential: CredentialSummary | undefined): { status: string; label: string } {
    if (!credential) {
      return { status: 'idle', label: 'Not connected' };
    }
    if (credential.isValid) {
      return { status: 'success', label: 'Connected' };
    }
    return { status: 'failed', label: 'Invalid' };
  }

  oauthLabel(hasCredential: boolean, connected: boolean): string {
    return connected || hasCredential ? 'Reconnect' : 'Connect';
  }

  oauthIcon(hasCredential: boolean, connected: boolean): 'refresh' | 'link' {
    return connected || hasCredential ? 'refresh' : 'link';
  }

  tokenToggleLabel(hasCredential: boolean, open: boolean): string {
    if (open) {
      return 'Hide token';
    }
    return hasCredential ? 'Advanced: update token' : 'Advanced: use token';
  }

  tokenSaveLabel(hasCredential: boolean): string {
    return hasCredential ? 'Update' : 'Save';
  }

  formatValidated(at?: string): string | null {
    if (!at) {
      return null;
    }
    return new Intl.DateTimeFormat(undefined, {
      dateStyle: 'medium',
      timeStyle: 'short'
    }).format(new Date(at));
  }

  connectWithVercel(): void {
    this.connectingOAuth.set(true);
    const returnPath = this.returnUrl ?? '/settings/connections';
    const step = this.route.snapshot.queryParamMap.get('step');
    const returnUrl = step ? `${returnPath}?step=${step}` : returnPath;

    this.api.getVercelLoginUrl(returnUrl).subscribe({
      next: (response) => {
        window.location.href = response.url;
      },
      error: (err) => {
        this.toast.error(err?.error?.error?.message ?? 'Could not start Vercel connection.');
        this.connectingOAuth.set(false);
      }
    });
  }

  connectWithToken(): void {
    const updating = !!this.vercelCredential();
    this.saving.set(true);
    this.store.addCredential('vercel', this.token, this.label).subscribe({
      next: () => {
        this.token = '';
        this.toast.success(updating ? 'Vercel token updated' : 'Connected to Vercel');
        this.saving.set(false);
        this.showAdvanced.set(false);
        this.store.load();
        this.navigateBackIfNeeded();
      },
      error: (err) => {
        this.toast.error(err?.error?.error?.message ?? 'Connection did not go through.');
        this.saving.set(false);
      }
    });
  }

  connectWithRailway(): void {
    this.connectingRailwayOAuth.set(true);
    const returnPath = this.returnUrl ?? '/settings/connections';
    const step = this.route.snapshot.queryParamMap.get('step');
    const returnUrl = step ? `${returnPath}?step=${step}` : returnPath;

    this.api.getRailwayLoginUrl(returnUrl).subscribe({
      next: (response) => {
        window.location.href = response.url;
      },
      error: (err) => {
        this.toast.error(err?.error?.error?.message ?? 'Could not start Railway connection.');
        this.connectingRailwayOAuth.set(false);
      }
    });
  }

  connectRailwayWithToken(): void {
    const updating = !!this.railwayCredential();
    this.savingRailway.set(true);
    this.store.addCredential('railway', this.railwayToken, this.railwayLabel).subscribe({
      next: () => {
        this.railwayToken = '';
        this.toast.success(updating ? 'Railway token updated' : 'Connected to Railway');
        this.savingRailway.set(false);
        this.showAdvancedRailway.set(false);
        this.store.load();
        this.navigateBackIfNeeded();
      },
      error: (err) => {
        this.toast.error(err?.error?.error?.message ?? 'Connection did not go through.');
        this.savingRailway.set(false);
      }
    });
  }

  toggleToken(provider: ProviderKey): void {
    if (provider === 'vercel') {
      this.showAdvanced.update(open => !open);
      return;
    }
    this.showAdvancedRailway.update(open => !open);
  }

  private navigateBackIfNeeded(): void {
    if (this.returnUrl) {
      const step = this.route.snapshot.queryParamMap.get('step');
      void this.router.navigate([this.returnUrl], {
        queryParams: step ? { step } : undefined
      });
    }
  }

  askRemove(id: string): void {
    this.confirmRemoveId.set(id);
  }

  confirmRemove(): void {
    const id = this.confirmRemoveId();
    if (!id) {
      return;
    }

    this.store.remove(id).subscribe({
      next: () => {
        this.toast.success('Connection removed');
        this.confirmRemoveId.set(null);
        this.store.load();
      },
      error: (err) => {
        this.toast.error(err?.error?.error?.message ?? 'Could not remove that connection.');
        this.confirmRemoveId.set(null);
      }
    });
  }
}
