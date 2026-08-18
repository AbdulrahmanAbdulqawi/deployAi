import { Component, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from '../../core/services/auth.service';
import { IconComponent } from '../ui/icon/icon.component';

const SIDEBAR_COLLAPSED_KEY = 'deployai-sidebar-collapsed';

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, IconComponent],
  templateUrl: './app-shell.component.html',
  styleUrl: './app-shell.component.scss'
})
export class AppShellComponent {
  readonly sidebarCollapsed = signal<boolean>(this.readStoredSidebarCollapsed());

  constructor(private readonly auth: AuthService) {}

  signOut(): void {
    this.auth.logout();
  }

  toggleSidebar(): void {
    const next = !this.sidebarCollapsed();
    this.sidebarCollapsed.set(next);
    localStorage.setItem(SIDEBAR_COLLAPSED_KEY, next ? 'true' : 'false');
  }

  private readStoredSidebarCollapsed(): boolean {
    return localStorage.getItem(SIDEBAR_COLLAPSED_KEY) === 'true';
  }
}
