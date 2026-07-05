import { Component, OnInit, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { ApiService } from '../core/services/api.service';
import { CredentialSummary, ProviderInfo } from '../core/models/api.models';

@Component({
  selector: 'app-settings',
  standalone: true,
  imports: [FormsModule],
  templateUrl: './settings.component.html',
  styleUrl: './settings.component.scss'
})
export class SettingsComponent implements OnInit {
  readonly credentials = signal<CredentialSummary[]>([]);
  readonly providers = signal<ProviderInfo[]>([]);
  readonly saving = signal(false);
  readonly connectingOAuth = signal(false);
  readonly connectingRailwayOAuth = signal(false);
  readonly showAdvanced = signal(false);
  readonly showAdvancedRailway = signal(false);
  readonly message = signal<string | null>(null);
  readonly isError = signal(false);

  token = '';
  label = 'Personal';
  railwayToken = '';
  railwayLabel = 'Personal';
  readonly savingRailway = signal(false);
  private returnUrl: string | null = null;

  constructor(
    private readonly api: ApiService,
    private readonly router: Router,
    private readonly route: ActivatedRoute
  ) {}

  ngOnInit(): void {
    this.returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
    const vercelStatus = this.route.snapshot.queryParamMap.get('vercel');
    if (vercelStatus === 'connected') {
      this.message.set('Connected to Vercel');
      this.isError.set(false);
      this.navigateBackIfNeeded();
    } else if (vercelStatus === 'error' || vercelStatus === 'invalid_state') {
      this.message.set('Vercel connection did not go through. Try again.');
      this.isError.set(true);
    }

    const railwayStatus = this.route.snapshot.queryParamMap.get('railway');
    if (railwayStatus === 'connected') {
      this.message.set('Connected to Railway');
      this.isError.set(false);
      this.navigateBackIfNeeded();
    } else if (railwayStatus === 'error' || railwayStatus === 'invalid_state') {
      this.message.set('Railway connection did not go through. Try again.');
      this.isError.set(true);
    }

    this.load();
    this.api.getProviders().subscribe({
      next: (response) => this.providers.set(response.providers)
    });
  }

  load(): void {
    this.api.getCredentials().subscribe({
      next: (response) => this.credentials.set(response.credentials)
    });
  }

  displayName(providerName: string): string {
    return this.providers().find(p => p.name === providerName)?.displayName ?? providerName;
  }

  connectWithVercel(): void {
    this.connectingOAuth.set(true);
    this.message.set(null);
    const returnPath = this.returnUrl ?? '/settings';
    const step = this.route.snapshot.queryParamMap.get('step');
    const returnUrl = step ? `${returnPath}?step=${step}` : returnPath;

    this.api.getVercelLoginUrl(returnUrl).subscribe({
      next: (response) => {
        window.location.href = response.url;
      },
      error: (err) => {
        this.message.set(err?.error?.error?.message ?? 'Could not start Vercel connection.');
        this.isError.set(true);
        this.connectingOAuth.set(false);
      }
    });
  }

  connectWithToken(): void {
    this.saving.set(true);
    this.message.set(null);
    this.api.addCredential('vercel', this.token, this.label).subscribe({
      next: () => {
        this.token = '';
        this.message.set('Connected to Vercel');
        this.isError.set(false);
        this.saving.set(false);
        this.load();
        this.navigateBackIfNeeded();
      },
      error: (err) => {
        this.message.set(err?.error?.error?.message ?? 'Connection did not go through. Check your Vercel access key and try again.');
        this.isError.set(true);
        this.saving.set(false);
      }
    });
  }

  connectWithRailway(): void {
    this.connectingRailwayOAuth.set(true);
    this.message.set(null);
    const returnPath = this.returnUrl ?? '/settings';
    const step = this.route.snapshot.queryParamMap.get('step');
    const returnUrl = step ? `${returnPath}?step=${step}` : returnPath;

    this.api.getRailwayLoginUrl(returnUrl).subscribe({
      next: (response) => {
        window.location.href = response.url;
      },
      error: (err) => {
        this.message.set(err?.error?.error?.message ?? 'Could not start Railway connection.');
        this.isError.set(true);
        this.connectingRailwayOAuth.set(false);
      }
    });
  }

  connectRailwayWithToken(): void {
    this.savingRailway.set(true);
    this.message.set(null);
    this.api.addCredential('railway', this.railwayToken, this.railwayLabel).subscribe({
      next: () => {
        this.railwayToken = '';
        this.message.set('Connected to Railway');
        this.isError.set(false);
        this.savingRailway.set(false);
        this.load();
        this.navigateBackIfNeeded();
      },
      error: (err) => {
        this.message.set(err?.error?.error?.message ?? 'Connection did not go through. Check your Railway access key and try again.');
        this.isError.set(true);
        this.savingRailway.set(false);
      }
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

  remove(id: string): void {
    this.api.deleteCredential(id).subscribe({
      next: () => {
        this.message.set('Connection removed');
        this.isError.set(false);
        this.load();
      },
      error: (err) => {
        this.message.set(err?.error?.error?.message ?? 'Could not remove that connection.');
        this.isError.set(true);
      }
    });
  }
}
