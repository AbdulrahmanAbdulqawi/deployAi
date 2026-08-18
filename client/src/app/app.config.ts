import { ApplicationConfig, provideZoneChangeDetection } from '@angular/core';
import { provideRouter, withRouterConfig } from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';

import { routes } from './app.routes';
import { apiBaseInterceptor } from './core/interceptors/api-base.interceptor';
import { authInterceptor } from './core/interceptors/auth.interceptor';

export const appConfig: ApplicationConfig = {
  providers: [
    provideZoneChangeDetection({ eventCoalescing: true }),
    // The project workspace's tabs are child routes of `projects/:id` and read the project id
    // via `route.snapshot.paramMap` the same way their pre-tab pages did — inheriting ancestor
    // route params is what makes that work unchanged instead of every tab needing `route.parent`.
    provideRouter(routes, withRouterConfig({ paramsInheritanceStrategy: 'always' })),
    provideHttpClient(withInterceptors([apiBaseInterceptor, authInterceptor]))
  ]
};
