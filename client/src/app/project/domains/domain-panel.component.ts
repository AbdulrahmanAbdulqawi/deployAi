import { Component, Input, OnInit, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../core/services/api.service';
import {
  DomainOptions,
  DomainSource,
  DomainStatus,
  DnsZoneUsability,
  ProjectDomain,
} from '../../core/models/api.models';
import { DnsConnectComponent } from '../../shared/dns-connect/dns-connect.component';
import { DomainBuyComponent } from './domain-buy.component';

/** How a status should read to someone who has never configured DNS. */
interface StatusPresentation {
  label: string;
  tone: 'working' | 'live' | 'attention' | 'unknown';
}

@Component({
  selector: 'app-domain-panel',
  standalone: true,
  imports: [CommonModule, FormsModule, DnsConnectComponent, DomainBuyComponent],
  templateUrl: './domain-panel.component.html',
  styleUrl: './domain-panel.component.scss',
})
export class DomainPanelComponent implements OnInit {
  @Input({ required: true }) projectId!: string;

  /** The part of the project a new domain attaches to — one deployed application. */
  @Input({ required: true }) deployTargetId!: string;

  private readonly api = inject(ApiService);

  readonly domains = signal<ProjectDomain[]>([]);
  readonly options = signal<DomainOptions | null>(null);
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly copied = signal<string | null>(null);

  typedDomain = '';

  /**
   * Keyed on real usability, not on a writable flag. The previous `canWrite` came from a field
   * Cloudflare does not populate for API tokens and defaulted to true when absent, so a zone whose
   * registrar had not delegated yet made this panel promise DeployAI would handle the DNS — and
   * then silently not.
   */
  readonly canWriteRecords = computed(() =>
    (this.options()?.zones ?? []).some((zone) => zone.usability === DnsZoneUsability.Ready)
  );

  /**
   * Whether the answer about zones has arrived — either a list, or a failure to fetch one.
   *
   * Not the same question as "are there no zones", and the offer below depends on the difference:
   * until this is true the panel has simply not heard yet, and treating that as "no DNS account"
   * flashes the whole connect offer at someone who already has one before snatching it away.
   * A failed lookup counts as heard, because the alternative — hiding the offer whenever that call
   * fails — sends a user with no DNS account off to edit records by hand, which is worse than
   * offering a connection to someone who already has one.
   */
  readonly zonesAreKnown = signal(false);

  ngOnInit(): void {
    this.refresh();
    this.api.getDomainOptions(this.projectId).subscribe({
      next: (options) => {
        this.options.set(options);
        this.zonesAreKnown.set(true);
      },
      // Suggestions are a convenience; failing to load them must not block adding a domain.
      error: () => {
        this.options.set(null);
        this.zonesAreKnown.set(true);
      },
    });
  }

  /** Re-read the options so the panel switches from "add this record yourself" to automatic. */
  onDnsConnected(): void {
    this.api.getDomainOptions(this.projectId).subscribe({
      next: (options) => this.options.set(options),
      error: () => undefined,
    });
  }

  /** A bought domain is already attached server-side; reload so it appears moving through setup. */
  onDomainBought(): void {
    this.refresh();
    this.onDnsConnected();
  }

  refresh(): void {
    this.api.getDomains(this.projectId).subscribe({
      next: (domains) => this.domains.set(domains),
      error: () => this.error.set('Could not load this project’s domains.'),
    });
  }

  useSuggested(): void {
    const suggested = this.options()?.suggestedSubdomain;
    if (suggested) {
      this.typedDomain = suggested;
      this.add();
    }
  }

  add(): void {
    const domain = this.typedDomain.trim();
    if (!domain || this.busy()) {
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    this.api
      .attachDomain(this.projectId, { deployTargetId: this.deployTargetId, domain })
      .subscribe({
        next: (added) => {
          this.domains.update((current) => [...current, added]);
          this.typedDomain = '';
          this.busy.set(false);
        },
        error: (response) => {
          this.error.set(response?.error?.error?.message ?? 'That domain could not be added.');
          this.busy.set(false);
        },
      });
  }

  recheck(domain: ProjectDomain): void {
    this.api.recheckDomain(this.projectId, domain.id).subscribe({
      next: (updated) =>
        this.domains.update((current) =>
          current.map((existing) => (existing.id === updated.id ? updated : existing))
        ),
      error: () => this.error.set('Could not start the check again.'),
    });
  }

  remove(domain: ProjectDomain): void {
    this.api.removeDomain(this.projectId, domain.id).subscribe({
      next: () =>
        this.domains.update((current) => current.filter((existing) => existing.id !== domain.id)),
      error: () => this.error.set('Could not remove that domain.'),
    });
  }

  copy(value: string): void {
    void navigator.clipboard?.writeText(value);
    this.copied.set(value);
    setTimeout(() => this.copied.set(null), 2000);
  }

  /**
   * Deliberately never says "failed" for the two unverifiable states. Those mean nothing could be
   * checked, which is not evidence the user did anything wrong — and a red cross would send them
   * to change a record that may well be correct.
   */
  presentation(status: DomainStatus): StatusPresentation {
    switch (status) {
      case DomainStatus.Active:
        return { label: 'Live and secure', tone: 'live' };
      case DomainStatus.Pending:
        return { label: 'Getting ready', tone: 'working' };
      case DomainStatus.DnsPending:
        return { label: 'Waiting for the domain to point here', tone: 'working' };
      case DomainStatus.DnsVerified:
        return { label: 'Domain found — setting it up', tone: 'working' };
      case DomainStatus.Assigned:
        return { label: 'Restarting the app', tone: 'working' };
      case DomainStatus.CertificatePending:
        return { label: 'Getting the security certificate', tone: 'working' };
      case DomainStatus.DnsFailed:
        return { label: 'Needs a change at your domain provider', tone: 'attention' };
      case DomainStatus.CertificateFailed:
        return { label: 'The certificate could not be issued', tone: 'attention' };
      case DomainStatus.Conflicted:
        return { label: 'Already used by something else', tone: 'attention' };
      case DomainStatus.DnsUnverifiable:
      case DomainStatus.CertificateUnverifiable:
        return { label: 'We could not check', tone: 'unknown' };
      default:
        return { label: 'Removed', tone: 'unknown' };
    }
  }

  /** Instructions are pointless for a name DeployAI already controls or already wrote. */
  showsInstruction(domain: ProjectDomain): boolean {
    return (
      !!domain.instruction &&
      domain.source === DomainSource.UserProvided &&
      domain.status !== DomainStatus.Active
    );
  }

  isSettled(domain: ProjectDomain): boolean {
    return this.presentation(domain.status).tone !== 'working';
  }

  trackById(_: number, domain: ProjectDomain): string {
    return domain.id;
  }
}
