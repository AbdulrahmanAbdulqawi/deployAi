import { Component, EventEmitter, Input, Output, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../core/services/api.service';
import { DnsZone, DnsZoneUsability } from '../../core/models/api.models';
import { InputComponent } from '../ui/input/input.component';
import { ButtonComponent } from '../ui/button/button.component';

/**
 * Pasting a DNS provider token. Used both in Settings and inline in the domain panel, so the offer
 * can appear where it pays off without the two copies drifting apart.
 */
@Component({
  selector: 'app-dns-connect',
  standalone: true,
  imports: [CommonModule, FormsModule, InputComponent, ButtonComponent],
  templateUrl: './dns-connect.component.html',
  styleUrl: './dns-connect.component.scss',
})
export class DnsConnectComponent {
  /** Compact drops the explainer, for the inline case where the surrounding text already says why. */
  @Input() compact = false;

  @Output() connected = new EventEmitter<DnsZone[]>();

  private readonly api = inject(ApiService);

  readonly token = signal('');
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  readonly zones = signal<DnsZone[] | null>(null);

  readonly Usability = DnsZoneUsability;

  connect(): void {
    const token = this.token().trim();
    if (!token || this.saving()) {
      return;
    }

    this.saving.set(true);
    this.error.set(null);

    this.api.createDnsConnection({ token }).subscribe({
      next: (detail) => {
        // Never keep the secret in memory after it has been persisted.
        this.token.set('');
        this.saving.set(false);
        this.zones.set(detail.zones);
        this.connected.emit(detail.zones);
      },
      error: (response) => {
        this.saving.set(false);
        // The server writes these messages because only it knows what the provider's codes mean —
        // "this token can change records but can't see your domains" rather than "invalid token".
        this.error.set(
          response?.error?.error?.message ?? 'That token could not be checked. Try again.'
        );
      },
    });
  }

  readyZones(): DnsZone[] {
    return (this.zones() ?? []).filter((z) => z.usability === DnsZoneUsability.Ready);
  }

  unusableZones(): DnsZone[] {
    return (this.zones() ?? []).filter((z) => z.usability !== DnsZoneUsability.Ready);
  }

  accountName(): string | null {
    return (this.zones() ?? []).map((z) => z.accountName).find((n) => !!n) ?? null;
  }
}
