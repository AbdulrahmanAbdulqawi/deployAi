import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute } from '@angular/router';
import { of, throwError } from 'rxjs';
import { ApiService } from '../../../core/services/api.service';
import { ProjectConfigPanelComponent } from './project-config-panel.component';

describe('ProjectConfigPanelComponent', () => {
  let fixture: ComponentFixture<ProjectConfigPanelComponent>;
  let component: ProjectConfigPanelComponent;
  let api: jasmine.SpyObj<ApiService>;

  const project = {
    id: 'proj-1',
    name: 'Demo App',
    defaultBranch: 'main',
    githubRepoFullName: 'acme/demo',
    targets: []
  };

  beforeEach(async () => {
    api = jasmine.createSpyObj('ApiService', ['getProject', 'listBranches', 'updateProject']);
    api.getProject.and.returnValue(of(project) as any);
    api.listBranches.and.returnValue(of({ branches: [{ name: 'main' }] }) as any);

    await TestBed.configureTestingModule({
      imports: [ProjectConfigPanelComponent],
      providers: [
        { provide: ApiService, useValue: api },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: { get: () => 'proj-1' } } }
        }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(ProjectConfigPanelComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('clears page loading after the project loads', () => {
    expect(component.pageLoading()).toBeFalse();
    expect(component.project()?.name).toBe('Demo App');
    expect(component.name).toBe('Demo App');
  });

  it('shows a load error when the project fetch fails', () => {
    api.getProject.and.returnValue(throwError(() => ({ error: { error: { message: 'Not found' } } })));

    fixture = TestBed.createComponent(ProjectConfigPanelComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();

    expect(component.pageLoading()).toBeFalse();
    expect(component.loadError()).toBe('Not found');
  });

  it('saves without navigating away, showing a success message', () => {
    api.updateProject.and.returnValue(of({ ...project }) as any);

    component.save();

    expect(api.updateProject).toHaveBeenCalled();
    expect(component.saving()).toBeFalse();
    expect(component.isError()).toBeFalse();
    expect(component.message()).toBe('Changes saved.');
  });

  it('shows an error message when saving fails', () => {
    api.updateProject.and.returnValue(throwError(() => ({ error: { error: { message: 'Nope' } } })));

    component.save();

    expect(component.saving()).toBeFalse();
    expect(component.isError()).toBeTrue();
    expect(component.message()).toBe('Nope');
  });
});
