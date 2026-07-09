import { Component, computed, DestroyRef, effect, inject, input, output, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, Subscription } from 'rxjs';
import {
  DeploymentFixResult,
  DeploymentFixStreamEvent,
  DeploymentSetupMergeResult,
  DeploymentSetupStreamEvent
} from '../../core/models/api.models';
import { ApiService } from '../../core/services/api.service';
import { ElapsedTimer, ElapsedTimerService } from '../../core/services/elapsed-timer.service';
import { formatDurationMs } from '../../core/utils/duration';
import { ButtonComponent } from '../ui/button/button.component';
import { IconComponent } from '../ui/icon/icon.component';
import { ActivityLine, LiveLogPanelComponent } from '../live-log-panel/live-log-panel.component';
import { OperationProgressComponent } from '../operation-progress/operation-progress.component';
import type { OperationProgressMode } from '../operation-progress/operation-progress.component';

export type RepoChangesStreamEvent = DeploymentFixStreamEvent | DeploymentSetupStreamEvent;

@Component({
  selector: 'app-deployment-repo-changes-flow',
  standalone: true,
  imports: [ButtonComponent, IconComponent, LiveLogPanelComponent, OperationProgressComponent],
  templateUrl: './deployment-repo-changes-flow.component.html',
  styleUrl: './deployment-repo-changes-flow.component.scss'
})
export class DeploymentRepoChangesFlowComponent {
  readonly owner = input.required<string>();
  readonly repo = input.required<string>();
  readonly projectId = input<string | null>(null);
  readonly baseBranch = input<string | null>(null);
  readonly mergeMode = input<'fix' | 'setup'>('fix');
  readonly contextTitle = input('Issue context');
  readonly contextSummary = input('');
  readonly contextHint = input<string | null>(null);
  readonly contextLines = input<ActivityLine[]>([]);
  readonly generateLabel = input('Generate fix with Claude');
  readonly showPrimaryGenerate = input(true);
  readonly secondaryGenerateLabel = input<string | null>(null);
  readonly mergeSuccessMessage = input('Changes merged. Publish again to deploy the updated code.');
  readonly buttonVariant = input<'primary' | 'secondary'>('primary');
  readonly compact = input(false);
  readonly runGenerate = input.required<() => Observable<RepoChangesStreamEvent>>();
  readonly runRegenerate = input<(() => Observable<RepoChangesStreamEvent>) | null>(null);
  readonly onRegenerate = input<(() => void) | null>(null);

  readonly deployStarted = output<string>();
  readonly mergeComplete = output<DeploymentSetupMergeResult | void>();
  readonly fixComplete = output<void>();

  readonly generating = signal(false);
  readonly usingBranch = signal(false);
  readonly merging = signal(false);
  readonly changeResult = signal<DeploymentFixResult | null>(null);
  readonly error = signal<string | null>(null);
  readonly message = signal<string | null>(null);
  readonly claudeLines = signal<ActivityLine[]>([]);
  readonly claudeLive = signal(false);
  readonly generateProgressMode = signal<OperationProgressMode>('indeterminate');
  readonly generateProgressValue = signal(0);
  readonly generateDurationMs = signal<number | null>(null);

  readonly showClaudeActivity = computed(
    () => this.generating() || this.claudeLines().length > 0
  );

  readonly generateProgressLabel = computed(() =>
    this.generating() ? this.generateLabel() : null
  );

  readonly showGenerateProgress = computed(() => this.generating() || this.generateDurationMs() !== null);

  readonly generateDurationLabel = computed(() => {
    const durationMs = this.generateDurationMs();
    return durationMs === null ? null : formatDurationMs(durationMs);
  });

  readonly generateElapsedMs = computed(() => this.generateTimer.elapsedMs());

  private readonly generateTimer: ElapsedTimer;

  readonly mergeButtonLabel = computed(() => {
    const base = this.baseBranch();
    return base ? `Merge into ${base}` : 'Merge pull request';
  });

  private readonly destroyRef = inject(DestroyRef);
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private generateSubscription?: Subscription;

  constructor(elapsedTimerService: ElapsedTimerService) {
    this.generateTimer = elapsedTimerService.create();
    this.destroyRef.onDestroy(() => {
      this.generateSubscription?.unsubscribe();
      this.generateTimer.destroy();
    });
  }

  generateChanges(): void {
    this.startGenerate(this.runGenerate()());
  }

  regenerateChanges(): void {
    const regenerate = this.runRegenerate();
    if (regenerate) {
      this.startGenerate(regenerate());
      return;
    }

    const callback = this.onRegenerate();
    if (callback) {
      callback();
      return;
    }

    this.generateChanges();
  }

