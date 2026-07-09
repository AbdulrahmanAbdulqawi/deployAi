import { Injectable, computed, inject, signal } from '@angular/core';
import { ApiService } from '../services/api.service';
import { SignalRService } from '../services/signalr.service';
import { DeploymentDetail } from '../models/api.models';
import { ActivityLine } from '../../shared/live-log-panel/live-log-panel.component';
import { ProjectsStore } from './projects.store';
import { computeDeploymentProgress } from '../utils/deployment-progress';

@Injectable({ providedIn: 'root' })
export class DeploymentStore {
  private readonly api = inject(ApiService);
  private readonly signalR = inject(SignalRService);
  private readonly projectsStore = inject(ProjectsStore);

  readonly deployment = signal<DeploymentDetail | null>(null);
  readonly activity = signal<ActivityLine[]>([]);
  readonly loading = signal(false);
  readonly connectingLogs = signal(true);
  readonly error = signal<string | null>(null);
  readonly restoring = signal(false);

  readonly canRestore = computed(() => {
    const status = this.deployment()?.status;
    return status === 'failed' || status === 'partial';
  });

  readonly isComplete = computed(() => {
    const status = this.deployment()?.status;
    return status === 'success' || status === 'failed' || status === 'partial';
  });

  readonly overallStatus = computed(() => this.deployment()?.status ?? 'pending');

  readonly deployProgress = computed(() => computeDeploymentProgress(this.deployment()));

  private activeDeploymentId: string | null = null;
  private connected = false;

  async load(deploymentId: string): Promise<void> {
    if (this.activeDeploymentId === deploymentId && this.deployment()) {
      return;
    }

    await this.unload();

    this.activeDeploymentId = deploymentId;
    this.loading.set(true);
    this.connectingLogs.set(true);
    this.error.set(null);

    this.api.getDeployment(deploymentId).subscribe({
      next: (deployment) => {
        this.deployment.set(deployment);
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(err?.error?.error?.message ?? 'Could not load this publish.');
        this.loading.set(false);
      }
    });

    this.api.getDeploymentLogs(deploymentId).subscribe({
      next: (response) => {
        this.activity.set(response.logs);
        this.connectingLogs.set(false);
      },
      error: () => this.connectingLogs.set(false)
    });

    await this.signalR.connect({
      onLogLine: (id, providerName, sequence, line) => {
        if (id !== deploymentId) return;
        this.activity.update(lines => [...lines, { providerName, sequence, line }]);
        this.connectingLogs.set(false);
      },
      onStatusChanged: (id, providerName, status) => {
        if (id !== deploymentId) return;
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
      onCompleted: (id, finalStatus) => {
        if (id !== deploymentId) return;
        this.deployment.update(current => current ? { ...current, status: finalStatus } : current);
        const projectId = this.deployment()?.projectId;
        if (projectId) {
          this.projectsStore.updateProjectDeployment(projectId, deploymentId, finalStatus);
        }
        this.api.getDeployment(deploymentId).subscribe({
          next: (deployment) => this.deployment.set(deployment)
        });
      },
      onFailureAnalysisReady: (id, targetId, providerName, analysis) => {
        if (id !== deploymentId) return;
        this.deployment.update(current => {
          if (!current) return current;
          return {
            ...current,
            targets: current.targets.map(target =>
              target.id === targetId
                ? {
                    ...target,
                    failureAnalysis: {
                      category: analysis.category as 'code_build' | 'infrastructure' | 'unknown',
                      summary: analysis.summary,
                      errorExcerpt: analysis.errorExcerpt,
                      referencedFiles: analysis.referencedFiles,
                      canRequestClaudeFix: analysis.canRequestClaudeFix
                    }
                  }
                : target
            )
          };
        });
      }
    });

    this.connected = true;
    await this.signalR.joinDeployment(deploymentId);
  }

  async unload(): Promise<void> {
    if (this.activeDeploymentId && this.connected) {
      await this.signalR.leaveDeployment(this.activeDeploymentId);
    }
    this.activeDeploymentId = null;
    this.deployment.set(null);
    this.activity.set([]);
    this.connectingLogs.set(true);
  }

  restore(): void {
    const deploymentId = this.activeDeploymentId;
    if (!deploymentId || this.restoring()) {
      return;
    }

    this.restoring.set(true);
    this.error.set(null);
    this.api.restoreDeployment(deploymentId).subscribe({
      next: (response) => {
        this.restoring.set(false);
        this.deployment.update(current =>
          current ? { ...current, status: response.status } : current
        );
      },
      error: (err) => {
        this.restoring.set(false);
        this.error.set(err?.error?.error?.message ?? 'Could not restore the previous version.');
      }
    });
  }
}
