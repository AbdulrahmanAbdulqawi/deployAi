import { Component } from '@angular/core';
import { AuthService } from '../../core/services/auth.service';
import { SessionStore } from '../../core/stores/session.store';
import { ButtonComponent } from '../../shared/ui/button/button.component';
import { StatusBadgeComponent } from '../../shared/status-badge/status-badge.component';

@Component({
  selector: 'app-account',
  standalone: true,
  imports: [ButtonComponent, StatusBadgeComponent],
  templateUrl: './account.component.html',
  styleUrl: './account.component.scss'
})
export class AccountComponent {
  constructor(
    readonly session: SessionStore,
    private readonly auth: AuthService
  ) {}

  signOut(): void {
    this.auth.logout();
  }
}
