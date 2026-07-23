import { Component, OnInit, computed, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { ApiService } from '../../core/services/api.service';
import { CredentialSummary, ProviderName, StorageConnectionSummary } from '../../core/models/api.models';
import { ConnectionsStore } from '../../core/stores/connections.store';
import { ButtonComponent } from '../../shared/ui/button/button.component';
import { InputComponent } from '../../shared/ui/input/input.component';
import { ToastService } from '../../shared/ui/toast/toast.service';
import { ConfirmService } from '../../shared/ui/confirm/confirm.service';
import { StatusBadgeComponent } from '../../shared/status-badge/status-badge.component';

type ProviderKey = ProviderName.Vercel | ProviderName.Railway | ProviderName.Coolify;

@Component({
  selector: 'app-connections',
  standalone: true,
  imports: [FormsModule, ButtonComponent, InputComponent, StatusBadgeComponent],
  templateUrl: './connections.component.html',
  styleUrl: './connections.component.scss'
})
export class ConnectionsComponent implements OnInit {
  readonly ProviderName = ProviderName;
  readonly saving = signal(false);
  readonly connectingOAuth = signal(false);
  readonly connectingRailwayOAuth = signal(false);
  readonly showAdvanced = signal(false);
  readonly showAdvancedRailway = signal(false);
  readonly showAdvancedCoolify = signal(false);
  readonly savingRailway = signal(false);
  readonly savingCoolify = signal(false);
  readonly showAdvancedStorage = signal(false);
  readonly savingStorage = signal(false);
  readonly storageConnections = signal<StorageConnectionSummary[]>([]);
  /** Vercel and Railway are the legacy path now — kept, but folded away by default. */
  readonly showLegacyProviders = signal(false);
  readonly providerHealth = signal<{ name: string; status: string; message?: string | null }[]>([]);

  readonly healthAlert = computed(() =>
    this.providerHealth().find(p => p.status === 'degraded' || p.status === 'incident')
  );

  readonly vercelCredential = computed(() => this.store.credentialFor(ProviderName.Vercel));
  readonly railwayCredential = computed(() => this.store.credentialFor(ProviderName.Railway));
  readonly coolifyCredentials = computed(() =>
    this.store.credentials().filter(c => c.providerName === ProviderName.Coolify)
  );
  readonly vercelConnected = computed(() => this.store.isConnected(ProviderName.Vercel));
  readonly railwayConnected = computed(() => this.store.isConnected(ProviderName.Railway));
  readonly coolifyConnected = computed(() => this.store.isConnected(ProviderName.Coolify));

  token = '';
  label = 'Personal';
  railwayToken = '';
  railwayLabel = 'Personal';
  coolifyInstanceUrl = '';
  coolifyToken = '';
  coolifyLabel = 'Homelab';
  storageEndpoint = '';
  storageRegion = '';
  storageAccessKey = '';
  storageSecretKey = '';
  storageLabel = 'Hetzner';
  private returnUrl: string | null = null;

  constructor(
    readonly store: ConnectionsStore,
    private readonly api: ApiService,
    private readonly router: Router,
    private readonly route: ActivatedRoute,
    private readonly toast: ToastService,
    private readonly confirm: ConfirmService
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
    this.loadStorageConnections();
  }

  loadStorageConnections(): void {
    this.api.listStorageConnections().subscribe({
      next: (response) => this.storageConnections.set(response.connections),
      error: () => this.storageConnections.set([])
    });
  }

  toggleStorageForm(): void {
    this.showAdvancedStorage.update(open => !open);
  }

  toggleLegacyProviders(): void {
    this.showLegacyProviders.update(open => !open);
  }

  connectStorage(): void {
    this.savingStorage.set(true);
    this.api.createStorageConnection({
      endpoint: this.storageEndpoint,
      region: this.storageRegion,
      accessKey: this.storageAccessKey,
      secretKey: this.storageSecretKey,
      label: this.storageLabel
    }).subscribe({
      next: () => {
        this.savingStorage.set(false);
        this.showAdvancedStorage.set(false);
        // Never keep the secret in memory after it has been persisted.
        this.storageAccessKey = '';
        this.storageSecretKey = '';
        this.toast.success('Object storage connected');
        this.loadStorageConnections();
      },
      error: (err) => {
        this.savingStorage.set(false);
        this.toast.error(err?.error?.error?.message ?? 'Could not connect that storage account.');
      }
    });
  }

  async removeStorage(id: string): Promise<void> {
    const confirmed = await this.confirm.ask({
      title: 'Remove storage connection?',
      message: 'Apps wired to this connection will fall back to local disk on their next deploy.',
      confirmLabel: 'Remove'
    });

    if (!confirmed) {
      return;
    }

    this.api.deleteStorageConnection(id).subscribe({
      next: () => {
        this.toast.success('Storage connection removed');
        this.loadStorageConnections();
      },
      error: (err) => this.toast.error(err?.error?.error?.message ?? 'Could not remove that connection.')
    });
  }

  introMessage(): string {
    // Coolify first: it is the default deploy target, so the copy should not open by
    // offering the platforms we now treat as the legacy path.
    const connected = [
      this.coolifyConnected() ? 'Coolify' : null,
      this.storageConnections().length > 0 ? 'object storage' : null,
      this.vercelConnected() ? 'Vercel' : null,
      this.railwayConnected() ? 'Railway' : null
    ].filter((name): name is string => name !== null);

    if (connected.length === 0) {
      return 'Connect your Coolify server to deploy, and object storage if your app stores files.';
    }

    return `${connected.join(', ').replace(/, ([^,]*)$/, ' and $1')} connected.`;
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
    this.store.addCredential(ProviderName.Vercel, this.token, this.label).subscribe({
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
    this.store.addCredential(ProviderName.Railway, this.railwayToken, this.railwayLabel).subscribe({
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

  connectCoolify(): void {
    this.savingCoolify.set(true);
    this.store
      .addCredential(ProviderName.Coolify, this.coolifyToken, this.coolifyLabel, this.coolifyInstanceUrl)
      .subscribe({
        next: () => {
          this.coolifyToken = '';
          this.toast.success('Connected to Coolify');
          this.savingCoolify.set(false);
          this.showAdvancedCoolify.set(false);
          this.store.load();
          this.navigateBackIfNeeded();
        },
        error: (err) => {
          this.toast.error(err?.error?.error?.message ?? 'Connection did not go through.');
          this.savingCoolify.set(false);
        }
      });
  }

  toggleToken(provider: ProviderKey): void {
    if (provider === ProviderName.Vercel) {
      this.showAdvanced.update(open => !open);
      return;
    }
    if (provider === ProviderName.Railway) {
      this.showAdvancedRailway.update(open => !open);
      return;
    }

    this.showAdvancedCoolify.update(open => {
      const next = !open;
      if (next) {
        const cred = this.coolifyCredentials()[0];
        if (cred?.instanceUrl) {
          this.coolifyInstanceUrl = cred.instanceUrl;
        }
        if (cred?.label) {
          this.coolifyLabel = cred.label;
        }
      }
      return next;
    });
  }

  private navigateBackIfNeeded(): void {
    if (this.returnUrl) {
      const step = this.route.snapshot.queryParamMap.get('step');
      void this.router.navigate([this.returnUrl], {
        queryParams: step ? { step } : undefined
      });
    }
  }

  async askRemove(id: string): Promise<void> {
    const confirmed = await this.confirm.ask({
      title: 'Remove connection?',
      message: 'DeployAI will stop using this connection.',
      confirmLabel: 'Remove',
      destructive: true
    });
    if (!confirmed) {
      return;
    }

    this.store.remove(id).subscribe({
      next: () => {
        this.toast.success('Connection removed');
        this.store.load();
      },
      error: (err) => {
        this.toast.error(err?.error?.error?.message ?? 'Could not remove that connection.');
      }
    });
  }
}
