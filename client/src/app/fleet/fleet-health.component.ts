import { Component, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ApiService } from '../core/services/api.service';
import {
  FleetCheckState,
  FleetProjectHealth,
  ProjectHealthStatus,
  VerificationCheckStatus
} from '../core/models/api.models';
import { IconComponent, IconName, IconStatusClass } from '../shared/ui/icon/icon.component';

/**
 * Every deployed app on one page, with what DeployAI last learned about each.
 *
 * The distinction this screen exists to preserve is between a check that failed and a check that
 * could not run. They are rendered differently on purpose: failures are the user's to act on,
 * whereas "couldn't check" is DeployAI reporting its own blind spot and must never be dressed up as
 * a verdict about someone's app.
 */
@Component({
  selector: 'app-fleet-health',
  standalone: true,
  imports: [DatePipe, IconComponent],
  templateUrl: './fleet-health.component.html',
  styleUrl: './fleet-health.component.scss'
})
export class FleetHealthComponent {
  private readonly api = inject(ApiService);

  readonly loading = signal(true);
  readonly sweeping = signal(false);
  readonly error = signal<string | null>(null);
  readonly lastSweepAt = signal<string | null>(null);
  readonly projects = signal<FleetProjectHealth[]>([]);
  readonly expanded = signal<Set<string>>(new Set());

  readonly totalFailing = computed(() =>
    this.projects().filter((p) => p.failed > 0).length
  );

  /** Counted separately from failures — an app nobody could check is not an app known to be broken. */
  readonly totalUncheckable = computed(() =>
    this.projects().filter((p) => p.failed === 0 && p.inconclusive > 0).length
  );

  constructor() {
    this.load();
  }

  load(): void {
    this.loading.set(true);
    this.api.getFleetHealth().subscribe({
      next: (response) => {
        this.projects.set(response.projects);
        this.lastSweepAt.set(response.lastSweepAt ?? null);
        this.error.set(null);
        this.loading.set(false);
      },
      error: () => {
        this.error.set("Couldn't load your apps' health.");
        this.loading.set(false);
      }
    });
  }

  /** Re-checks now. Waiting an hour to find out whether a fix worked makes the fix untestable. */
  sweepNow(): void {
    this.sweeping.set(true);
    this.api.runFleetSweep().subscribe({
      next: () => {
        // The sweep is queued, not finished — reloading immediately would show the old picture, so
        // the button stays busy and the user reloads when they choose.
        this.sweeping.set(false);
      },
      error: () => {
        this.error.set('Could not start the check.');
        this.sweeping.set(false);
      }
    });
  }

  toggle(projectId: string): void {
    const next = new Set(this.expanded());
    if (next.has(projectId)) {
      next.delete(projectId);
    } else {
      next.add(projectId);
    }
    this.expanded.set(next);
  }

  isExpanded(projectId: string): boolean {
    return this.expanded().has(projectId);
  }

  projectStatusClass(status: ProjectHealthStatus): IconStatusClass {
    switch (status) {
      case ProjectHealthStatus.Healthy:
        return 'success';
      case ProjectHealthStatus.Degraded:
        return 'warning';
      case ProjectHealthStatus.Down:
        return 'danger';
      default:
        return 'muted';
    }
  }

  projectStatusLabel(status: ProjectHealthStatus): string {
    switch (status) {
      case ProjectHealthStatus.Healthy:
        return 'Healthy';
      case ProjectHealthStatus.Degraded:
        return 'Needs attention';
      case ProjectHealthStatus.Down:
        return 'Down';
      case ProjectHealthStatus.Inconclusive:
        return "Couldn't check";
      default:
        return 'Not checked yet';
    }
  }

  projectStatusIcon(status: ProjectHealthStatus): IconName {
    switch (status) {
      case ProjectHealthStatus.Healthy:
        return 'check';
      case ProjectHealthStatus.Degraded:
      case ProjectHealthStatus.Down:
        return 'alert';
      default:
        return 'info';
    }
  }

  /**
   * A failed check gets the alert glyph; one that could not run gets the neutral info glyph. The
   * shape carries the distinction even for a reader who is not reading the colour.
   */
  checkStatusIcon(status: VerificationCheckStatus): IconName {
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

  checkStatusClass(status: VerificationCheckStatus): IconStatusClass {
    switch (status) {
      case 'passed':
        return 'success';
      case 'failed':
        return 'danger';
      case 'warning':
        return 'warning';
      // Both 'skipped' and 'inconclusive' are muted rather than red. They are told apart by their
      // label and their message, not by borrowing the colour of a real failure.
      default:
        return 'muted';
    }
  }

  checkStatusLabel(status: VerificationCheckStatus): string {
    switch (status) {
      case 'passed':
        return 'Passed';
      case 'failed':
        return 'Failed';
      case 'warning':
        return 'Warning';
      case 'skipped':
        return "Doesn't apply";
      case 'inconclusive':
        return "Couldn't check";
      default:
        return status;
    }
  }

  /**
   * Whether this check has been stuck on the same conclusive answer long enough to be worth saying
   * so. A failure that started this morning and one that has been failing for three weeks call for
   * different reactions, and the current status alone cannot tell them apart.
   */
  showsChangedAt(check: FleetCheckState): boolean {
    return check.status === 'failed' || check.status === 'warning';
  }

  /** How long DeployAI has been unable to run this check, when that is the situation. */
  blindFor(check: FleetCheckState): number {
    return check.status === 'inconclusive' ? check.consecutiveInconclusive : 0;
  }

  trackProject(_: number, project: FleetProjectHealth): string {
    return project.projectId;
  }

  trackCheck(_: number, check: FleetCheckState): string {
    return check.checkId;
  }
}
