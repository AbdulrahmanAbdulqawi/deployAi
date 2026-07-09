import { Component, inject, input, output, signal } from '@angular/core';
import { ApiService } from '../../core/services/api.service';
import { AiSetupPreferenceService } from '../../core/services/ai-setup-preference.service';
import {
  DeploymentPlanPart,
  DeploymentReadinessResult,
  DeploymentSetupMergeResult,
  MissingDeploymentFile
} from '../../core/models/api.models';
import { IconComponent } from '../ui/icon/icon.component';
import { DeploymentRepoChangesFlowComponent } from '../deployment-repo-changes-flow/deployment-repo-changes-flow.component';

@Component({
  selector: 'app-deployment-setup-panel',
  standalone: true,
  imports: [IconComponent, DeploymentRepoChangesFlowComponent],
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
  readonly mergeResult = signal<DeploymentSetupMergeResult | null>(null);
  readonly error = signal<string | null>(null);

  private readonly api = inject(ApiService);
  private readonly aiSetup = inject(AiSetupPreferenceService);

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

  readonly runGenerate = () =>
    this.api.generateDeploymentSetup(
      this.owner(),
      this.repo(),
      this.gitRef(),
      this.parts(),
      this.projectId() ?? undefined,
      false,
      this.aiSetup.enabled()
    );

  readonly runRegenerate = () =>
    this.api.generateDeploymentSetup(
      this.owner(),
      this.repo(),
      this.gitRef(),
      this.parts(),
      this.projectId() ?? undefined,
      true,
      this.aiSetup.enabled()
    );

  onMergeComplete(result: DeploymentSetupMergeResult | void): void {
    if (result && typeof result === 'object' && 'merged' in result) {
      this.mergeResult.set(result);
      this.setupComplete.emit();
    }
  }

  onDeployStarted(): void {
    this.setupComplete.emit();
  }
}
