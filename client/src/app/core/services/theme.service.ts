import { computed, Injectable, signal } from '@angular/core';

const THEME_KEY = 'deployai-theme';
const STYLE_KEY = 'deployai-style';
const ORB_SNAKE_KEY = 'deployai-orb-snake';
const ORB_DRIFTERS_KEY = 'deployai-orb-drifters';

export type ThemeMode = 'light' | 'dark';
export type ThemeStyle = 'default' | 'notebook';
export type AppearancePreset =
  | 'default-light'
  | 'default-dark'
  | 'notebook-light'
  | 'notebook-dark';

@Injectable({ providedIn: 'root' })
export class ThemeService {
  readonly mode = signal<ThemeMode>(this.readStoredTheme());
  readonly style = signal<ThemeStyle>(this.readStoredStyle());
  readonly orbSnakeEnabled = signal<boolean>(this.readStoredOrbSnake());
  readonly orbDriftersEnabled = signal<boolean>(this.readStoredOrbDrifters());
  readonly prefersReducedMotion = signal<boolean>(this.readPrefersReducedMotion());

  readonly showOrbSnake = computed(
    () => this.orbSnakeEnabled() && !this.prefersReducedMotion()
  );

  readonly showOrbDrifters = computed(
    () => this.orbDriftersEnabled() && !this.prefersReducedMotion()
  );

  constructor() {
    this.apply(this.mode(), this.style());
    this.bindReducedMotionListener();
  }

  appearance(): AppearancePreset {
    if (this.style() === 'notebook') {
      return this.mode() === 'light' ? 'notebook-light' : 'notebook-dark';
    }
    return this.mode() === 'light' ? 'default-light' : 'default-dark';
  }

  toggle(): void {
    const next: ThemeMode = this.mode() === 'light' ? 'dark' : 'light';
    this.setMode(next);
  }

  setMode(mode: ThemeMode): void {
    this.mode.set(mode);
    localStorage.setItem(THEME_KEY, mode);
    this.apply(mode, this.style());
  }

  setStyle(style: ThemeStyle): void {
    this.style.set(style);
    localStorage.setItem(STYLE_KEY, style);
    this.apply(this.mode(), style);
  }

  setOrbSnakeEnabled(enabled: boolean): void {
    this.orbSnakeEnabled.set(enabled);
    localStorage.setItem(ORB_SNAKE_KEY, enabled ? 'true' : 'false');
  }

  setOrbDriftersEnabled(enabled: boolean): void {
    this.orbDriftersEnabled.set(enabled);
    localStorage.setItem(ORB_DRIFTERS_KEY, enabled ? 'true' : 'false');
  }

  setAppearance(preset: AppearancePreset): void {
    const map: Record<AppearancePreset, { mode: ThemeMode; style: ThemeStyle }> = {
      'default-light': { mode: 'light', style: 'default' },
      'default-dark': { mode: 'dark', style: 'default' },
      'notebook-light': { mode: 'light', style: 'notebook' },
      'notebook-dark': { mode: 'dark', style: 'notebook' },
    };
    const { mode, style } = map[preset];
    this.mode.set(mode);
    this.style.set(style);
    localStorage.setItem(THEME_KEY, mode);
    localStorage.setItem(STYLE_KEY, style);
    this.apply(mode, style);
  }

  private apply(mode: ThemeMode, style: ThemeStyle): void {
    document.documentElement.setAttribute('data-theme', mode);
    document.documentElement.setAttribute('data-style', style);
  }

  private bindReducedMotionListener(): void {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
      return;
    }

    const query = window.matchMedia('(prefers-reduced-motion: reduce)');
    const update = (): void => {
      this.prefersReducedMotion.set(query.matches);
    };

    update();
    query.addEventListener('change', update);
  }

  private readPrefersReducedMotion(): boolean {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
      return false;
    }

    return window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  }

  private readStoredTheme(): ThemeMode {
    const stored = localStorage.getItem(THEME_KEY);
    if (stored === 'dark' || stored === 'light') {
      return stored;
    }
    return 'dark';
  }

  private readStoredStyle(): ThemeStyle {
    const stored = localStorage.getItem(STYLE_KEY);
    if (stored === 'notebook' || stored === 'default') {
      return stored;
    }
    return 'default';
  }

  private readStoredOrbSnake(): boolean {
    const stored = localStorage.getItem(ORB_SNAKE_KEY);
    if (stored === 'true') {
      return true;
    }
    if (stored === 'false') {
      return false;
    }
    return true;
  }

  private readStoredOrbDrifters(): boolean {
    const stored = localStorage.getItem(ORB_DRIFTERS_KEY);
    if (stored === 'true') {
      return true;
    }
    if (stored === 'false') {
      return false;
    }
    return false;
  }
}
