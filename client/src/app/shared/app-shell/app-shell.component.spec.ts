import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { AppShellComponent } from './app-shell.component';
import { AuthService } from '../../core/services/auth.service';

describe('AppShellComponent sidebar collapse', () => {
  let fixture: ComponentFixture<AppShellComponent>;
  let component: AppShellComponent;

  beforeEach(() => {
    localStorage.removeItem('deployai-sidebar-collapsed');

    TestBed.configureTestingModule({
      imports: [AppShellComponent],
      providers: [
        provideRouter([]),
        { provide: AuthService, useValue: { logout: jasmine.createSpy('logout') } }
      ]
    });

    fixture = TestBed.createComponent(AppShellComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => {
    localStorage.removeItem('deployai-sidebar-collapsed');
  });

  it('starts expanded when nothing is stored', () => {
    expect(component.sidebarCollapsed()).toBeFalse();
  });

  it('toggling collapses the sidebar and persists it', () => {
    component.toggleSidebar();
    expect(component.sidebarCollapsed()).toBeTrue();
    expect(localStorage.getItem('deployai-sidebar-collapsed')).toBe('true');
  });

  it('toggling twice returns to expanded and persists that too', () => {
    component.toggleSidebar();
    component.toggleSidebar();
    expect(component.sidebarCollapsed()).toBeFalse();
    expect(localStorage.getItem('deployai-sidebar-collapsed')).toBe('false');
  });

  it('a new instance reads the previously stored collapsed state', () => {
    localStorage.setItem('deployai-sidebar-collapsed', 'true');

    const otherFixture = TestBed.createComponent(AppShellComponent);
    expect(otherFixture.componentInstance.sidebarCollapsed()).toBeTrue();
  });
});
