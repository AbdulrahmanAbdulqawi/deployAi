import { Component, OnDestroy, OnInit, effect, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { DeploymentStore } from '../core/stores/deployment.store';
import { ApiService } from '../core/services/api.service';
import { roleLabelForProvider } from '../core/utils/target-config';
import { ActivityLine } from '../shared/live-log-panel/live-log-panel.component';
import { StatusBadgeComponent } from '../shared/status-badge/status-badge.component';
import { ProviderStatusCardComponent } from '../shared/provider-status-card/provider-status-card.component';
import { IconComponent } from '../shared/ui/icon/icon.component';
import { ButtonComponent } from '../shared/ui/button/button.component';

@Component({
  selector: 'app-publish-view',
  standalone: true,
  imports: [StatusBadgeComponent, ProviderStatusCardComponent, IconComponent, ButtonComponent],
  templateUrl: './publish-view.component.html',
  styleUrl: './publish-view.component.scss'
})
export class PublishViewComponent implements OnInit, OnDestroy {
  readonly showDetails = signal(false);
  readonly expandedTargets = signal<Record<string, boolean>>({});
  readonly redeployingTargets = signal<Record<string, boolean>>({});
  readonly redeployMessage = signal<string | null>(null);

  constructor(
    readonly store: DeploymentStore,
    private readonly route: ActivatedRoute,
    private readonly api: ApiService
  ) {
    effect(() => {
      const deployment = this.store.deployment();
      if (!deployment || !this.store.isComplete()) {
        return;
      }

      const nextExpanded = { ...this.expandedTargets() };
      let changed = false;
      for (const target of deployment.targets) {
        if (target.status === 'failed' && !nextExpanded[target.id]) {
          nextExpanded[target.id] = true;
          changed = true;
        }
      }

      if (changed) {
        this.expandedTargets.set(nextExpanded);
      }
    });
  }

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

  isExpanded(targetId: string): boolean {
    return this.expandedTargets()[targetId] ?? false;
  }

  setTargetExpanded(targetId: string, expanded: boolean): void {
    this.expandedTargets.update(state => ({
      ...state,
      [targetId]: expanded
    }));
  }

  linesForProvider(providerName: string): ActivityLine[] {
    return this.store.activity().filter(line => line.providerName === providerName);
  }

  canRedeployTarget(target: { status: string; deployTargetId?: string }): boolean {
    return target.status === 'failed' && !!target.deployTargetId && this.store.isComplete();
  }

  redeployTarget(target: {
    id: string;
    deployTargetId: string;
    providerName: string;
  }): void {
    const deployment = this.store.deployment();
    if (!deployment) {
      return;
    }

    this.redeployingTargets.update(state => ({ ...state, [target.id]: true }));
    this.redeployMessage.set(null);
    this.api.redeployProjectService(deployment.projectId, target.deployTargetId).subscribe({
      next: () => {
        this.redeployingTargets.update(state => ({ ...state, [target.id]: false }));
        this.redeployMessage.set(`${this.roleLabel(target.providerName)} redeploy started.`);
        this.expandedTargets.update(state => ({ ...state, [target.id]: true }));
      },
      error: (err) => {
        this.redeployingTargets.update(state => ({ ...state, [target.id]: false }));
        this.redeployMessage.set(err?.error?.error?.message ?? 'Could not redeploy that service.');
      }
    });
  }

  isRedeploying(targetId: string): boolean {
    return this.redeployingTargets()[targetId] ?? false;
  }
}
