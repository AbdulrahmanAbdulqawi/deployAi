import {
  Component,
  EventEmitter,
  Input,
  OnDestroy,
  OnInit,
  Output,
  computed,
  inject,
  signal,
} from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../core/services/api.service';
import {
  DnsAuthorizationState,
  DnsProviderInfo,
  DnsZone,
} from '../../core/models/api.models';
import { InputComponent } from '../ui/input/input.component';
import { ButtonComponent } from '../ui/button/button.component';
import { DnsZoneListComponent } from './dns-zone-list.component';

/**
 * Connecting a DNS account. Used both in Settings and inline in the domain panel, so the offer can
 * appear where it pays off without the two copies drifting apart.
 *
 * The form is built from whatever the chosen provider says it needs, rather than assuming a single
 * token — Cloudflare wants one bearer token, Porkbun wants a key and a secret, and a form hardcoded
 * around one password box cannot express the second.
 *
 * Where the provider supports it, approval is the offered path and the key form is tucked behind
 * "enter keys instead". Creating, scoping, copying and pasting a key account for every failed
 * connection attempt this feature has seen, so the flow that removes all four is not something to
 * make the user go looking for.
 */
@Component({
  selector: 'app-dns-connect',
  standalone: true,
  imports: [CommonModule, FormsModule, InputComponent, ButtonComponent, DnsZoneListComponent],
  templateUrl: './dns-connect.component.html',
  styleUrl: './dns-connect.component.scss',
})
export class DnsConnectComponent implements OnInit, OnDestroy {
  /** Every three seconds: the user is watching this, and it is DeployAI's own endpoint being asked. */
  private static readonly PollIntervalMs = 3000;

  /** Compact drops the explainer, for the inline case where the surrounding text already says why. */
  @Input() compact = false;

  @Output() connected = new EventEmitter<DnsZone[]>();

  private readonly api = inject(ApiService);

  readonly providers = signal<DnsProviderInfo[]>([]);
  readonly selectedName = signal<string | null>(null);
  readonly values = signal<Record<string, string>>({});
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  readonly zones = signal<DnsZone[] | null>(null);

  /** The approval link, while one is outstanding. */
  readonly approvalUrl = signal<string | null>(null);
  readonly awaitingApproval = signal(false);
  readonly approvalMessage = signal<string | null>(null);

  /** Set once the user asks for the key form on a provider that could have been approved. */
  readonly showsKeyForm = signal(false);

  private pollTimer: ReturnType<typeof setTimeout> | null = null;

  readonly selected = computed(
    () => this.providers().find((p) => p.name === this.selectedName()) ?? null
  );

  /** Only offer a choice when there is one to make. */
  readonly showsProviderChoice = computed(() => this.providers().length > 1);

  /** Approval is offered instead of the key form, unless the user asked for the keys. */
  readonly offersApproval = computed(() => this.selected()?.supportsApproval === true);

  readonly showsFields = computed(() => !this.offersApproval() || this.showsKeyForm());

  readonly canSubmit = computed(() => {
    const provider = this.selected();
    if (!provider || this.saving()) {
      return false;
    }
    return provider.fields.every((f) => (this.values()[f.key] ?? '').trim().length > 0);
  });

  ngOnInit(): void {
    this.api.listDnsProviders().subscribe({
      next: (response) => {
        this.providers.set(response.providers);
        if (response.providers.length && !this.selectedName()) {
          this.select(response.providers[0].name);
        }
      },
      error: () => this.error.set('Could not load the list of DNS providers.'),
    });
  }

  ngOnDestroy(): void {
    this.stopPolling();
  }

  select(name: string): void {
    this.selectedName.set(name);
    // Never carry one provider's secret into another's form.
    this.values.set({});
    this.error.set(null);
    // Nor one provider's outstanding approval into another's screen.
    this.stopPolling();
    this.approvalUrl.set(null);
    this.awaitingApproval.set(false);
    this.approvalMessage.set(null);
    this.showsKeyForm.set(false);
  }

