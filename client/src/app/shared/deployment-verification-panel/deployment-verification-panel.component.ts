import { Component, computed, DestroyRef, effect, inject, input, output, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ApiService } from '../../core/services/api.service';
import {
  DeploymentDetail,
  DeploymentVerificationCheck,
  DeploymentVerificationResult,
  DeploymentVerificationScope,
  ProviderName,
  VerificationCheckStatus
} from '../../core/models/api.models';
import { ElapsedTimer, ElapsedTimerService } from '../../core/services/elapsed-timer.service';
import { formatDurationMs } from '../../core/utils/duration';
import { ButtonComponent } from '../ui/button/button.component';
import { IconComponent } from '../ui/icon/icon.component';
import { ToastService } from '../ui/toast/toast.service';
import { DeploymentRepoChangesFlowComponent } from '../deployment-repo-changes-flow/deployment-repo-changes-flow.component';
import { ActivityLine } from '../live-log-panel/live-log-panel.component';
import { OperationProgressComponent } from '../operation-progress/operation-progress.component';

type DeploymentTarget = DeploymentDetail['targets'][number];
type CheckGroup = { key: 'website' | 'server' | 'connection'; label: string; checks: DeploymentVerificationCheck[] };

@Component({
  selector: 'app-deployment-verification-panel',
  standalone: true,
  imports: [DatePipe, ButtonComponent, IconComponent, DeploymentRepoChangesFlowComponent, OperationProgressComponent],
  templateUrl: './deployment-verification-panel.component.html',
  styleUrl: './deployment-verification-panel.component.scss'
})
export class DeploymentVerificationPanelComponent {
  readonly deploymentId = input.required<string>();
  readonly projectId = input<string | null>(null);
  readonly repoOwner = input<string | null>(null);
  readonly repo = input<string | null>(null);
  readonly branch = input<string | null>(null);
  readonly targets = input.required<DeploymentTarget[]>();
  readonly deploymentComplete = input.required<boolean>();
  readonly canReconnect = input(false);
  readonly reconnecting = input(false);
  readonly redeploying = input<Record<string, boolean>>({});

  readonly reconnect = output<void>();
  readonly redeploy = output<DeploymentTarget>();

  readonly verifying = signal(false);
  readonly verificationResult = signal<DeploymentVerificationResult | null>(null);
  readonly verificationScope = signal<DeploymentVerificationScope>('both');
  readonly verifyError = signal<string | null>(null);
  readonly checkDurationMs = signal<number | null>(null);

  private readonly api = inject(ApiService);
  private readonly toast = inject(ToastService);
  private readonly verifyTimer: ElapsedTimer;

  readonly verifyElapsedMs = computed(() => this.verifyTimer.elapsedMs());

  readonly checkDurationLabel = computed(() => {
    const durationMs = this.checkDurationMs();
    return durationMs === null ? null : formatDurationMs(durationMs);
  });

  readonly websiteTarget = computed(() =>
    this.targets().find(target => target.providerName === ProviderName.Vercel && target.status === 'success' && !!target.deployUrl) ?? null
  );

  readonly serverTarget = computed(() =>
    this.targets().find(target => target.providerName === ProviderName.Railway && target.status === 'success' && !!target.deployUrl) ?? null
  );

  readonly coolifyTarget = computed(() =>
    this.targets().find(target => target.providerName === ProviderName.Coolify && target.status === 'success' && !!target.deployUrl) ?? null
  );

  readonly hasBothTargets = computed(() => !!this.websiteTarget() && !!this.serverTarget());

  readonly scopeOptions = computed(() => {
    const options: { value: DeploymentVerificationScope; label: string }[] = [];
    if (this.websiteTarget() && this.serverTarget()) {
      options.push({ value: 'both', label: 'Website & API' });
    }
    if (this.websiteTarget()) {
      options.push({ value: 'website', label: 'Website' });
    }
    if (this.serverTarget()) {
      options.push({ value: 'server', label: 'API' });
    }
    if (this.coolifyTarget()) {
      options.push({ value: 'server', label: 'Coolify app' });
    }
    return options;
  });

