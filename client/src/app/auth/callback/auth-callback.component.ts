import { Component, OnInit } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService } from '../../core/services/auth.service';

@Component({
  selector: 'app-auth-callback',
  standalone: true,
  templateUrl: './auth-callback.component.html',
  styleUrl: './auth-callback.component.scss'
})
export class AuthCallbackComponent implements OnInit {
  message = 'One moment…';

  constructor(
    private readonly route: ActivatedRoute,
    private readonly auth: AuthService,
    private readonly router: Router
  ) {}

  ngOnInit(): void {
    const error = this.route.snapshot.queryParamMap.get('error');
    if (error) {
      this.message = 'Sign in did not go through. Try again.';
      setTimeout(() => void this.router.navigate(['/login']), 2000);
      return;
    }

    const accessToken = this.route.snapshot.queryParamMap.get('accessToken');
    const refreshToken = this.route.snapshot.queryParamMap.get('refreshToken');
    if (accessToken && refreshToken) {
      this.auth.handleCallback(accessToken, refreshToken);
      return;
    }

    this.message = 'Sign in did not go through. Try again.';
    setTimeout(() => void this.router.navigate(['/login']), 2000);
  }
}
