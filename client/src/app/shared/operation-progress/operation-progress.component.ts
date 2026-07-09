import { Component, computed, input } from '@angular/core';
import { formatDurationMs, formatElapsedMs } from '../../core/utils/duration';

export type OperationProgressMode = 'indeterminate' | 'determinate' | 'complete' | 'failed';

@Component({
  selector: 'app-operation-progress',
  standalone: true,
  templateUrl: './operation-progress.component.html',
  styleUrl: './operation-progress.component.scss'
})
export class OperationProgressComponent {
  readonly mode = input<OperationProgressMode>('indeterminate');
  readonly value = input(0);
  readonly label = input<string | null>(null);
  readonly elapsedMs = input<number | null>(null);
  readonly durationMs = input<number | null>(null);
  readonly showElapsed = input(true);
  readonly showDuration = input(true);
  readonly compact = input(false);

  readonly timingLabel = computed(() => {
    const duration = this.durationMs();
    if (this.showDuration() && duration !== null && duration !== undefined && this.mode() !== 'indeterminate') {
      return `Finished in ${formatDurationMs(duration)}`;
    }

    const elapsed = this.elapsedMs();
    if (this.showElapsed() && elapsed !== null && elapsed !== undefined && this.mode() !== 'complete' && this.mode() !== 'failed') {
      return `Elapsed ${formatElapsedMs(elapsed)}`;
    }

    return null;
  });

  readonly fillWidth = computed(() => {
    if (this.mode() === 'determinate') {
      return `${Math.min(100, Math.max(0, this.value()))}%`;
    }
    if (this.mode() === 'complete' || this.mode() === 'failed') {
      return '100%';
    }
    return undefined;
  });
}