  private startGenerate(stream: Observable<RepoChangesStreamEvent>): void {
    this.generateSubscription?.unsubscribe();
    this.generating.set(true);
    this.error.set(null);
    this.changeResult.set(null);
    this.message.set(null);
    this.claudeLines.set([]);
    this.claudeLive.set(true);
    this.generateDurationMs.set(null);
    this.generateProgressMode.set('indeterminate');
    this.generateProgressValue.set(0);
    this.generateTimer.start();

    this.pushClaudeLine('Sending context to Claude:');
    for (const line of this.contextLines().slice(0, 12)) {
      this.pushClaudeLine(`  ${line.line.trimEnd()}`);
    }

    if (this.contextLines().length === 0 && this.contextSummary()) {
      for (const line of this.contextSummary().split('\n').slice(0, 12)) {
        const trimmed = line.trimEnd();
        if (trimmed.length > 0) {
          this.pushClaudeLine(`  ${trimmed}`);
        }
      }
    }

    this.generateSubscription = stream.subscribe({
      next: (event) => {
        if (event.type === 'started') {
          this.generateTimer.start(event.startedAt);
          return;
        }

        if (event.type === 'log') {
          this.pushClaudeLine(event.message);
          if (event.message.includes('Turn')) {
            this.generateProgressMode.set('determinate');
            this.generateProgressValue.set(Math.max(this.generateProgressValue(), 30));
          }
          return;
        }

        this.claudeLive.set(false);
        const durationMs = this.finishGenerateTiming(event);

        if (event.type === 'complete') {
          for (const file of event.committedFiles) {
            this.pushClaudeLine(`Updated ${file}`);
          }
          this.pushClaudeLine(`Created branch ${event.branchName}`);
          this.pushClaudeLine(`Opened pull request #${event.pullRequestNumber}`);
          this.changeResult.set({
            branchName: event.branchName,
            pullRequestNumber: event.pullRequestNumber,
            pullRequestUrl: event.pullRequestUrl,
            committedFiles: event.committedFiles,
            durationSeconds: event.durationSeconds
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
        this.generateDurationMs.set(this.generateTimer.stop());
        this.generateProgressMode.set('failed');
        this.generateProgressValue.set(100);
        const errorMessage =
          err?.error?.error?.message ??
          (err?.name === 'TimeoutError'
            ? 'Generation timed out. Try again — large repositories can take several minutes.'
            : 'Could not complete the request with Claude.');
        this.pushClaudeLine(errorMessage, true);
        this.error.set(errorMessage);
      }
    });
  }

  private finishGenerateTiming(event: RepoChangesStreamEvent | null): number {
    const serverDurationMs =
      event?.type === 'complete' && event.durationSeconds !== undefined
        ? event.durationSeconds * 1000
        : null;
    const durationMs = serverDurationMs ?? this.generateTimer.stop();
    this.generateDurationMs.set(durationMs);
    this.generateProgressMode.set(event?.type === 'error' ? 'failed' : 'complete');
    this.generateProgressValue.set(100);
    return durationMs;
  }

  useBranchAndDeploy(): void {
    const projectId = this.projectId();
    const result = this.changeResult();
    if (!projectId || !result) {
      this.error.set('Project context is required to deploy from this branch.');
      return;
    }

    this.usingBranch.set(true);
    this.error.set(null);
    this.api.useBranchAndDeploy(projectId, result.branchName, true).subscribe({
      next: (response) => {
        this.usingBranch.set(false);
        this.message.set(response.message ?? `Deploying from ${response.branch} without merging.`);
        if (response.deploymentId) {
          this.deployStarted.emit(response.deploymentId);
          void this.router.navigate(['/projects', projectId, 'deploy', response.deploymentId]);
        }
        this.fixComplete.emit();
      },
      error: (err) => {
        this.usingBranch.set(false);
        this.error.set(err?.error?.error?.message ?? 'Could not deploy from this branch.');
      }
    });
  }

  mergeChanges(): void {
    const result = this.changeResult();
    if (!result) {
      return;
    }

    this.merging.set(true);
    this.error.set(null);

    const merge$ =
      this.mergeMode() === 'setup'
        ? this.api.mergeDeploymentSetup(
            this.owner(),
            this.repo(),
            result.pullRequestNumber,
            this.projectId() ?? undefined
          )
        : this.api.mergeDeploymentFix(this.owner(), this.repo(), result.pullRequestNumber);

    merge$.subscribe({
      next: (mergeResult) => {
        this.merging.set(false);
        this.message.set(this.mergeSuccessMessage());
        if (this.mergeMode() === 'setup') {
          this.mergeComplete.emit(mergeResult as DeploymentSetupMergeResult);
        } else {
          this.mergeComplete.emit();
          this.fixComplete.emit();
        }
      },
      error: (err) => {
        this.merging.set(false);
        this.error.set(err?.error?.error?.message ?? 'Could not merge pull request.');
      }
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
