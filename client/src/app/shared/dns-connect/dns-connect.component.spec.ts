import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { DnsConnectComponent } from './dns-connect.component';
import { DnsZone, DnsZoneUsability } from '../../core/models/api.models';

const PROVIDERS = {
  providers: [
    {
      name: 'cloudflare',
      displayName: 'Cloudflare',
      fields: [{ key: 'token', label: 'API token', secret: true, placeholder: null }],
    },
  ],
};

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
    http.expectOne('/api/dns/providers').flush(PROVIDERS);
  });

  afterEach(() => http.verify());

  it('does not post an empty token', () => {
    component.setValue('token', '   ');
    component.connect();

    http.expectNone('/api/dns/connections');
  });

  it('posts the token that was typed', () => {
    component.setValue('token', 'cfut_abc');
    component.connect();

    const request = http.expectOne('/api/dns/connections');
    expect(request.request.body).toEqual({ providerName: 'cloudflare', fields: { token: 'cfut_abc' } });
    request.flush({ connection: {}, zones: [zone()] });
  });

  // Cloudflare shows a token's value once and revokes leaked ones, so it must not linger in
  // component state after it has been stored.
  it('forgets the token once it has been saved', () => {
    component.setValue('token', 'cfut_abc');
    component.connect();
    http.expectOne('/api/dns/connections').flush({ connection: {}, zones: [zone()] });

    expect(component.values()['token'] ?? '').toBe('');
  });

  // The server writes these messages because only it knows what the provider's error codes mean.
  // Replacing them with something generic here would throw away the whole point.
  it('shows the exact reason a token was refused', () => {
    component.setValue('token', 'cfut_abc');
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
    component.setValue('token', 'cfut_abc');
    component.connect();

    http.expectOne('/api/dns/connections').flush(null, { status: 503, statusText: 'Unavailable' });

    expect(component.error()).toBeTruthy();
  });

  // A zone that is listed but unusable is the case most needing explanation, so it is shown
  // separately rather than hidden.
  it('separates zones that are ready from zones that need attention', () => {
    component.setValue('token', 'cfut_abc');
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

    expect(component.zones()!.filter(z => z.usability === DnsZoneUsability.Ready).map((z) => z.name)).toEqual(['example.com']);
    expect(component.zones()!.filter(z => z.usability !== DnsZoneUsability.Ready).map((z) => z.name)).toEqual(['pending.com']);
  });

  // Connecting the wrong account is the likeliest cause of a list that looks wrong.
  it('reports which Cloudflare account was connected', () => {
    component.setValue('token', 'cfut_abc');
    component.connect();
    http.expectOne('/api/dns/connections').flush({ connection: {}, zones: [zone()] });

    expect(component.zones()!.map(z => z.accountName).find(n => !!n)).toBe('Acme');
  });

  it('emits the zones so the surrounding page can react', () => {
    let emitted: DnsZone[] | null = null;
    component.connected.subscribe((zones) => (emitted = zones));

    component.setValue('token', 'cfut_abc');
    component.connect();
    http.expectOne('/api/dns/connections').flush({ connection: {}, zones: [zone()] });

    expect(emitted!.length).toBe(1);
  });
});