  readonly defaultScope = computed((): DeploymentVerificationScope => {
    if (this.websiteTarget() && this.serverTarget()) {
      return 'both';
    }
    if (this.websiteTarget()) {
      return 'website';
    }
    return 'server';
  });

  readonly plannedCheckGroups = computed((): CheckGroup[] => {
    const groups: CheckGroup[] = [
      { key: 'website', label: 'Website', checks: [] },
      { key: 'server', label: 'API', checks: [] },
      { key: 'connection', label: 'Connection', checks: [] }
    ];

    const scope = this.scopeOptions().some(option => option.value === this.verificationScope())
      ? this.verificationScope()
      : this.defaultScope();

    if (scope === 'website' || scope === 'both') {
      groups[0].checks = [
        { id: 'website.reachable', target: 'website', label: 'Website homepage', status: 'skipped', message: '', canRequestClaudeFix: false, referencedFiles: [] },
        { id: 'website.spa_shell', target: 'website', label: 'SPA shell', status: 'skipped', message: '', canRequestClaudeFix: false, referencedFiles: [] }
      ];
      if (this.hasBothTargets()) {
        groups[0].checks.push({
          id: 'website.split_origin_wiring',
          target: 'website',
          label: 'Split-origin bundle wiring',
          status: 'skipped',
          message: '',
          canRequestClaudeFix: false,
          referencedFiles: []
        });
      }
    }

    if (scope === 'server' || scope === 'both') {
      groups[1].checks = [
        { id: 'server.reachable', target: 'server', label: 'API reachable', status: 'skipped', message: '', canRequestClaudeFix: false, referencedFiles: [] },
        { id: 'server.health', target: 'server', label: 'API health', status: 'skipped', message: '', canRequestClaudeFix: false, referencedFiles: [] }
      ];
    }

    if (this.hasBothTargets()) {
      groups[2].checks = [
        { id: 'connection.api_health', target: 'connection', label: 'API health (split-origin)', status: 'skipped', message: '', canRequestClaudeFix: false, referencedFiles: [] },
        { id: 'connection.cors', target: 'connection', label: 'CORS', status: 'skipped', message: '', canRequestClaudeFix: false, referencedFiles: [] }
      ];
    }

    return groups.filter(group => group.checks.length > 0);
  });

  readonly canRunChecks = computed(() =>
    this.deploymentComplete() && this.scopeOptions().length > 0 && !this.verifying()
  );

  constructor(elapsedTimerService: ElapsedTimerService) {
    this.verifyTimer = elapsedTimerService.create();
    inject(DestroyRef).onDestroy(() => this.verifyTimer.destroy());

    effect(() => {
      const options = this.scopeOptions();
      const current = this.verificationScope();
      if (!options.some(option => option.value === current)) {
        this.verificationScope.set(this.defaultScope());
      }
    });
  }

  readonly resultStats = computed(() => {
    const checks = this.verificationResult()?.checks ?? [];
    return {
      passed: checks.filter(check => check.status === 'passed').length,
      failed: checks.filter(check => check.status === 'failed').length,
      warning: checks.filter(check => check.status === 'warning').length,
      skipped: checks.filter(check => check.status === 'skipped').length
    };
  });

  readonly groupedChecks = computed((): CheckGroup[] => {
    const checks = this.verificationResult()?.checks ?? [];
    const groups: CheckGroup[] = [
      { key: 'website', label: 'Website', checks: checks.filter(check => check.target === 'website') },
      { key: 'server', label: 'API', checks: checks.filter(check => check.target === 'server') },
      { key: 'connection', label: 'Connection', checks: checks.filter(check => check.target === 'connection') }
    ];
    return groups.filter(group => group.checks.length > 0);
  });

  readonly hasRepoContext = computed(() => !!(this.repoOwner() && this.repo() && this.branch()));

  setScope(scope: DeploymentVerificationScope): void {
    this.verificationScope.set(scope);
  }

