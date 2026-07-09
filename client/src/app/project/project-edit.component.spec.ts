import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router } from '@angular/router';
import { of, throwError } from 'rxjs';
import { ApiService } from '../core/services/api.service';
import { ConfirmService } from '../shared/ui/confirm/confirm.service';
import { ProjectEditComponent } from './project-edit.component';

describe('ProjectEditComponent', () => {
  let fixture: ComponentFixture<ProjectEditComponent>;
  let component: ProjectEditComponent;
  let router: jasmine.SpyObj<Router>;

  const project = {
    id: 'proj-1',
    name: 'Demo App',
    defaultBranch: 'main',
    githubRepoFullName: 'acme/demo',
    targets: []
  };

  beforeEach(async () => {
    router = jasmine.createSpyObj('Router', ['navigate']);

    await TestBed.configureTestingModule({
      imports: [ProjectEditComponent],
      providers: [
        {
          provide: ApiService,
          useValue: {
            getProject: jasmine.createSpy('getProject').and.returnValue(of(project)),
            listBranches: jasmine.createSpy('listBranches').and.returnValue(of({ branches: [{ name: 'main' }] }))
          }
        },
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: {
              paramMap: {
                get: () => 'proj-1'
              }
            }
          }
        },
        { provide: Router, useValue: router },
        {
          provide: ConfirmService,
          useValue: {
            ask: jasmine.createSpy('ask').and.resolveTo(false)
          }
        }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(ProjectEditComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('clears page loading after project loads', () => {
    expect(component.pageLoading()).toBeFalse();
    expect(component.project()?.name).toBe('Demo App');
  });

  it('navigates back to the project detail page', () => {
    component.goToProject();
    expect(router.navigate).toHaveBeenCalledWith(['/projects', 'proj-1']);
  });

  it('navigates back to the projects dashboard', () => {
    component.goToProjects();
    expect(router.navigate).toHaveBeenCalledWith(['/dashboard']);
  });

  it('shows load error when project fetch fails', () => {
    const api = TestBed.inject(ApiService) as jasmine.SpyObj<ApiService>;
    api.getProject.and.returnValue(throwError(() => ({ error: { error: { message: 'Not found' } } })));

    fixture = TestBed.createComponent(ProjectEditComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();

    expect(component.pageLoading()).toBeFalse();
    expect(component.loadError()).toBe('Not found');
  });
});
