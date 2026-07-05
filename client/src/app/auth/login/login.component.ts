import { Component } from '@angular/core';
import { AuthService } from '../../core/services/auth.service';

@Component({
  selector: 'app-login',
  standalone: true,
  templateUrl: './login.component.html',
  styleUrl: './login.component.scss'
})
export class LoginComponent {
  constructor(private readonly auth: AuthService) {}

  continueWithGitHub(): void {
    this.auth.loginWithGitHub();
  }
}
