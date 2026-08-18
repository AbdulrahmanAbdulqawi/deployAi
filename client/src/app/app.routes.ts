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
        path: 'fleet',
        loadComponent: () => import('./fleet/fleet-health.component').then(m => m.FleetHealthComponent)
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
            path: 'ai',
            loadComponent: () => import('./settings/ai/ai-settings.component').then(m => m.AiSettingsComponent)
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
        path: 'projects/:projectId/deploy/:deploymentId',
        loadComponent: () => import('./publish/publish-view.component').then(m => m.PublishViewComponent)
      },
      {
        path: 'projects/:id',
        loadComponent: () => import('./project/workspace/project-workspace.component').then(m => m.ProjectWorkspaceComponent),
        children: [
          { path: '', pathMatch: 'full', redirectTo: 'overview' },
          {
            path: 'overview',
            loadComponent: () => import('./project/workspace/overview/project-overview.component').then(m => m.ProjectOverviewComponent)
          },
          {
            path: 'activity',
            loadComponent: () => import('./project/workspace/activity/project-activity.component').then(m => m.ProjectActivityComponent)
          },
          {
            path: 'domains',
            loadComponent: () => import('./project/workspace/domains/project-domains.component').then(m => m.ProjectDomainsComponent)
          },
          {
            path: 'troubleshoot',
            loadComponent: () => import('./project/workspace/troubleshoot/project-troubleshoot-tab.component').then(m => m.ProjectTroubleshootTabComponent)
          },
          {
            path: 'settings',
            loadComponent: () => import('./project/workspace/settings/project-settings.component').then(m => m.ProjectSettingsComponent)
          }
        ]
      },
      {
        path: 'publish/:id',
        loadComponent: () => import('./publish/publish-redirect.component').then(m => m.PublishRedirectComponent)
      }
    ]
  },
  { path: '**', redirectTo: 'dashboard' }
];
