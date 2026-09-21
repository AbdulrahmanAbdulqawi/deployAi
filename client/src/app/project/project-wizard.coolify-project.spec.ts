import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { of } from 'rxjs';
import { ApiService } from '../core/services/api.service';
import { ProjectsStore } from '../core/stores/projects.store';
import { ProjectWizardComponent } from './project-wizard.component';

/**
 * Creating a Coolify project from the wizard. A Coolify project is the folder an app lives in;
 * the picker used to list existing ones only, so the first deploy into a new folder was a step in
 * Coolify's own UI. These pin the wizard's half: the sentinel becomes a name on the create
 * request and never a uuid, and the environment default prefers `production` the way the
 * provider does, so the two layers cannot disagree.
 */
describe('ProjectWizardComponent Coolify project selection', () => {
  let fixture: ComponentFixture<ProjectWizardComponent>;
  let component: ProjectWizardComponent;
  let api: {
    listRepos: jasmine.Spy;
    getCredentials: jasmine.Spy;
    getEnvSchema: jasmine.Spy;
    listCoolifyProjectEnvironments: jasmine.Spy;
  };

  const createOptions = (): { coolifyProjectUuid?: string; coolifyProjectName?: string } =>
    (component as unknown as {
      buildCoolifyCreateOptions(isPrivate: boolean, role: 'website' | 'server'): {
        coolifyProjectUuid?: string;
        coolifyProjectName?: string;
      };
    }).buildCoolifyCreateOptions(false, 'website');

  beforeEach(async () => {
    api = {
      listRepos: jasmine.createSpy('listRepos').and.returnValue(of({ repos: [] })),
      getCredentials: jasmine.createSpy('getCredentials').and.returnValue(of({ credentials: [] })),
      getEnvSchema: jasmine.createSpy('getEnvSchema').and.returnValue(of({ vars: [], inconclusive: false, searchedIn: [] })),
      listCoolifyProjectEnvironments: jasmine.createSpy('listCoolifyProjectEnvironments').and.returnValue(
        of({ environments: [{ id: 'env-staging', name: 'staging' }, { id: 'env-prod', name: 'production' }] })
      )
    };

    await TestBed.configureTestingModule({
      imports: [ProjectWizardComponent],
      providers: [
        { provide: ApiService, useValue: api },
        { provide: Router, useValue: jasmine.createSpyObj('Router', ['navigate']) },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({}) } } },
        { provide: ProjectsStore, useValue: { load: jasmine.createSpy('load') } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(ProjectWizardComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    component.coolifyInfrastructure.set({
      projects: [{ id: 'proj-a', name: 'alpha' }, { id: 'proj-b', name: 'beta' }],
      servers: [{ id: 'srv', name: 'localhost' }],
      githubApps: []
    });
  });

  it('offers a new-project entry after the existing projects', () => {
    const destinations = component.coolifyDestinations();

    expect(destinations.map(d => d.id)).toEqual(['proj-a', 'proj-b', ProjectWizardComponent.NewCoolifyProject]);
  });

  it('sends the new project as a name, never as a uuid', () => {
    component.selectedCoolifyProjectUuid = ProjectWizardComponent.NewCoolifyProject;
    component.coolifyProjectToCreate = '  deployai-shapes ';

    const options = createOptions();

    expect(options.coolifyProjectName).toBe('deployai-shapes');
    expect(options.coolifyProjectUuid).toBeUndefined();
  });

  it('sends an existing project as a uuid, with no name', () => {
    component.selectedCoolifyProjectUuid = 'proj-b';

    const options = createOptions();

    expect(options.coolifyProjectUuid).toBe('proj-b');
    expect(options.coolifyProjectName).toBeUndefined();
  });

  it('does not list environments for a project that does not exist yet', () => {
    component.selectedCoolifyCredentialId = 'cred';
    component.selectedCoolifyProjectUuid = ProjectWizardComponent.NewCoolifyProject;

    component.loadCoolifyEnvironments();

    expect(api.listCoolifyProjectEnvironments).not.toHaveBeenCalled();
    expect(component.coolifyEnvironments()).toEqual([]);
  });

  it('defaults to the production environment rather than the first one listed', () => {
    component.selectedCoolifyCredentialId = 'cred';
    component.selectedCoolifyProjectUuid = 'proj-a';
    component.selectedCoolifyEnvironmentName = '';

    component.loadCoolifyEnvironments();

    expect(component.selectedCoolifyEnvironmentName).toBe('production');
  });
});
