import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { ApiService } from '../../core/services/api.service';
import { AiSetupPreferenceService } from '../../core/services/ai-setup-preference.service';
import { ProjectsStore } from '../../core/stores/projects.store';
import { ToastService } from '../../shared/ui/toast/toast.service';
import { ProjectWorkspaceContext } from './project-workspace-context.service';

describe('ProjectWorkspaceContext', () => {
  let context: ProjectWorkspaceContext;
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
    api.getProject.and.returnValue(of(project('proj-1')) as any);
    api.getProjectServices.and.returnValue(of({ applicationServices: [], dataServices: [], hasManagedServer: false }) as any);
    api.listDeployments.and.returnValue(of({ deployments: [] }) as any);

    TestBed.configureTestingModule({
      providers: [
        ProjectWorkspaceContext,
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

    context = TestBed.inject(ProjectWorkspaceContext);
  });

  it('loads the project once for a given id', () => {
    context.load('proj-1');
    expect(api.getProject).toHaveBeenCalledTimes(1);
    expect(context.project()?.id).toBe('proj-1');

    context.load('proj-1');
    expect(api.getProject).toHaveBeenCalledTimes(1);
  });

  it('reloads when the project id changes', () => {
    context.load('proj-1');
    api.getProject.and.returnValue(of(project('proj-2')) as any);

    context.load('proj-2');
    expect(api.getProject).toHaveBeenCalledTimes(2);
    expect(context.project()?.id).toBe('proj-2');
  });

  it('reload() re-fetches the current project', () => {
    context.load('proj-1');
    expect(api.getProject).toHaveBeenCalledTimes(1);

    context.reload();
    expect(api.getProject).toHaveBeenCalledTimes(2);
  });
});
