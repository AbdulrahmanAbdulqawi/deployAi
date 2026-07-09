import { Injectable, NgZone, signal } from '@angular/core';

export class ElapsedTimer {
  readonly elapsedMs = signal(0);

  private intervalId?: ReturnType<typeof setInterval>;
  private anchorMs = 0;
  private frozenMs = 0;
  private running = false;

  constructor(private readonly ngZone: NgZone) {}

  start(fromTimestamp?: string | number | Date): void {
    this.stopInterval();
    this.running = true;
    this.frozenMs = 0;
    this.anchorMs = fromTimestamp !== undefined
      ? typeof fromTimestamp === 'number'
        ? fromTimestamp
        : new Date(fromTimestamp).getTime()
      : Date.now();
    this.elapsedMs.set(Math.max(0, Date.now() - this.anchorMs));
    this.ngZone.runOutsideAngular(() => {
      this.intervalId = setInterval(() => {
        this.ngZone.run(() => {
          if (this.running) {
            this.elapsedMs.set(Math.max(0, Date.now() - this.anchorMs));
          }
        });
      }, 1000);
    });
  }

  stop(): number {
    this.running = false;
    this.frozenMs = Math.max(0, Date.now() - this.anchorMs);
    this.elapsedMs.set(this.frozenMs);
    this.stopInterval();
    return this.frozenMs;
  }

  reset(): void {
    this.running = false;
    this.anchorMs = 0;
    this.frozenMs = 0;
    this.elapsedMs.set(0);
    this.stopInterval();
  }

  snapshotDurationMs(): number {
    return this.running ? Math.max(0, Date.now() - this.anchorMs) : this.frozenMs;
  }

  destroy(): void {
    this.reset();
  }

  private stopInterval(): void {
    if (this.intervalId !== undefined) {
      clearInterval(this.intervalId);
      this.intervalId = undefined;
    }
  }
}

@Injectable({ providedIn: 'root' })
export class ElapsedTimerService {
  constructor(private readonly ngZone: NgZone) {}

  create(): ElapsedTimer {
    return new ElapsedTimer(this.ngZone);
  }
}
