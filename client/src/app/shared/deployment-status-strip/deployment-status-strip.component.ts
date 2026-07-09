import { Component, computed, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { DeploymentReadinessResult } from '../../core/models/api.models';
import { IconComponent, IconStatusClass } from '../ui/icon/icon.component';

export type DeploymentStatusStripState = 'blocking' | 'recommended' | 'hidden';

@Component({
  selector: 'app-deployment-status-strip',
  standalone: true,
  imports: [RouterLink, IconComponent],
  templateUrl: './deployment-status-strip.component.html',
  styleUrl: './deployment-status-strip.component.scss'
})
export class DeploymentStatusStripComponent {
  readonly readiness = input<DeploymentReadinessResult | null>(null);
  readonly projectId = input.required<string>();
  readonly aiSetupEnabled = input(true);
  readonly loading = input(false);

  readonly state = computed((): DeploymentStatusStripState => {
    if (!this.aiSetupEnabled()) {
      return 'hidden';
    }

    const readiness = this.readiness();
    if (!readiness?.usesSplitOrigin) {
      return 'hidden';
    }

    if (!readiness.isReady) {
      return 'blocking';
    }

    const optionalCount = readiness.missingFiles.filter(
      file => file.severity === 'recommended' || file.severity === 'warning'
    ).length;

    if (optionalCount > 0) {
      return 'recommended';
    }

    return 'hidden';
  });

  readonly title = computed(() => {
    switch (this.state()) {
      case 'blocking':
        return 'Setup required before publish';
      case 'recommended': {
        const count = this.readiness()?.missingFiles.filter(
          file => file.severity === 'recommended' || file.severity === 'warning'
        ).length ?? 0;
        return count === 1 ? '1 optional improvement' : `${count} optional improvements`;
      }
      default:
        return '';
    }
  });

  readonly description = computed(() => {
    switch (this.state()) {
      case 'blocking':
        return 'Split-origin deployment files are missing or need updating.';
      case 'recommended':
        return 'Review recommended setup files to improve deployment reliability.';
      default:
        return '';
    }
  });

  readonly iconStatus = computed((): IconStatusClass => {
    switch (this.state()) {
      case 'blocking':
        return 'warning';
      case 'recommended':
        return 'accent';
      default:
        return 'muted';
    }
  });

  readonly ctaLabel = computed(() =>
    this.state() === 'blocking' ? 'Open troubleshooting' : 'Review'
  );
}
