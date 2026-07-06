import { Routes } from '@angular/router';
import { authGuard, guestGuard } from './core/guards/auth.guard';
import { AppShellComponent } from './shared/app-shell/app-shell.component';

export const routes: Routes = [
  {
    path: 'login',
    loadComponent: () => import('./auth/login/login.component').then(m => m.LoginComponent),
    canActivate: [guestGuard]
  },
  {
    path: 'auth/callback',
    loadComponent: () => import('./auth/callback/auth-callback.component').then(m => m.AuthCallbackComponent)
  },
  {
    path: '',
    component: AppShellComponent,
    canActivate: [authGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      {
        path: 'dashboard',
        loadComponent: () => import('./dashboard/dashboard.component').then(m => m.DashboardComponent)
      },
      {
        path: 'settings',
        loadComponent: () => import('./settings/settings-shell.component').then(m => m.SettingsShellComponent),
        children: [
          { path: '', pathMatch: 'full', redirectTo: 'connections' },
          {
            path: 'connections',
            loadComponent: () => import('./settings/connections/connections.component').then(m => m.ConnectionsComponent)
          },
          {
            path: 'notifications',
            loadComponent: () => import('./settings/notifications/notifications.component').then(m => m.NotificationsComponent)
          },
          {
            path: 'account',
            loadComponent: () => import('./settings/account/account.component').then(m => m.AccountComponent)
          }
        ]
      },
      {
        path: 'projects/new',
        loadComponent: () => import('./project/project-wizard.component').then(m => m.ProjectWizardComponent)
      },
      {
        path: 'projects/:id/edit',
        loadComponent: () => import('./project/project-edit.component').then(m => m.ProjectEditComponent)
      },
      {
        path: 'projects/:id/history',
        loadComponent: () => import('./history/history.component').then(m => m.HistoryComponent)
      },
      {
        path: 'projects/:projectId/deploy/:deploymentId',
        loadComponent: () => import('./publish/publish-view.component').then(m => m.PublishViewComponent)
      },
      {
        path: 'projects/:id',
        loadComponent: () => import('./project/project-detail.component').then(m => m.ProjectDetailComponent)
      },
      {
        path: 'publish/:id',
        loadComponent: () => import('./publish/publish-redirect.component').then(m => m.PublishRedirectComponent)
      }
    ]
  },
  { path: '**', redirectTo: 'dashboard' }
];
