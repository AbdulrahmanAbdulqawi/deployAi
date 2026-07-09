import {
  AfterViewInit,
  Component,
  DestroyRef,
  ElementRef,
  inject,
  NgZone,
  OnDestroy,
  QueryList,
  ViewChildren,
} from '@angular/core';

interface Point {
  x: number;
  y: number;
}

interface DrifterConfig {
  ampX: number;
  ampY: number;
  freqX: number;
  freqY: number;
  phase: number;
  speed: number;
  sizeRem: number;
  tone: 'idle' | 'working' | 'success';
  depth: 'near' | 'far';
  opacity: number;
}

const DRIFTER_CONFIGS: DrifterConfig[] = [
  {
    ampX: 0.42,
    ampY: 0.34,
    freqX: 1,
    freqY: 1.65,
    phase: 0,
    speed: 0.00075,
    sizeRem: 1.75,
    tone: 'idle',
    depth: 'near',
    opacity: 0.9,
  },
  {
    ampX: 0.38,
    ampY: 0.38,
    freqX: 1.25,
    freqY: 2.05,
    phase: 2.4,
    speed: 0.00055,
    sizeRem: 1.35,
    tone: 'working',
    depth: 'far',
    opacity: 0.6,
  },
  {
    ampX: 0.35,
    ampY: 0.3,
    freqX: 0.85,
    freqY: 1.9,
    phase: 4.2,
    speed: 0.00065,
    sizeRem: 1.5,
    tone: 'success',
    depth: 'near',
    opacity: 0.75,
  },
  {
    ampX: 0.4,
    ampY: 0.32,
    freqX: 1.45,
    freqY: 2.35,
    phase: 1.1,
    speed: 0.00048,
    sizeRem: 1.1,
    tone: 'idle',
    depth: 'far',
    opacity: 0.5,
  },
  {
    ampX: 0.33,
    ampY: 0.36,
    freqX: 1.05,
    freqY: 1.55,
    phase: 5.6,
    speed: 0.00058,
    sizeRem: 1.25,
    tone: 'working',
    depth: 'near',
    opacity: 0.68,
  },
];

function drifterPoint(config: DrifterConfig, time: number): Point {
  const t = time + config.phase;
  return {
    x: 0.5 + config.ampX * Math.sin(config.freqX * t),
    y: 0.5 + config.ampY * Math.sin(config.freqY * t + Math.PI / 5),
  };
}

@Component({
  selector: 'app-orb-drifters-background',
  standalone: true,
  templateUrl: './orb-drifters-background.component.html',
  styleUrl: './orb-drifters-background.component.scss',
})
export class OrbDriftersBackgroundComponent implements AfterViewInit, OnDestroy {
  private readonly destroyRef = inject(DestroyRef);
  private readonly ngZone = inject(NgZone);

  readonly drifters = DRIFTER_CONFIGS;

  @ViewChildren('drifterRef') private drifterRefs!: QueryList<ElementRef<HTMLElement>>;

  private readonly pathTimes = DRIFTER_CONFIGS.map((config) => config.phase);
  private animationFrameId: number | null = null;
  private viewportWidth = 0;
  private viewportHeight = 0;
  private remPx = 16;

  private readonly onResize = (): void => {
    this.updateViewport();
    this.syncDrifterElements();
  };

  private readonly onVisibilityChange = (): void => {
    if (document.visibilityState === 'visible') {
      this.startAnimation();
      return;
    }

    this.stopAnimation();
  };

  ngAfterViewInit(): void {
    this.updateViewport();

    window.addEventListener('resize', this.onResize, { passive: true });
    document.addEventListener('visibilitychange', this.onVisibilityChange);

    this.destroyRef.onDestroy(() => {
      this.stopAnimation();
      window.removeEventListener('resize', this.onResize);
      document.removeEventListener('visibilitychange', this.onVisibilityChange);
    });

    window.requestAnimationFrame(() => {
      this.syncDrifterElements();
      this.startAnimation();
    });
  }

  ngOnDestroy(): void {
    this.stopAnimation();
  }

  toneClass(tone: DrifterConfig['tone']): string {
    return `orb-drifters__orb--${tone}`;
  }

  depthClass(depth: DrifterConfig['depth']): string {
    return depth === 'far' ? 'orb-drifters__orb--far' : 'orb-drifters__orb--near';
  }

  private startAnimation(): void {
    if (this.animationFrameId !== null) {
      return;
    }

    this.ngZone.runOutsideAngular(() => {
      const tick = (): void => {
        this.step();
        this.animationFrameId = window.requestAnimationFrame(tick);
      };

      this.animationFrameId = window.requestAnimationFrame(tick);
    });
  }

  private stopAnimation(): void {
    if (this.animationFrameId === null) {
      return;
    }

    window.cancelAnimationFrame(this.animationFrameId);
    this.animationFrameId = null;
  }

  private step(): void {
    DRIFTER_CONFIGS.forEach((config, index) => {
      this.pathTimes[index] += config.speed;
    });
    this.syncDrifterElements();
  }

  private syncDrifterElements(): void {
    const elements = this.drifterRefs?.toArray() ?? [];
    if (elements.length === 0) {
      return;
    }

    const themeOpacity = this.themeOpacityMultiplier();

    elements.forEach((elementRef, index) => {
      const config = DRIFTER_CONFIGS[index];
      if (!config) {
        return;
      }

      const element = elementRef.nativeElement;
      const point = this.toPixels(drifterPoint(config, this.pathTimes[index] ?? 0));
      const sizePx = config.sizeRem * this.remPx;
      const x = point.x - sizePx / 2;
      const y = point.y - sizePx / 2;
      element.style.transform = `translate3d(${x}px, ${y}px, 0)`;
      element.style.opacity = `${config.opacity * themeOpacity}`;
    });
  }

  private themeOpacityMultiplier(): number {
    const theme = document.documentElement.getAttribute('data-theme');
    const style = document.documentElement.getAttribute('data-style');
    if (style === 'notebook') {
      return theme === 'light' ? 1.15 : 0.9;
    }
    return theme === 'light' ? 1.25 : 1;
  }

  private toPixels(point: Point): Point {
    return {
      x: point.x * this.viewportWidth,
      y: point.y * this.viewportHeight,
    };
  }

  private updateViewport(): void {
    this.viewportWidth = window.innerWidth;
    this.viewportHeight = window.innerHeight;
    this.remPx = Number.parseFloat(getComputedStyle(document.documentElement).fontSize) || 16;
  }
}
