import { Component, computed, inject, input, output } from '@angular/core';
import { ApiService } from '../../core/services/api.service';
import { DeploymentFailureAnalysis } from '../../core/models/api.models';
import { IconComponent } from '../ui/icon/icon.component';
import { ActivityLine } from '../live-log-panel/live-log-panel.component';
import { DeploymentRepoChangesFlowComponent } from '../deployment-repo-changes-flow/deployment-repo-changes-flow.component';

@Component({
  selector: 'app-deployment-fix-panel',
  standalone: true,
  imports: [IconComponent, DeploymentRepoChangesFlowComponent],
  templateUrl: './deployment-fix-panel.component.html',
  styleUrl: './deployment-fix-panel.component.scss'
})
export class DeploymentFixPanelComponent {
  readonly deploymentId = input.required<string>();
  readonly targetId = input.required<string>();
  readonly projectId = input.required<string>();
  readonly owner = input.required<string>();
  readonly repo = input.required<string>();
  readonly baseBranch = input<string | null>(null);
  readonly failureAnalysis = input.required<DeploymentFailureAnalysis>();

  readonly fixComplete = output<void>();

  private readonly api = inject(ApiService);

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

  readonly runGenerate = () =>
    this.api.generateDeploymentFix(this.deploymentId(), this.targetId());
}
