import { Component, computed, DestroyRef, inject, input, output, signal } from '@angular/core';
import { Subscription } from 'rxjs';
import { ApiService } from '../../core/services/api.service';
import { DeploymentFailureAnalysis, DeploymentFixResult } from '../../core/models/api.models';
import { ButtonComponent } from '../ui/button/button.component';
import { IconComponent } from '../ui/icon/icon.component';
import { ActivityLine, LiveLogPanelComponent } from '../live-log-panel/live-log-panel.component';

@Component({
  selector: 'app-deployment-fix-panel',
  standalone: true,
  imports: [ButtonComponent, IconComponent, LiveLogPanelComponent],
  templateUrl: './deployment-fix-panel.component.html',
  styleUrl: './deployment-fix-panel.component.scss'
})
export class DeploymentFixPanelComponent {
  readonly deploymentId = input.required<string>();
  readonly targetId = input.required<string>();
  readonly owner = input.required<string>();
  readonly repo = input.required<string>();
  readonly failureAnalysis = input.required<DeploymentFailureAnalysis>();

  readonly fixComplete = output<void>();

  readonly generating = signal(false);
  readonly fixResult = signal<DeploymentFixResult | null>(null);
  readonly error = signal<string | null>(null);
  readonly message = signal<string | null>(null);
  readonly claudeLines = signal<ActivityLine[]>([]);
  readonly claudeLive = signal(false);

  readonly claudeErrorText = computed(() => {
    const analysis = this.failureAnalysis();
    return (analysis.errorExcerpt?.trim() || analysis.summary.trim());
  });

  readonly claudeErrorLines = computed<ActivityLine[]>(() =>
    this.claudeErrorText()
      .split('\n')
      .map(line => line.trimEnd())
      .filter(line => line.length > 0)
      .map((line, index) => ({
        providerName: 'build',
        sequence: index,
        line
      }))
  );

  readonly showClaudeActivity = computed(
    () => this.generating() || this.claudeLines().length > 0
  );

  private readonly destroyRef = inject(DestroyRef);
  private readonly api = inject(ApiService);
  private fixSubscription?: Subscription;

  constructor() {
    this.destroyRef.onDestroy(() => this.fixSubscription?.unsubscribe());
  }

  generateFix(): void {
    this.fixSubscription?.unsubscribe();
    this.generating.set(true);
    this.error.set(null);
    this.fixResult.set(null);
    this.claudeLines.set([]);
    this.claudeLive.set(true);

    this.pushClaudeLine('Extracted build errors sent to Claude:');
    for (const line of this.claudeErrorText().split('\n').slice(0, 12)) {
      const trimmed = line.trimEnd();
      if (trimmed.length > 0) {
        this.pushClaudeLine(`  ${trimmed}`);
      }
    }

    this.fixSubscription = this.api.generateDeploymentFix(this.deploymentId(), this.targetId()).subscribe({
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
          this.fixResult.set({
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
            ? 'Fix generation timed out. Try again — large repositories can take several minutes.'
            : 'Could not generate a fix with Claude.');
        this.pushClaudeLine(message, true);
        this.error.set(message);
      }
    });
  }

  mergeFix(): void {
    const fix = this.fixResult();
    if (!fix) {
      return;
    }

    this.api.mergeDeploymentFix(this.owner(), this.repo(), fix.pullRequestNumber).subscribe({
      next: () => {
        this.message.set('Fix merged. Publish again to deploy the updated code.');
        this.fixComplete.emit();
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
