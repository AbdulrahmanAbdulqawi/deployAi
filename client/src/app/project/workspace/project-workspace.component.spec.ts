import { TestBed } from '@angular/core/testing';
import { Component } from '@angular/core';
import { provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { of } from 'rxjs';
import { ApiService } from '../../core/services/api.service';
import { AiSetupPreferenceService } from '../../core/services/ai-setup-preference.service';
import { ProjectsStore } from '../../core/stores/projects.store';
import { ToastService } from '../../shared/ui/toast/toast.service';
import { ProjectWorkspaceComponent } from './project-workspace.component';

@Component({ selector: 'app-stub-overview', standalone: true, template: '' })
class StubOverviewComponent {}

@Component({ selector: 'app-stub-activity', standalone: true, template: '' })
class StubActivityComponent {}

describe('ProjectWorkspaceComponent routing', () => {
  let api: jasmine.SpyObj<ApiService>;

  const project = (id: string) => ({
    id,
    name: `Project ${id}`,
    defaultBranch: 'main',
    githubRepoFullName: 'acme/demo',
    targets: []
  });

  beforeEach(() => {
    api = jasmine.createSpyObj('ApiService', [
      'getProject',
      'getProjectServices',
      'listDeployments',
      'getProjectDeploymentReadiness'
    ]);
    api.getProject.and.callFake((id: string) => of(project(id)) as any);
    api.getProjectServices.and.returnValue(of({ applicationServices: [], dataServices: [], hasManagedServer: false }) as any);
    api.listDeployments.and.returnValue(of({ deployments: [] }) as any);

    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          {
            path: 'projects/:id',
            component: ProjectWorkspaceComponent,
            children: [
              { path: '', pathMatch: 'full', redirectTo: 'overview' },
              { path: 'overview', component: StubOverviewComponent },
              { path: 'activity', component: StubActivityComponent }
            ]
          }
        ]),
        { provide: ApiService, useValue: api },
        {
          provide: AiSetupPreferenceService,
          useValue: { enabled: () => false, hydrateForProject: jasmine.createSpy('hydrateForProject') }
        },
        {
          provide: ProjectsStore,
          useValue: { deployingProjectId: () => null, triggerDeploy: jasmine.createSpy('triggerDeploy') }
        },
        { provide: ToastService, useValue: { error: jasmine.createSpy('error'), success: jasmine.createSpy('success') } }
      ]
    });
  });

  it('projects/:id redirects to the overview tab and loads the project once', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/projects/proj-1');

    expect(harness.routeNativeElement).toBeTruthy();
    expect(api.getProject).toHaveBeenCalledTimes(1);
    expect(api.getProject).toHaveBeenCalledWith('proj-1');
  });

  it('switching tabs under the same project does not reload it', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/projects/proj-1/overview');
    expect(api.getProject).toHaveBeenCalledTimes(1);

    await harness.navigateByUrl('/projects/proj-1/activity');
    expect(api.getProject).toHaveBeenCalledTimes(1);
  });

  it('navigating to a different project reloads it', async () => {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/projects/proj-1/overview');
    expect(api.getProject).toHaveBeenCalledTimes(1);

    await harness.navigateByUrl('/projects/proj-2/overview');
    expect(api.getProject).toHaveBeenCalledTimes(2);
    expect(api.getProject).toHaveBeenCalledWith('proj-2');
  });
});
