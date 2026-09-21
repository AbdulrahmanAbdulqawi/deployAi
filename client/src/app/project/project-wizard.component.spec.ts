import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router } from '@angular/router';
import { convertToParamMap } from '@angular/router';
import { of, throwError } from 'rxjs';
import { ApiService } from '../core/services/api.service';
import { ProjectsStore } from '../core/stores/projects.store';
import { ProjectWizardComponent } from './project-wizard.component';

/**
 * The env step must tell two empty answers apart: "this app declares no configuration" and "I
 * could not read the repository". Collapsing them is what let an app deploy with nothing set and
 * crash-loop on missing `Jwt` settings — the screen said "your app should run as-is" about a
 * repository it had failed to open.
 *
 * `e9b5708` fixed that. Nothing covered it, so these exist to keep the two branches distinct:
 * the distinction is invisible in a green build and costs a crash-loop when it regresses.
 */
describe('ProjectWizardComponent env scan coverage', () => {
  let fixture: ComponentFixture<ProjectWizardComponent>;
  let component: ProjectWizardComponent;
  let api: {
    listRepos: jasmine.Spy;
    getCredentials: jasmine.Spy;
    getEnvSchema: jasmine.Spy;
  };

  const text = (): string => (fixture.nativeElement as HTMLElement).textContent ?? '';

  beforeEach(async () => {
    api = {
      listRepos: jasmine.createSpy('listRepos').and.returnValue(of({ repos: [] })),
      getCredentials: jasmine.createSpy('getCredentials').and.returnValue(of({ credentials: [] })),
      getEnvSchema: jasmine.createSpy('getEnvSchema').and.returnValue(
        of({ vars: [], inconclusive: false, searchedIn: [] })
      )
    };

    await TestBed.configureTestingModule({
      imports: [ProjectWizardComponent],
      providers: [
        { provide: ApiService, useValue: api },
        { provide: Router, useValue: jasmine.createSpyObj('Router', ['navigate']) },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: convertToParamMap({}) } }
        },
        {
          provide: ProjectsStore,
          useValue: { load: jasmine.createSpy('load') }
        }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(ProjectWizardComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();

    // The env block lives inside step 3's case and behind showEnvStep; both are needed before
    // any of it renders.
    component.step.set(3);
    component.showEnvStep.set(true);
  });

  it('says it could not read the repository when the scan was inconclusive', () => {
    component.envSchema.set([]);
    component.envScanInconclusive.set(true);
    component.envScanSearchedIn.set(['src/Api', 'src/Api/appsettings.json']);
    fixture.detectChanges();

    expect(text()).toContain("couldn't read this app's configuration");
    expect(text()).toContain('src/Api');
    // The reassurance belongs to the other branch and must not appear here.
    expect(text()).not.toContain('should run as-is');
  });

  it('reassures only when the repository was actually read and declared nothing', () => {
    component.envSchema.set([]);
    component.envScanInconclusive.set(false);
    fixture.detectChanges();

    expect(text()).toContain('should run as-is');
    expect(text()).not.toContain("couldn't read this app's configuration");
  });

  /**
   * Reached through the private loader on purpose: the public path to it runs the whole plan
   * acceptance, and what is under test is the failure branch of the scan itself, not the route
   * taken to reach it.
   */
  it('treats a failed scan as inconclusive rather than as a clean empty result', () => {
    api.getEnvSchema.and.returnValue(throwError(() => new Error('502')));
    component.selectedRepo.set({ fullName: 'acme/shop', defaultBranch: 'main', private: false });
    component.selectedBranch.set('main');

    (component as unknown as { loadEnvSchema(): void }).loadEnvSchema();
    fixture.detectChanges();

    expect(component.envScanInconclusive()).toBeTrue();
    expect(component.envSchema().length).toBe(0);
    expect(text()).not.toContain('should run as-is');
  });

  it('carries a successful scan through without claiming a blind spot', () => {
    api.getEnvSchema.and.returnValue(
      of({
        vars: [
          {
            name: 'Jwt__SigningKey',
            isSecret: true,
            hasDefault: false,
            category: 'generic',
            sources: ['src/Api/appsettings.json']
          }
        ],
        inconclusive: false,
        searchedIn: ['src/Api']
      })
    );
    component.selectedRepo.set({ fullName: 'acme/shop', defaultBranch: 'main', private: false });
    component.selectedBranch.set('main');

    (component as unknown as { loadEnvSchema(): void }).loadEnvSchema();
    fixture.detectChanges();

    expect(component.envScanInconclusive()).toBeFalse();
    expect(component.requiredEnvVars().map(v => v.name)).toEqual(['Jwt__SigningKey']);
  });
});
