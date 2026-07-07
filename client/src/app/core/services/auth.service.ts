import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { finalize, Observable, shareReplay, tap } from 'rxjs';
import { SessionStore } from '../stores/session.store';

const ACCESS_TOKEN_KEY = 'deployai_access_token';
const REFRESH_TOKEN_KEY = 'deployai_refresh_token';

type RefreshResponse = { accessToken: string; refreshToken: string; expiresIn: number };

@Injectable({ providedIn: 'root' })
export class AuthService {
  private refreshRequest$: Observable<RefreshResponse> | null = null;

  constructor(
    private readonly http: HttpClient,
    private readonly router: Router,
    private readonly session: SessionStore
  ) {}

  get isAuthenticated() {
    return this.session.isAuthenticated;
  }

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
    this.session.setAuthenticated(true);
    void this.router.navigate(['/dashboard']);
  }

  refreshSession() {
    const refreshToken = this.refreshToken;
    if (!refreshToken) {
      throw new Error('No refresh token');
    }

    if (!this.refreshRequest$) {
      this.refreshRequest$ = this.http.post<RefreshResponse>(
        '/api/auth/refresh',
        { refreshToken }
      ).pipe(
        tap((response) => {
          localStorage.setItem(ACCESS_TOKEN_KEY, response.accessToken);
          localStorage.setItem(REFRESH_TOKEN_KEY, response.refreshToken);
          this.session.setAuthenticated(true);
        }),
        shareReplay(1),
        finalize(() => {
          this.refreshRequest$ = null;
        })
      );
    }

    return this.refreshRequest$;
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
    this.session.clear();
    void this.router.navigate(['/login']);
  }
}
