import { Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { DeploymentReadinessResult } from '../../core/models/api.models';
import {
  buildReadinessScorecard,
  ReadinessCheckStatus
} from '../../core/utils/readiness-scorecard';
import { IconComponent, IconStatusClass } from '../ui/icon/icon.component';

@Component({
  selector: 'app-readiness-scorecard',
  standalone: true,
  imports: [RouterLink, IconComponent],
  templateUrl: './readiness-scorecard.component.html',
  styleUrl: './readiness-scorecard.component.scss'
})
export class ReadinessScorecardComponent {
  readonly readiness = input<DeploymentReadinessResult | null>(null);
  readonly projectId = input<string | null>(null);
  readonly loading = input(false);
  readonly compact = input(false);

  readonly scorecard = computed(() => buildReadinessScorecard(this.readiness()));

  readonly headline = computed(() => {
    const summary = this.scorecard();
    if (!summary.visible) {
      return '';
    }

    if (!summary.isReady) {
      return summary.blockingCount === 1
        ? '1 required fix before publish'
        : `${summary.blockingCount} required fixes before publish`;
    }

    if (summary.scorePercent < 100) {
      return 'Ready to publish with optional improvements';
    }

    return 'Ready to publish';
  });

  statusIcon(status: ReadinessCheckStatus): IconStatusClass {
    switch (status) {
      case ReadinessCheckStatus.Passed:
        return 'success';
      case ReadinessCheckStatus.Failed:
        return 'danger';
      case ReadinessCheckStatus.Warning:
        return 'warning';
      default:
        return 'muted';
    }
  }

  statusLabel(status: ReadinessCheckStatus): string {
    switch (status) {
      case ReadinessCheckStatus.Passed:
        return 'Ready';
      case ReadinessCheckStatus.Failed:
        return 'Required';
      case ReadinessCheckStatus.Warning:
        return 'Recommended';
      default:
        return 'Skipped';
    }
  }
}
