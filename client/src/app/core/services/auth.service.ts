import { Injectable, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { tap } from 'rxjs';

const ACCESS_TOKEN_KEY = 'deployai_access_token';
const REFRESH_TOKEN_KEY = 'deployai_refresh_token';

@Injectable({ providedIn: 'root' })
export class AuthService {
  readonly isAuthenticated = signal(!!localStorage.getItem(ACCESS_TOKEN_KEY));

  constructor(
    private readonly http: HttpClient,
    private readonly router: Router
  ) {}

  get accessToken(): string | null {
    return localStorage.getItem(ACCESS_TOKEN_KEY);
  }

  get refreshToken(): string | null {
    return localStorage.getItem(REFRESH_TOKEN_KEY);
  }

  loginWithGitHub(): void {
    window.location.href = '/api/auth/github/login';
  }

  handleCallback(accessToken: string, refreshToken: string): void {
    localStorage.setItem(ACCESS_TOKEN_KEY, accessToken);
    localStorage.setItem(REFRESH_TOKEN_KEY, refreshToken);
    this.isAuthenticated.set(true);
    void this.router.navigate(['/dashboard']);
  }

  refreshSession() {
    const refreshToken = this.refreshToken;
    if (!refreshToken) {
      throw new Error('No refresh token');
    }

    return this.http.post<{ accessToken: string; refreshToken: string; expiresIn: number }>(
      '/api/auth/refresh',
      { refreshToken }
    ).pipe(
      tap((response) => {
        localStorage.setItem(ACCESS_TOKEN_KEY, response.accessToken);
        localStorage.setItem(REFRESH_TOKEN_KEY, response.refreshToken);
        this.isAuthenticated.set(true);
      })
    );
  }

  logout(): void {
    const refreshToken = this.refreshToken;
    if (refreshToken) {
      this.http.post('/api/auth/logout', { refreshToken }).subscribe({
        complete: () => this.clearSession()
      });
    } else {
      this.clearSession();
    }
  }

  clearSession(): void {
    localStorage.removeItem(ACCESS_TOKEN_KEY);
    localStorage.removeItem(REFRESH_TOKEN_KEY);
    this.isAuthenticated.set(false);
    void this.router.navigate(['/login']);
  }
}
