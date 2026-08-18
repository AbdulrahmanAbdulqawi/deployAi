import { Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ProjectWorkspaceContext } from '../project-workspace-context.service';
import { ProjectServiceView } from '../../../core/models/api.models';
import { databaseEngineLabel, providerLabel } from '../../../core/utils/target-config';
import { EnvironmentDriftBannerComponent } from '../../../shared/environment-drift-banner/environment-drift-banner.component';
import { ProjectHealthBannerComponent } from '../../../shared/project-health-banner/project-health-banner.component';
import { ReadinessScorecardComponent } from '../../../shared/readiness-scorecard/readiness-scorecard.component';
import { DeploymentStatusStripComponent } from '../../../shared/deployment-status-strip/deployment-status-strip.component';
import { AiSetupPreferenceService } from '../../../core/services/ai-setup-preference.service';
import { IconComponent } from '../../../shared/ui/icon/icon.component';
import { ButtonComponent } from '../../../shared/ui/button/button.component';
import { ToastService } from '../../../shared/ui/toast/toast.service';

interface ConnectedItem {
  label: string;
  status: string;
}

@Component({
  selector: 'app-project-overview',
  standalone: true,
  imports: [
    RouterLink,
    EnvironmentDriftBannerComponent,
    ProjectHealthBannerComponent,
    ReadinessScorecardComponent,
    DeploymentStatusStripComponent,
    IconComponent,
    ButtonComponent
  ],
  templateUrl: './project-overview.component.html',
  styleUrl: './project-overview.component.scss'
})
export class ProjectOverviewComponent {
  readonly context = inject(ProjectWorkspaceContext);
  readonly aiSetup = inject(AiSetupPreferenceService);
  private readonly toast = inject(ToastService);

  displayUrl(url: string): string {
    return url.replace(/^https?:\/\//i, '').replace(/\/$/, '');
  }

  async copyUrl(url: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(url);
      this.toast.success('Link copied');
    } catch {
      this.toast.error('Could not copy link');
    }
  }

  connectedItems(): ConnectedItem[] {
    const services = this.context.services();
    if (!services) {
      return [];
    }

    const apps: ConnectedItem[] = services.applicationServices.map((service: ProjectServiceView) => ({
      label: providerLabel(service.providerName),
      status: 'Configured'
    }));

    const data: ConnectedItem[] = services.dataServices.map((service: ProjectServiceView) => ({
      label: databaseEngineLabel(service.databaseEngine),
      status: 'Connected'
    }));

    return [...apps, ...data];
  }
}
