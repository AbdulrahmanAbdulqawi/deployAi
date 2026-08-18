import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { DomainBuyComponent } from './domain-buy.component';
import { DomainAvailability, DomainSearchResult } from '../../core/models/api.models';

describe('DomainBuyComponent', () => {
  let fixture: ReturnType<typeof TestBed.createComponent<DomainBuyComponent>>;
  let component: DomainBuyComponent;
  let http: HttpTestingController;

  const offer = (overrides: Partial<DomainSearchResult> = {}): DomainSearchResult => ({
    hostname: 'yemenconnect.com',
    availability: DomainAvailability.Available,
    message: 'Available for $11.08.',
    quoteId: 'quote-1',
    firstYearCents: 1108,
    renewalCents: 1108,
    isFirstYearPromotional: false,
    isPremium: false,
    isSandbox: false,
    quoteExpiresAt: null,
    ...overrides,
  });

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [DomainBuyComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    fixture = TestBed.createComponent(DomainBuyComponent);
    component = fixture.componentInstance;
    component.projectId = 'project-1';
    component.deployTargetId = 'target-1';
    http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  function searchAndFlush(result = offer()): void {
    component.typed.set('yemenconnect.com');
    component.search();
    http.expectOne('/api/projects/project-1/domains/search').flush({ results: [result] });
    fixture.detectChanges();
  }

  // The registrar allows one check every ten seconds, so this must never fire on a keystroke.
  it('does not search until asked, and not on a short entry', () => {
    component.typed.set('ab');
    component.search();

    http.expectNone('/api/projects/project-1/domains/search');
  });

  it('searches with the typed name against this project', () => {
    component.typed.set('yemenconnect.com');
    component.search();

    const request = http.expectOne('/api/projects/project-1/domains/search');
    expect(request.request.body).toEqual({
      name: 'yemenconnect.com',
      deployTargetId: 'target-1',
    });
    request.flush({ results: [offer()] });
  });

  // The single most important behaviour here: money is never spent on one click.
  it('never buys straight from a search result', () => {
    searchAndFlush();

    component.askToBuy(component.result()!);

    http.expectNone('/api/projects/project-1/domains/purchase');
    expect(component.confirming()).not.toBeNull();
  });

  it('buys only against the quote id, never a price', () => {
    searchAndFlush();
    component.askToBuy(component.result()!);
    component.confirmBuy();

    const request = http.expectOne('/api/projects/project-1/domains/purchase');
    expect(request.request.body).toEqual({ quoteId: 'quote-1', agreeToTerms: true });
    request.flush({
      succeeded: true,
      hostname: 'yemenconnect.com',
      message: 'yours',
      chargedCents: 1108,
      orderId: '9912355',
      domainId: 'domain-1',
    });
  });

  it('cancelling spends nothing', () => {
    searchAndFlush();
    component.askToBuy(component.result()!);
    component.cancel();

    expect(component.confirming()).toBeNull();
    http.expectNone('/api/projects/project-1/domains/purchase');
  });

  it('tells the surrounding panel once a domain is bought, so it can show it setting up', () => {
    let announced: string | null = null;
    component.bought.subscribe((name) => (announced = name));

    searchAndFlush();
    component.askToBuy(component.result()!);
    component.confirmBuy();
    http.expectOne('/api/projects/project-1/domains/purchase').flush({
      succeeded: true,
      hostname: 'yemenconnect.com',
      message: 'yours',
      chargedCents: 1108,
      orderId: '9912355',
      domainId: 'domain-1',
    });

    expect(announced!).toBe('yemenconnect.com');
  });

  // A refusal comes back as a 200 with succeeded false -- a price that moved, insufficient funds,
  // a name taken since the search. The server's wording says which, so it is shown verbatim.
  it('shows why a purchase was refused', () => {
    searchAndFlush();
    component.askToBuy(component.result()!);
    component.confirmBuy();

    http.expectOne('/api/projects/project-1/domains/purchase').flush({
      succeeded: false,
      hostname: 'yemenconnect.com',
      message: 'The cost submitted must equal the cost of the domain.',
      chargedCents: null,
      orderId: null,
      domainId: null,
    });

    expect(component.error()).toContain('must equal the cost');
    expect(component.purchased()).toBeNull();
  });

  it('shows a taken domain without offering to buy it', () => {
    searchAndFlush(
      offer({ availability: DomainAvailability.Taken, quoteId: null, message: 'already taken' })
    );

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('already taken');
    expect(text).not.toContain('Buy for');
  });

  // The renewal is the figure people are surprised by, so the confirmation states it as plainly
  // as the first-year price.
  it('states the renewal price in the confirmation', () => {
    searchAndFlush(offer({ firstYearCents: 199, renewalCents: 3499, isFirstYearPromotional: true }));
    component.askToBuy(component.result()!);
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('$1.99');
    expect(text).toContain('$34.99');
    expect(text).toContain('Every year after');
    expect(text).toContain('promotional');
  });

  it('says when a purchase would not spend real money', () => {
    searchAndFlush(offer({ isSandbox: true }));
    component.askToBuy(component.result()!);
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Test mode');
    expect(text).toContain('no real money');
  });

  it('warns that a real purchase cannot be undone', () => {
    searchAndFlush();
    component.askToBuy(component.result()!);
    fixture.detectChanges();

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain("can't be refunded");
  });
});
