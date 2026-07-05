import { Component, OnDestroy, OnInit, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { ApiService } from '../core/services/api.service';
import { SignalRService } from '../core/services/signalr.service';
import { DeploymentDetail } from '../core/models/api.models';
import { StatusBadgeComponent } from '../shared/status-badge/status-badge.component';
import { LiveLogPanelComponent, ActivityLine } from '../shared/live-log-panel/live-log-panel.component';

@Component({
  selector: 'app-publish-view',
  standalone: true,
  imports: [StatusBadgeComponent, LiveLogPanelComponent],
  templateUrl: './publish-view.component.html',
  styleUrl: './publish-view.component.scss'
})
export class PublishViewComponent implements OnInit, OnDestroy {
  readonly deployment = signal<DeploymentDetail | null>(null);
  readonly activity = signal<ActivityLine[]>([]);

  private deploymentId = '';

  constructor(
    private readonly route: ActivatedRoute,
    private readonly api: ApiService,
    private readonly signalR: SignalRService
  ) {}

  async ngOnInit(): Promise<void> {
    this.deploymentId = this.route.snapshot.paramMap.get('id') ?? '';
    this.api.getDeployment(this.deploymentId).subscribe({
      next: (deployment) => this.deployment.set(deployment)
    });

    this.api.getDeploymentLogs(this.deploymentId).subscribe({
      next: (response) => this.activity.set(response.logs)
    });

    await this.signalR.connect({
      onLogLine: (_deploymentId, providerName, sequence, line) => {
        this.activity.update(lines => [...lines, { providerName, sequence, line }]);
      },
      onStatusChanged: (_deploymentId, providerName, status) => {
        this.deployment.update(current => {
          if (!current) return current;
          return {
            ...current,
            targets: current.targets.map(t =>
              t.providerName === providerName ? { ...t, status } : t
            )
          };
        });
      },
      onCompleted: (_deploymentId, finalStatus) => {
        this.deployment.update(current => current ? { ...current, status: finalStatus } : current);
      }
    });

    await this.signalR.joinDeployment(this.deploymentId);
  }

  async ngOnDestroy(): Promise<void> {
    await this.signalR.leaveDeployment(this.deploymentId);
  }
}
