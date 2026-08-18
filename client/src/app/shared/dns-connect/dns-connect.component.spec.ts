import { TestBed, fakeAsync, tick } from '@angular/core/testing';
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
      supportsApproval: false,
    },
  ],
};

const APPROVABLE = {
  providers: [
    {
      name: 'porkbun',
      displayName: 'Porkbun',
      fields: [
        { key: 'apiKey', label: 'API key', secret: true, placeholder: 'pk1_…' },
        { key: 'secretApiKey', label: 'Secret API key', secret: true, placeholder: 'sk1_…' },
      ],
      supportsApproval: true,
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

  // A provider with no approval flow must still get the key form immediately — the whole screen
  // would otherwise be an approval button that cannot do anything.
  it('shows the key form for a provider that cannot be approved', () => {
    expect(component.offersApproval()).toBe(false);
    expect(component.showsFields()).toBe(true);
  });

  // Which provider is offered first decides what most users do. One that can be approved has
  // nothing to create or paste, so it leads even when another is listed before it — otherwise the
  // panel opens on a wall of token instructions and the easy path is the one behind a tab.
  it('opens on a provider that can be approved, whatever the order', () => {
    const fixture2 = TestBed.createComponent(DnsConnectComponent);
    fixture2.detectChanges();
    http.expectOne('/api/dns/providers').flush({
      providers: [...PROVIDERS.providers, ...APPROVABLE.providers],
    });

    expect(fixture2.componentInstance.selectedName()).toBe('porkbun');
  });

  it('falls back to the first provider when none can be approved', () => {
    const fixture2 = TestBed.createComponent(DnsConnectComponent);
    fixture2.detectChanges();
    http.expectOne('/api/dns/providers').flush(PROVIDERS);

    expect(fixture2.componentInstance.selectedName()).toBe('cloudflare');
  });

  describe('connecting by approval', () => {
    // A second component, because the outer setup has already answered the providers call with a
    // provider that has no approval flow.
    const approvable = () => {
      const f = TestBed.createComponent(DnsConnectComponent);
      f.detectChanges();
      http.expectOne('/api/dns/providers').flush(APPROVABLE);
      return f.componentInstance;
    };

    it('offers approval instead of the key form', () => {
      const c = approvable();

      expect(c.offersApproval()).toBe(true);
      expect(c.showsFields()).toBe(false);
    });

    it('sends the account holder to the approval page', () => {
      const opened = spyOn(window, 'open');
      const c = approvable();

      c.approve();
      http.expectOne('/api/dns/authorizations').flush({
        requestToken: 'req-1',
        approvalUrl: 'https://porkbun.com/approve/req-1',
        expiresAt: '2026-08-17T13:20:00Z',
      });

      expect(opened).toHaveBeenCalledWith(
        'https://porkbun.com/approve/req-1',
        '_blank',
        'noopener'
      );
      // Kept on screen too, for a blocked popup.
      expect(c.approvalUrl()).toBe('https://porkbun.com/approve/req-1');
    });

    // The server stores the credentials on the first successful poll, because the secret is
    // returned exactly once. Nothing here may post them a second time.
    it('saves nothing itself once approved', fakeAsync(() => {
      spyOn(window, 'open');
      const c = approvable();
      let emitted: DnsZone[] | null = null;
      c.connected.subscribe((zones) => (emitted = zones));

      c.approve();
      http.expectOne('/api/dns/authorizations').flush({
        requestToken: 'req-1',
        approvalUrl: 'https://porkbun.com/approve/req-1',
        expiresAt: '2026-08-17T13:20:00Z',
      });

      tick(3000);
      http.expectOne('/api/dns/authorizations/req-1/poll').flush({
        state: 'Approved',
        message: 'Connected.',
        connection: { connection: {}, zones: [zone()] },
      });

      http.expectNone('/api/dns/connections');
      expect(emitted!.length).toBe(1);
      expect(c.awaitingApproval()).toBe(false);

      // No further polling once it is done.
      tick(10000);
      http.expectNone('/api/dns/authorizations/req-1/poll');
    }));

    // Denied is an answer. Treated as pending it would spin on something never going to happen.
    it('stops and says why when the account holder declines', fakeAsync(() => {
      spyOn(window, 'open');
      const c = approvable();

      c.approve();
      http.expectOne('/api/dns/authorizations').flush({
        requestToken: 'req-1',
        approvalUrl: 'https://porkbun.com/approve/req-1',
        expiresAt: '2026-08-17T13:20:00Z',
      });

      tick(3000);
      http.expectOne('/api/dns/authorizations/req-1/poll').flush({
        state: 'Denied',
        message: 'That request was declined in Porkbun.',
        connection: null,
      });

      expect(c.error()).toContain('declined');
      expect(c.awaitingApproval()).toBe(false);

      tick(10000);
      http.expectNone('/api/dns/authorizations/req-1/poll');
    }));

    // Unreachable says nothing about the approval, so it must not throw away one already given.
    it('keeps waiting when the provider could not be asked', fakeAsync(() => {
      spyOn(window, 'open');
      const c = approvable();

      c.approve();
      http.expectOne('/api/dns/authorizations').flush({
        requestToken: 'req-1',
        approvalUrl: 'https://porkbun.com/approve/req-1',
        expiresAt: '2026-08-17T13:20:00Z',
      });

      tick(3000);
      http.expectOne('/api/dns/authorizations/req-1/poll').flush({
        state: 'Unreachable',
        message: 'Could not reach Porkbun.',
        connection: null,
      });

      expect(c.error()).toBeNull();
      expect(c.awaitingApproval()).toBe(true);

      tick(3000);
      http.expectOne('/api/dns/authorizations/req-1/poll').flush({
        state: 'Expired',
        message: 'That approval link expired.',
        connection: null,
      });
    }));

    it('still allows keys to be entered by hand', () => {
      const c = approvable();

      c.enterKeysInstead();

      expect(c.showsFields()).toBe(true);
    });

    // Leaving a timer running would poll on forever behind a closed panel.
    it('stops polling when it goes away', fakeAsync(() => {
      spyOn(window, 'open');
      const c = approvable();

      c.approve();
      http.expectOne('/api/dns/authorizations').flush({
        requestToken: 'req-1',
        approvalUrl: 'https://porkbun.com/approve/req-1',
        expiresAt: '2026-08-17T13:20:00Z',
      });

      c.ngOnDestroy();

      tick(10000);
      http.expectNone('/api/dns/authorizations/req-1/poll');
    }));
  });
});
