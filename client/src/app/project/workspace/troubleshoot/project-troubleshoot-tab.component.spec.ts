import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { of } from 'rxjs';
import { ApiService } from '../../../core/services/api.service';
import { AiSetupPreferenceService } from '../../../core/services/ai-setup-preference.service';
import { ToastService } from '../../../shared/ui/toast/toast.service';
import { DeploymentDetail, ProviderName } from '../../../core/models/api.models';
import { ProjectTroubleshootTabComponent } from './project-troubleshoot-tab.component';

/**
 * The page named "Troubleshoot" opened on setup files, log panels and health checks, and never
 * stated the problem — although the API returns a failure analysis that the live deploy view was
 * already rendering. These cover the header that answers the two questions someone arrives with:
 * what broke, and is the rest of my app still up.
 */
describe('ProjectTroubleshootTabComponent what-is-wrong header', () => {
  let fixture: ComponentFixture<ProjectTroubleshootTabComponent>;
  let component: ProjectTroubleshootTabComponent;

  const text = (): string => (fixture.nativeElement as HTMLElement).textContent ?? '';

  const target = (over: Partial<DeploymentDetail['targets'][number]>): DeploymentDetail['targets'][number] =>
    ({
      id: 't1',
      deployTargetId: 'dt1',
      providerName: ProviderName.Coolify,
      status: 'success',
      ...over
    }) as DeploymentDetail['targets'][number];

  const deploymentWith = (targets: DeploymentDetail['targets']): DeploymentDetail =>
    ({ id: 'd1', branch: 'main', status: 'failed', targets }) as DeploymentDetail;

  const project = {
    id: 'p1',
    name: 'Yemen Connect',
    githubRepoFullName: 'acme/yemen-connect',
    defaultBranch: 'main',
    targets: []
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ProjectTroubleshootTabComponent],
      providers: [
        {
          provide: ApiService,
          useValue: {
            // `loadPage` reads the preference, then the project, then the latest deployment. All
            // three have to answer or the page lands in its error state and renders none of this.
            getAiSetupPreference: () => of({ enabled: false }),
            getProject: () => of(project),
            listDeployments: () => of({ deployments: [] }),
            getDeploymentLogs: () => of({ logs: [] })
          }
        },
        { provide: Router, useValue: jasmine.createSpyObj('Router', ['navigate']) },
        {
          // Both maps are needed: loadDeploymentForPage reads queryParamMap for a deploymentId, and
          // an undefined map throws inside the load chain, which surfaces as the page's error state
          // rather than as anything pointing at the route.
          provide: ActivatedRoute,
          useValue: {
            snapshot: {
              paramMap: convertToParamMap({ id: 'p1' }),
              queryParamMap: convertToParamMap({})
            }
          }
        },
        { provide: ToastService, useValue: jasmine.createSpyObj('ToastService', ['show', 'error', 'success']) },
        {
          // `enabled` must be a real signal — loadPage calls `.set` on it.
          provide: AiSetupPreferenceService,
          useValue: { enabled: signal(false), setEnabled: () => {}, setEnabledForProject: () => {} }
        }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(ProjectTroubleshootTabComponent);
    component = fixture.componentInstance;
    // Runs ngOnInit, which loads the project; the deployment is then set per test.
    fixture.detectChanges();
  });

  it('names the failed part by its role, not by its provider', () => {
    component.deployment.set(
      deploymentWith([
        target({ id: 'web', role: 'website', status: 'success' }),
        target({ id: 'api', role: 'server', status: 'failed' })
      ])
    );
    fixture.detectChanges();

    // Both parts are Coolify. Keying off provider would call them the same thing.
    expect(component.troubleHeadline()).toBe("Your server didn't start");
    expect(text()).toContain("Your server didn't start");
  });

  it('says what is still running before it says what broke', () => {
    component.deployment.set(
      deploymentWith([
        target({ id: 'web', role: 'website', status: 'success' }),
        target({ id: 'api', role: 'server', status: 'failed' })
      ])
    );
    fixture.detectChanges();

    const body = text();
    expect(body).toContain('Your website is still running.');
    expect(body.indexOf('still running')).toBeLessThan(body.indexOf('Why'));
  });

  it('shows the failure analysis the API already returns', () => {
    component.deployment.set(
      deploymentWith([
        target({
          id: 'api',
          role: 'server',
          status: 'failed',
          failureAnalysis: {
            category: 'code_build',
            summary: "A connection string named 'Default' was not configured.",
            errorExcerpt: 'InvalidOperationException: A connection string named "Default"',
            referencedFiles: ['src/Api/Program.cs'],
            canRequestClaudeFix: false
          }
        })
      ])
    );
    fixture.detectChanges();

    const body = text();
    expect(body).toContain("A connection string named 'Default' was not configured.");
    expect(body).toContain('InvalidOperationException');
    expect(body).toContain('src/Api/Program.cs');
  });

  /** An unexplained failure must not render as an empty panel that reads like nothing is wrong. */
  it('admits when it cannot explain the failure', () => {
    component.deployment.set(
      deploymentWith([target({ id: 'api', role: 'server', status: 'failed', failureAnalysis: null })])
    );
    fixture.detectChanges();

    expect(text()).toContain("couldn't work out what went wrong");
  });

  it('leads with the failure that carries an explanation', () => {
    const explained = target({
      id: 'api',
      role: 'server',
      status: 'failed',
      failureAnalysis: {
        category: 'code_build',
        summary: 'Explained failure.',
        referencedFiles: [],
        canRequestClaudeFix: false
      }
    });
    component.deployment.set(
      deploymentWith([target({ id: 'store', role: 'storage', status: 'failed' }), explained])
    );
    fixture.detectChanges();

    expect(component.analysedFailure()?.id).toBe('api');
    expect(component.troubleHeadline()).toBe("2 parts of your app didn't start");
  });

  it('does not claim trouble when every part deployed', () => {
    component.deployment.set(
      deploymentWith([
        target({ id: 'web', role: 'website', status: 'success' }),
        target({ id: 'api', role: 'server', status: 'success' })
      ])
    );
    fixture.detectChanges();

    expect(component.hasTrouble()).toBeFalse();
    expect(text()).toContain('Nothing is failing right now');
    expect(text()).not.toContain("didn't start");
  });

  it('falls back to the provider for a legacy target with no role', () => {
    component.deployment.set(
      deploymentWith([target({ id: 'v', providerName: ProviderName.Vercel, status: 'failed', role: null })])
    );
    fixture.detectChanges();

    expect(component.troubleHeadline()).toBe("Your website didn't start");
  });
});
