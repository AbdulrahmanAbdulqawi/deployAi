import { Component, computed, DestroyRef, inject, input, output, signal } from '@angular/core';
import { ApiService } from '../../core/services/api.service';
import { AiSetupPreferenceService } from '../../core/services/ai-setup-preference.service';
import {
  DeploymentPlanPart,
  DeploymentReadinessResult,
  DeploymentSetupMergeResult,
  DeploymentSetupResult,
  MissingDeploymentFile
} from '../../core/models/api.models';
import { ButtonComponent } from '../ui/button/button.component';
import { IconComponent } from '../ui/icon/icon.component';
import { ActivityLine, LiveLogPanelComponent } from '../live-log-panel/live-log-panel.component';

@Component({
  selector: 'app-deployment-setup-panel',
  standalone: true,
  imports: [ButtonComponent, IconComponent, LiveLogPanelComponent],
  templateUrl: './deployment-setup-panel.component.html',
  styleUrl: './deployment-setup-panel.component.scss'
})
export class DeploymentSetupPanelComponent {
  readonly owner = input.required<string>();
  readonly repo = input.required<string>();
  readonly gitRef = input.required<string>();
  readonly parts = input.required<DeploymentPlanPart[]>();
  readonly projectId = input<string | null>(null);

  readonly readiness = input<DeploymentReadinessResult | null>(null);
  readonly setupComplete = output<void>();

  readonly collapsed = signal(true);
  readonly generating = signal(false);
  readonly setupResult = signal<DeploymentSetupResult | null>(null);
  readonly mergeResult = signal<DeploymentSetupMergeResult | null>(null);
  readonly error = signal<string | null>(null);
  readonly claudeLines = signal<ActivityLine[]>([]);
  readonly claudeLive = signal(false);

  readonly showClaudeActivity = computed(
    () => this.generating() || this.claudeLines().length > 0
  );

  private readonly destroyRef = inject(DestroyRef);
  private readonly api = inject(ApiService);
  private readonly aiSetup = inject(AiSetupPreferenceService);
  private setupSubscription?: { unsubscribe: () => void };

  constructor() {
    this.destroyRef.onDestroy(() => this.setupSubscription?.unsubscribe());
  }

  toggleCollapsed(): void {
    this.collapsed.update(value => !value);
  }

  hasMissingFiles(): boolean {
    return (this.readiness()?.missingFiles.length ?? 0) > 0;
  }

  blockingFiles(): MissingDeploymentFile[] {
    return (this.readiness()?.missingFiles ?? []).filter(file => file.severity === 'blocking');
  }

  recommendedFiles(): MissingDeploymentFile[] {
    return (this.readiness()?.missingFiles ?? []).filter(file => file.severity === 'recommended');
  }

  severityClass(severity: MissingDeploymentFile['severity']): string {
    return severity;
  }

  severityLabel(severity: MissingDeploymentFile['severity']): string {
    switch (severity) {
      case 'blocking':
        return 'Required';
      case 'recommended':
        return 'Recommended';
      case 'warning':
        return 'Warning';
      default:
        return severity;
    }
  }

  generateSetup(forceRegenerate = false): void {
    this.setupSubscription?.unsubscribe();
    this.generating.set(true);
    this.error.set(null);
    this.mergeResult.set(null);
    this.setupResult.set(null);
    this.claudeLines.set([]);
    this.claudeLive.set(true);

    this.setupSubscription = this.api
      .generateDeploymentSetup(
        this.owner(),
        this.repo(),
        this.gitRef(),
        this.parts(),
        this.projectId() ?? undefined,
        forceRegenerate,
        this.aiSetup.enabled()
      )
      .subscribe({
        next: (event) => {
          if (event.type === 'log') {
            this.pushClaudeLine(event.message);
            return;
          }

          this.claudeLive.set(false);

          if (event.type === 'complete') {
            for (const file of event.committedFiles) {
              this.pushClaudeLine(`Updated ${file}`);
            }
            this.pushClaudeLine(`Created branch ${event.branchName}`);
            this.pushClaudeLine(`Opened pull request #${event.pullRequestNumber}`);
            this.setupResult.set({
              branchName: event.branchName,
              pullRequestNumber: event.pullRequestNumber,
              pullRequestUrl: event.pullRequestUrl,
              committedFiles: event.committedFiles
            });
            this.generating.set(false);
            return;
          }

          this.pushClaudeLine(event.message, true);
          this.error.set(event.message);
          this.generating.set(false);
        },
        error: (err) => {
          this.claudeLive.set(false);
          this.generating.set(false);
          const message =
            err?.error?.error?.message ??
            (err?.name === 'TimeoutError'
              ? 'Setup generation timed out. Try again — large repositories can take several minutes.'
              : 'Could not generate deployment files.');
          this.pushClaudeLine(message, true);
          this.error.set(message);
        }
      });
  }

  regenerateSetup(): void {
    this.generateSetup(true);
  }

  mergeSetup(): void {
    const setup = this.setupResult();
    if (!setup) {
      return;
    }

    this.api
      .mergeDeploymentSetup(
        this.owner(),
        this.repo(),
        setup.pullRequestNumber,
        this.projectId() ?? undefined
      )
      .subscribe({
        next: (result) => {
          this.mergeResult.set(result);
          this.setupComplete.emit();
        },
        error: (err) => this.error.set(err?.error?.error?.message ?? 'Could not merge pull request.')
      });
  }

  private pushClaudeLine(line: string, isError = false): void {
    const formatted = isError ? `error: ${line}` : line;
    this.claudeLines.update(lines => [
      ...lines,
      {
        providerName: 'claude',
        sequence: lines.length,
        line: formatted,
        loggedAt: new Date().toISOString()
      }
    ]);
  }
}
