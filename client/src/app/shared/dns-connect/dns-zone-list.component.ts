import { Component, Input, computed, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { DnsZone, DnsZoneUsability } from '../../core/models/api.models';

/**
 * The domains a DNS connection can see, split into the ones DeployAI can use and the ones it
 * cannot, each with the reason.
 *
 * Its own component because both the connect form and the Settings card show this, and a zone
 * silently reclassified in one place but not the other is the kind of drift that makes a panel
 * promise automation it will not deliver.
 */
@Component({
  selector: 'app-dns-zone-list',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './dns-zone-list.component.html',
  styleUrl: './dns-connect.component.scss',
})
export class DnsZoneListComponent {
  private readonly zonesSignal = signal<DnsZone[]>([]);

  @Input({ required: true })
  set zones(value: DnsZone[] | null) {
    this.zonesSignal.set(value ?? []);
  }

  readonly ready = computed(() =>
    this.zonesSignal().filter((z) => z.usability === DnsZoneUsability.Ready)
  );

  readonly needsAttention = computed(() =>
    this.zonesSignal().filter((z) => z.usability !== DnsZoneUsability.Ready)
  );

  /** Connecting the wrong account is the likeliest cause of a list that looks wrong. */
  readonly accountName = computed(
    () => this.zonesSignal().map((z) => z.accountName).find((n) => !!n) ?? null
  );

  readonly hasAny = computed(() => this.zonesSignal().length > 0);
}
