import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router } from '@angular/router';
import { signal } from '@angular/core';
import { of } from 'rxjs';
import { ApiService } from '../core/services/api.service';
import { ToastService } from '../shared/ui/toast/toast.service';
import { DeploymentStore } from '../core/stores/deployment.store';
import { PublishViewComponent } from './publish-view.component';

describe('PublishViewComponent details visibility', () => {
  let fixture: ComponentFixture<PublishViewComponent>;
  let component: PublishViewComponent;

  const deployment = {
    id: 'dep-1',
    projectId: 'proj-1',
    branch: 'main',
    status: 'success',
    triggeredBy: 'user',
    targets: []
  };

  const project = {
    id: 'proj-1',
    name: 'Demo App',
    defaultBranch: 'main',
    githubRepoFullName: 'acme/demo',
    targets: []
  };

  beforeEach(async () => {
    const store = {
      deployment: signal(deployment),
      activity: signal([]),
      loading: signal(false),
      connectingLogs: signal(false),
      error: signal<string | null>(null),
      restoring: signal(false),
      canRestore: () => false,
      isComplete: () => true,
      overallStatus: () => 'success',
      deployProgress: () => null,
      load: jasmine.createSpy('load').and.resolveTo(undefined),
      unload: jasmine.createSpy('unload').and.resolveTo(undefined),
      restore: jasmine.createSpy('restore')
    };

    const api = jasmine.createSpyObj('ApiService', ['getProject', 'getEnvironmentSyncStatus']);
    api.getProject.and.returnValue(of(project));
    api.getEnvironmentSyncStatus.and.returnValue(of({ synced: false }));

    await TestBed.configureTestingModule({
      imports: [PublishViewComponent],
      providers: [
        { provide: DeploymentStore, useValue: store },
        { provide: ApiService, useValue: api },
        { provide: ToastService, useValue: { success: jasmine.createSpy('success'), error: jasmine.createSpy('error'), show: jasmine.createSpy('show') } },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: { get: () => 'dep-1' } } }
        },
        { provide: Router, useValue: jasmine.createSpyObj('Router', ['navigate']) }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(PublishViewComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('hides build-log details by default for a successful deploy', () => {
    expect(component.showDetails()).toBeFalse();
  });

  it('toggleDetails() flips it back open and closed', () => {
    component.toggleDetails();
    expect(component.showDetails()).toBeTrue();

    component.toggleDetails();
    expect(component.showDetails()).toBeFalse();
  });
});