  /** Reveals the key form for a provider that could have been approved. */
  enterKeysInstead(): void {
    this.stopPolling();
    this.awaitingApproval.set(false);
    this.approvalUrl.set(null);
    this.approvalMessage.set(null);
    this.showsKeyForm.set(true);
  }

  /**
   * Asks the provider for access and sends the account holder to approve it.
   */
  approve(): void {
    const provider = this.selected();
    if (!provider || this.awaitingApproval()) {
      return;
    }

    this.error.set(null);
    this.awaitingApproval.set(true);
    this.approvalMessage.set('Asking ' + provider.displayName + ' for access…');

    this.api.beginDnsAuthorization({ providerName: provider.name }).subscribe({
      next: (start) => {
        this.approvalUrl.set(start.approvalUrl);
        this.approvalMessage.set(
          `Approve DeployAI in the ${provider.displayName} tab that just opened. This link is good for ten minutes.`
        );
        // Opened rather than only shown: the whole point is that the user does not have to go
        // and find anything. The link stays on screen for a popup blocker or a closed tab.
        window.open(start.approvalUrl, '_blank', 'noopener');
        this.scheduleNextPoll(start.requestToken, provider.name);
      },
      error: (response) => {
        this.awaitingApproval.set(false);
        this.approvalMessage.set(null);
        this.error.set(
          response?.error?.error?.message ??
            `Could not ask ${provider.displayName} for access. Try again, or enter your keys instead.`
        );
      },
    });
  }

  private scheduleNextPoll(requestToken: string, providerName: string): void {
    this.pollTimer = setTimeout(
      () => this.poll(requestToken, providerName),
      DnsConnectComponent.PollIntervalMs
    );
  }

  private poll(requestToken: string, providerName: string): void {
    this.api.pollDnsAuthorization(requestToken, { providerName }).subscribe({
      next: (result) => {
        if (result.state === DnsAuthorizationState.Approved) {
          // Already stored by the server: the secret comes back exactly once, so there is nothing
          // here to save and nothing that may be allowed to fail afterwards.
          this.stopPolling();
          this.awaitingApproval.set(false);
          this.approvalUrl.set(null);
          this.approvalMessage.set(null);
          this.zones.set(result.connection?.zones ?? []);
          this.connected.emit(result.connection?.zones ?? []);
          return;
        }

        // Denied and expired are answers, not variations of "not yet" — waiting on either would
        // spin forever on something that is never going to happen.
        if (
          result.state === DnsAuthorizationState.Denied ||
          result.state === DnsAuthorizationState.Expired
        ) {
          this.stopPolling();
          this.awaitingApproval.set(false);
          this.approvalUrl.set(null);
          this.approvalMessage.set(null);
          this.error.set(result.message);
          return;
        }

        // Pending, and Unreachable too: not reachable is not the same as not approved, so it
        // keeps waiting rather than throwing away an approval the user may have already given.
        this.scheduleNextPoll(requestToken, providerName);
      },
      // Same reasoning — a failed poll says nothing about the approval.
      error: () => this.scheduleNextPoll(requestToken, providerName),
    });
  }

  private stopPolling(): void {
    if (this.pollTimer !== null) {
      clearTimeout(this.pollTimer);
      this.pollTimer = null;
    }
  }

  setValue(key: string, value: string): void {
    this.values.update((current) => ({ ...current, [key]: value }));
  }

  connect(): void {
    const provider = this.selected();
    if (!provider || !this.canSubmit()) {
      return;
    }

    this.saving.set(true);
    this.error.set(null);

    this.api
      .createDnsConnection({ providerName: provider.name, fields: this.values() })
      .subscribe({
        next: (detail) => {
          // Never keep the secrets in memory after they have been persisted.
          this.values.set({});
          this.saving.set(false);
          this.zones.set(detail.zones);
          this.connected.emit(detail.zones);
        },
        error: (response) => {
          this.saving.set(false);
          // The server writes these messages because only it knows what the provider's codes mean —
          // "this token can change records but can't see your domains" rather than "invalid token".
          this.error.set(
            response?.error?.error?.message ?? 'Those details could not be checked. Try again.'
          );
        },
      });
  }
}
