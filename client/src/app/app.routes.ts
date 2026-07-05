import { Routes } from '@angular/router';
import { authGuard, guestGuard } from './core/guards/auth.guard';
import { AppShellComponent } from './shared/app-shell/app-shell.component';
import { LoginComponent } from './auth/login/login.component';
import { AuthCallbackComponent } from './auth/callback/auth-callback.component';
import { DashboardComponent } from './dashboard/dashboard.component';
import { SettingsComponent } from './settings/settings.component';
import { ProjectWizardComponent } from './project/project-wizard.component';
import { ProjectDetailComponent } from './project/project-detail.component';
import { ProjectEditComponent } from './project/project-edit.component';
import { PublishViewComponent } from './publish/publish-view.component';
import { HistoryComponent } from './history/history.component';

export const routes: Routes = [
  { path: 'login', component: LoginComponent, canActivate: [guestGuard] },
  { path: 'auth/callback', component: AuthCallbackComponent },
  {
    path: '',
    component: AppShellComponent,
    canActivate: [authGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      { path: 'dashboard', component: DashboardComponent },
      { path: 'settings', component: SettingsComponent },
      { path: 'projects/new', component: ProjectWizardComponent },
      { path: 'projects/:id/edit', component: ProjectEditComponent },
      { path: 'projects/:id', component: ProjectDetailComponent },
      { path: 'projects/:id/history', component: HistoryComponent },
      { path: 'publish/:id', component: PublishViewComponent }
    ]
  },
  { path: '**', redirectTo: 'dashboard' }
];
