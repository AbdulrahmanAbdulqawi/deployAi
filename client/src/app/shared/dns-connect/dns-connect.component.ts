import { Component, EventEmitter, Input, OnInit, Output, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../core/services/api.service';
import { DnsProviderInfo, DnsZone } from '../../core/models/api.models';
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
 */
@Component({
  selector: 'app-dns-connect',
  standalone: true,
  imports: [CommonModule, FormsModule, InputComponent, ButtonComponent, DnsZoneListComponent],
  templateUrl: './dns-connect.component.html',
  styleUrl: './dns-connect.component.scss',
})
export class DnsConnectComponent implements OnInit {
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

  readonly selected = computed(
    () => this.providers().find((p) => p.name === this.selectedName()) ?? null
  );

  /** Only offer a choice when there is one to make. */
  readonly showsProviderChoice = computed(() => this.providers().length > 1);

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

  select(name: string): void {
    this.selectedName.set(name);
    // Never carry one provider's secret into another's form.
    this.values.set({});
    this.error.set(null);
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
