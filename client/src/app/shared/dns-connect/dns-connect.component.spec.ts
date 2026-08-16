import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { DnsConnectComponent } from './dns-connect.component';
import { DnsZone, DnsZoneUsability } from '../../core/models/api.models';

describe('DnsConnectComponent', () => {
  let fixture: ReturnType<typeof TestBed.createComponent<DnsConnectComponent>>;
  let component: DnsConnectComponent;
  let http: HttpTestingController;

  const zone = (overrides: Partial<DnsZone> = {}): DnsZone => ({
    id: 'zone-1',
    name: 'example.com',
    canWrite: null,
    usability: DnsZoneUsability.Ready,
    usabilityMessage: 'Ready.',
    accountName: 'Acme',
    ...overrides,
  });

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [DnsConnectComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    fixture = TestBed.createComponent(DnsConnectComponent);
    component = fixture.componentInstance;
    http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  it('does not post an empty token', () => {
    component.token.set('   ');
    component.connect();

    http.expectNone('/api/dns/connections');
  });

  it('posts the token that was typed', () => {
    component.token.set('cfut_abc');
    component.connect();

    const request = http.expectOne('/api/dns/connections');
    expect(request.request.body).toEqual({ token: 'cfut_abc' });
    request.flush({ connection: {}, zones: [zone()] });
  });

  // Cloudflare shows a token's value once and revokes leaked ones, so it must not linger in
  // component state after it has been stored.
  it('forgets the token once it has been saved', () => {
    component.token.set('cfut_abc');
    component.connect();
    http.expectOne('/api/dns/connections').flush({ connection: {}, zones: [zone()] });

    expect(component.token()).toBe('');
  });

  // The server writes these messages because only it knows what the provider's error codes mean.
  // Replacing them with something generic here would throw away the whole point.
  it('shows the exact reason a token was refused', () => {
    component.token.set('cfut_abc');
    component.connect();

    http.expectOne('/api/dns/connections').flush(
      {
        error: {
          code: 'dns_token_cannot_list_zones',
          message: "This token can change DNS records but cannot see which domains you own.",
        },
      },
      { status: 403, statusText: 'Forbidden' }
    );

    expect(component.error()).toContain('cannot see which domains you own');
  });

  it('falls back to a plain message when the server sends none', () => {
    component.token.set('cfut_abc');
    component.connect();

    http.expectOne('/api/dns/connections').flush(null, { status: 503, statusText: 'Unavailable' });

    expect(component.error()).toBeTruthy();
  });

  // A zone that is listed but unusable is the case most needing explanation, so it is shown
  // separately rather than hidden.
  it('separates zones that are ready from zones that need attention', () => {
    component.token.set('cfut_abc');
    component.connect();

    http.expectOne('/api/dns/connections').flush({
      connection: {},
      zones: [
        zone(),
        zone({
          id: 'z2',
          name: 'pending.com',
          usability: DnsZoneUsability.NotDelegated,
          usabilityMessage: 'Registrar has not delegated yet.',
        }),
      ],
    });

    expect(component.readyZones().map((z) => z.name)).toEqual(['example.com']);
    expect(component.unusableZones().map((z) => z.name)).toEqual(['pending.com']);
  });

  // Connecting the wrong account is the likeliest cause of a list that looks wrong.
  it('reports which Cloudflare account was connected', () => {
    component.token.set('cfut_abc');
    component.connect();
    http.expectOne('/api/dns/connections').flush({ connection: {}, zones: [zone()] });

    expect(component.accountName()).toBe('Acme');
  });

  it('emits the zones so the surrounding page can react', () => {
    let emitted: DnsZone[] | null = null;
    component.connected.subscribe((zones) => (emitted = zones));

    component.token.set('cfut_abc');
    component.connect();
    http.expectOne('/api/dns/connections').flush({ connection: {}, zones: [zone()] });

    expect(emitted!.length).toBe(1);
  });
});
