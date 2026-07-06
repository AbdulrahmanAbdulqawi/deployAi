import { Component, OnDestroy, OnInit, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { DeploymentStore } from '../core/stores/deployment.store';
import { roleLabelForProvider } from '../core/utils/target-config';
import { StatusBadgeComponent } from '../shared/status-badge/status-badge.component';
import { LiveLogPanelComponent } from '../shared/live-log-panel/live-log-panel.component';
import { ProviderStatusCardComponent } from '../shared/provider-status-card/provider-status-card.component';
import { IconComponent } from '../shared/ui/icon/icon.component';
import { ButtonComponent } from '../shared/ui/button/button.component';

@Component({
  selector: 'app-publish-view',
  standalone: true,
  imports: [StatusBadgeComponent, LiveLogPanelComponent, ProviderStatusCardComponent, IconComponent, ButtonComponent],
  templateUrl: './publish-view.component.html',
  styleUrl: './publish-view.component.scss'
})
export class PublishViewComponent implements OnInit, OnDestroy {
  readonly showDetails = signal(false);

  constructor(
    readonly store: DeploymentStore,
    private readonly route: ActivatedRoute
  ) {}

  ngOnInit(): void {
    const deploymentId = this.route.snapshot.paramMap.get('deploymentId')
      ?? this.route.snapshot.paramMap.get('id')
      ?? '';
    void this.store.load(deploymentId);
  }

  async ngOnDestroy(): Promise<void> {
    await this.store.unload();
  }

  roleLabel(providerName: string): string {
    return roleLabelForProvider(providerName);
  }

  phaseHeading(): string {
    const deployment = this.store.deployment();
    if (!deployment) {
      return 'Getting ready…';
    }

    if (this.store.isComplete()) {
      if (deployment.status === 'success') {
        return "Everything's live";
      }
      if (deployment.status === 'partial') {
        return 'Partially live';
      }
      return 'Something went wrong';
    }

    const apiTarget = deployment.targets.find(t => t.providerName === 'railway');
    const siteTarget = deployment.targets.find(t => t.providerName === 'vercel');

    if (apiTarget && apiTarget.status !== 'success' && apiTarget.status !== 'failed') {
      return 'Setting up your API…';
    }

    if (siteTarget && siteTarget.status !== 'success' && siteTarget.status !== 'failed') {
      return 'Connecting your site…';
    }

    return 'Getting ready…';
  }

  liveLinks(): { label: string; url: string }[] {
    const deployment = this.store.deployment();
    if (!deployment) {
      return [];
    }

    return deployment.targets
      .filter(t => t.deployUrl)
      .map(t => ({
        label: roleLabelForProvider(t.providerName),
        url: t.deployUrl!
      }));
  }

  toggleDetails(): void {
    this.showDetails.update(open => !open);
  }
}
