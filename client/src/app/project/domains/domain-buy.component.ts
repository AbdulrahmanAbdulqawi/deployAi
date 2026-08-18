import { Component, EventEmitter, Input, Output, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../core/services/api.service';
import { DomainAvailability, DomainSearchResult } from '../../core/models/api.models';

/**
 * Finding a domain and buying it, without leaving the product.
 *
 * Two things shape this. The registrar allows one availability check every ten seconds, so search
 * runs on a deliberate action and never on a keystroke. And buying spends real money that cannot
 * be refunded, so it goes through a confirmation that states the renewal price as prominently as
 * the first-year one — the renewal is the figure people are surprised by, and a promotional first
 * year is exactly when they are least likely to look for it.
 */
@Component({
  selector: 'app-domain-buy',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './domain-buy.component.html',
  styleUrl: './domain-buy.component.scss',
})
export class DomainBuyComponent {
  @Input({ required: true }) projectId!: string;
  @Input({ required: true }) deployTargetId!: string;

  /** Raised once a domain is bought, so the panel can show it moving through setup. */
  @Output() bought = new EventEmitter<string>();

  private readonly api = inject(ApiService);

  readonly typed = signal('');
  readonly searching = signal(false);
  readonly buying = signal(false);
  readonly result = signal<DomainSearchResult | null>(null);
  readonly error = signal<string | null>(null);
  readonly confirming = signal<DomainSearchResult | null>(null);
  readonly purchased = signal<string | null>(null);

  readonly Availability = DomainAvailability;

  readonly canSearch = computed(() => this.typed().trim().length > 2 && !this.searching());

  search(): void {
    if (!this.canSearch()) {
      return;
    }

    this.searching.set(true);
    this.error.set(null);
    this.result.set(null);
    this.purchased.set(null);

    this.api
      .searchDomains(this.projectId, {
        name: this.typed().trim(),
        deployTargetId: this.deployTargetId,
      })
      .subscribe({
        next: (response) => {
          this.result.set(response.results[0] ?? null);
          this.searching.set(false);
        },
        error: (response) => {
          this.searching.set(false);
          this.error.set(
            response?.error?.error?.message ?? 'That search could not be run. Try again.'
          );
        },
      });
  }

  /** Opens the confirmation. Nothing is spent until it is accepted. */
  askToBuy(offer: DomainSearchResult): void {
    this.confirming.set(offer);
  }

  cancel(): void {
    this.confirming.set(null);
  }

  confirmBuy(): void {
    const offer = this.confirming();
    if (!offer?.quoteId || this.buying()) {
      return;
    }

    this.buying.set(true);
    this.error.set(null);

    this.api
      .purchaseDomain(this.projectId, { quoteId: offer.quoteId, agreeToTerms: true })
      .subscribe({
        next: (response) => {
          this.buying.set(false);
          this.confirming.set(null);

          if (!response.succeeded) {
            // The server's message says which of the several refusals it was — a price that moved,
            // insufficient funds, a name taken since the search.
            this.error.set(response.message);
            return;
          }

          this.result.set(null);
          this.typed.set('');
          this.purchased.set(response.hostname);
          this.bought.emit(response.hostname);
        },
        error: (response) => {
          this.buying.set(false);
          this.confirming.set(null);
          this.error.set(
            response?.error?.error?.message ?? 'That purchase could not be completed.'
          );
        },
      });
  }

  money(cents: number | null): string {
    return cents === null ? '' : `$${(cents / 100).toFixed(2)}`;
  }
}
