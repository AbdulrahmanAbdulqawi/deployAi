import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, switchMap, throwError } from 'rxjs';
import { AuthService } from '../services/auth.service';

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const token = auth.accessToken;

  const authReq = token
    ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } })
    : req;

  return next(authReq).pipe(
    catchError((error) => {
      if (error.status !== 401 || !auth.refreshToken || req.url.includes('/api/auth/refresh')) {
        return throwError(() => error);
      }

      return auth.refreshSession().pipe(
        switchMap(() => {
          const retryReq = req.clone({
            setHeaders: { Authorization: `Bearer ${auth.accessToken}` }
          });
          return next(retryReq);
        }),
        catchError(() => {
          auth.clearSession();
          return throwError(() => error);
        })
      );
    })
  );
};
