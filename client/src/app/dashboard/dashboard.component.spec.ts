import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';
import { signal } from '@angular/core';
import { ProjectsStore } from '../core/stores/projects.store';
import { DashboardComponent } from './dashboard.component';

describe('DashboardComponent search filter', () => {
  let fixture: ComponentFixture<DashboardComponent>;
  let component: DashboardComponent;

  const projects = [
    { id: 'a', name: 'My storefront', githubRepoFullName: 'acme/storefront', defaultBranch: 'main', targets: [] },
    { id: 'b', name: 'Client portal', githubRepoFullName: 'acme/portal', defaultBranch: 'main', targets: [] },
    { id: 'c', name: 'Team wiki', githubRepoFullName: 'acme/wiki', defaultBranch: 'main', targets: [] }
  ];

  beforeEach(async () => {
    const store = {
      projects: signal(projects),
      loading: signal(false),
      error: signal<string | null>(null),
      deployingProjectId: signal<string | null>(null),
      isEmpty: () => false,
      hasProjects: () => true,
      load: jasmine.createSpy('load')
    };

    await TestBed.configureTestingModule({
      imports: [DashboardComponent],
      providers: [
        { provide: ProjectsStore, useValue: store },
        { provide: Router, useValue: jasmine.createSpyObj('Router', ['navigate']) }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(DashboardComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('returns every project when the search query is empty', () => {
    expect(component.filteredProjects().length).toBe(3);
  });

  it('filters by name, case-insensitively', () => {
    component.search.set('PORTAL');
    expect(component.filteredProjects().map(p => p.name)).toEqual(['Client portal']);
  });

  it('reports no matches when the filtered list is empty but the query is non-blank', () => {
    component.search.set('nonexistent');
    expect(component.filteredProjects().length).toBe(0);
    expect(component.noSearchMatches()).toBeTrue();
  });

  it('does not report no-matches for a blank query', () => {
    component.search.set('   ');
    expect(component.noSearchMatches()).toBeFalse();
  });
});
