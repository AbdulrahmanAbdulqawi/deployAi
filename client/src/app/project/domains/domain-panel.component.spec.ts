import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { DomainPanelComponent } from './domain-panel.component';
import { DomainSource, DomainStatus, ProjectDomain } from '../../core/models/api.models';

describe('DomainPanelComponent', () => {
  let fixture: ReturnType<typeof TestBed.createComponent<DomainPanelComponent>>;
  let component: DomainPanelComponent;
  let http: HttpTestingController;

  const domain = (overrides: Partial<ProjectDomain> = {}): ProjectDomain => ({
    id: 'domain-1',
    deployTargetId: 'target-1',
    hostname: 'app.example.com',
    displayHostname: 'app.example.com',
    source: DomainSource.UserProvided,
    status: DomainStatus.DnsPending,
    isPrimary: true,
    statusMessage: 'Waiting.',
    expectedAddress: '46.225.80.188',
    instruction: {
      type: 'A',
      name: 'app.example.com',
      value: '46.225.80.188',
      hint: 'Leave any proxy toggle off.',
    },
    certificateIssuer: null,
    certificateNotAfter: null,
    lastCheckedAt: null,
    ...overrides,
  });

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [DomainPanelComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });

    fixture = TestBed.createComponent(DomainPanelComponent);
    component = fixture.componentInstance;
    component.projectId = 'project-1';
    component.deployTargetId = 'target-1';
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function flushInitialRequests(domains: ProjectDomain[] = []): void {
    fixture.detectChanges();
    http.expectOne('/api/projects/project-1/domains').flush(domains);
    http
      .expectOne('/api/projects/project-1/domains/options')
      .flush({ suggestedSubdomain: null, zones: [] });
    fixture.detectChanges();
  }

  // The distinction the whole feature rests on, carried all the way to the pixel: a check that
  // could not run must never read as the user's DNS being wrong.
  it('separates "we could not check" from a real failure', () => {
    expect(component.presentation(DomainStatus.DnsUnverifiable).tone).toBe('unknown');
    expect(component.presentation(DomainStatus.CertificateUnverifiable).tone).toBe('unknown');
    expect(component.presentation(DomainStatus.DnsFailed).tone).toBe('attention');
    expect(component.presentation(DomainStatus.CertificateFailed).tone).toBe('attention');
  });

  it('shows issuance as in progress rather than broken', () => {
    expect(component.presentation(DomainStatus.CertificatePending).tone).toBe('working');
    expect(component.presentation(DomainStatus.Assigned).tone).toBe('working');
    expect(component.presentation(DomainStatus.Active).tone).toBe('live');
  });

  it('shows the exact record to create when nothing can write it for the user', () => {
    flushInitialRequests([domain()]);

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('46.225.80.188');
    expect(text).toContain('Add this at your domain provider');
  });

  // A name under DeployAI's own zone is already covered by a wildcard record, so asking the user
  // to create one would be asking for work that is already done.
  it('hides the record instructions for a domain the platform already controls', () => {
    flushInitialRequests([domain({ source: DomainSource.PlatformSubdomain })]);

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).not.toContain('Add this at your domain provider');
  });

  it('hides the record instructions once the domain is live', () => {
    flushInitialRequests([domain({ status: DomainStatus.Active })]);

    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).not.toContain('Add this at your domain provider');
  });

  it('offers a check-again button only once a domain has stopped moving', () => {
    expect(component.isSettled(domain({ status: DomainStatus.DnsUnverifiable }))).toBe(true);
    expect(component.isSettled(domain({ status: DomainStatus.DnsPending }))).toBe(false);
  });

  it('posts the typed domain against the browser-facing target', () => {
    flushInitialRequests();

    component.typedDomain = 'shop.example.com';
    component.add();

    const request = http.expectOne('/api/projects/project-1/domains');
    expect(request.request.body).toEqual({
      deployTargetId: 'target-1',
      domain: 'shop.example.com',
    });
    request.flush(domain({ hostname: 'shop.example.com' }));
  });

  it('surfaces the reason a domain was rejected', () => {
    flushInitialRequests();

    component.typedDomain = '*.example.com';
    component.add();

    http.expectOne('/api/projects/project-1/domains').flush(
      { error: { code: 'domain_invalid', message: 'Wildcard domains need a different check.' } },
      { status: 400, statusText: 'Bad Request' }
    );

    expect(component.error()).toContain('Wildcard');
  });
});