  runChecks(): void {
    if (!this.canRunChecks()) {
      return;
    }

    const scope = this.scopeOptions().some(option => option.value === this.verificationScope())
      ? this.verificationScope()
      : this.defaultScope();

    this.verificationScope.set(scope);
    this.verifying.set(true);
    this.verifyError.set(null);
    this.checkDurationMs.set(null);
    this.verifyTimer.start();

    this.api.verifyDeployment(this.deploymentId(), scope).subscribe({
      next: (result) => {
        this.checkDurationMs.set(this.verifyTimer.stop());
        this.verificationResult.set(result);
        this.verifying.set(false);
      },
      error: (err) => {
        this.checkDurationMs.set(this.verifyTimer.stop());
        this.verifying.set(false);
        const message = err?.error?.error?.message ?? 'Could not run deployment checks.';
        this.verifyError.set(message);
        this.toast.error(message);
      }
    });
  }

  statusIcon(status: VerificationCheckStatus): 'check' | 'alert' | 'info' {
    switch (status) {
      case 'passed':
        return 'check';
      case 'failed':
      case 'warning':
        return 'alert';
      default:
        return 'info';
    }
  }

  statusClass(status: VerificationCheckStatus): 'success' | 'danger' | 'warning' | 'muted' {
    switch (status) {
      case 'passed':
        return 'success';
      case 'failed':
        return 'danger';
      case 'warning':
        return 'warning';
      default:
        return 'muted';
    }
  }

  statusLabel(status: VerificationCheckStatus): string {
    switch (status) {
      case 'passed':
        return 'Passed';
      case 'failed':
        return 'Failed';
      case 'warning':
        return 'Warning';
      case 'skipped':
        return 'Skipped';
      default:
        return status;
    }
  }

  showOutputDirectoryHint(check: DeploymentVerificationCheck): boolean {
    return check.suggestedAction === 'fix_output_directory' &&
      check.status !== 'passed' &&
      check.status !== 'skipped';
  }

  canActOnCheck(check: DeploymentVerificationCheck): boolean {
    if (!check.suggestedAction || check.status === 'passed' || check.status === 'skipped') {
      return false;
    }

    switch (check.suggestedAction) {
      case 'reconnect':
        return this.canReconnect();
      case 'redeploy_website':
        return !!this.websiteTarget();
      case 'redeploy_server':
        return !!this.serverTarget();
      default:
        return false;
    }
  }

  actionLabel(check: DeploymentVerificationCheck): string | null {
    switch (check.suggestedAction) {
      case 'reconnect':
        return 'Reconnect';
      case 'redeploy_website':
        return 'Redeploy website';
      case 'redeploy_server':
        return 'Redeploy API';
      default:
        return null;
    }
  }

  isRedeploying(target: DeploymentTarget | null): boolean {
    return target ? (this.redeploying()[target.id] ?? false) : false;
  }

  triggerAction(check: DeploymentVerificationCheck): void {
    switch (check.suggestedAction) {
      case 'reconnect':
        this.reconnect.emit();
        break;
      case 'redeploy_website': {
        const target = this.websiteTarget();
        if (target) {
          this.redeploy.emit(target);
        }
        break;
      }
      case 'redeploy_server': {
        const target = this.serverTarget();
        if (target) {
          this.redeploy.emit(target);
        }
        break;
      }
    }
  }

  canShowClaudeFix(check: DeploymentVerificationCheck): boolean {
    return check.canRequestClaudeFix && !!this.repoOwner() && !!this.repo();
  }

  checkContextLines(check: DeploymentVerificationCheck): ActivityLine[] {
    const lines = [
      `${check.label} (${check.id})`,
      check.message
    ];
    if (check.url) {
      lines.push(`URL: ${check.url}`);
    }
    if (check.referencedFiles.length > 0) {
      lines.push(`Files: ${check.referencedFiles.join(', ')}`);
    }

    return lines.map((line, index) => ({
      providerName: 'health',
      sequence: index,
      line
    }));
  }

  verificationFixRunner(check: DeploymentVerificationCheck): () => ReturnType<ApiService['generateVerificationFix']> {
    return () => this.api.generateVerificationFix(
      this.deploymentId(),
      check.id,
      this.resolveTargetId(check)
    );
  }

  private resolveTargetId(check: DeploymentVerificationCheck): string | undefined {
    if (check.target === 'server') {
      return this.serverTarget()?.id;
    }
    return this.websiteTarget()?.id;
  }
}
