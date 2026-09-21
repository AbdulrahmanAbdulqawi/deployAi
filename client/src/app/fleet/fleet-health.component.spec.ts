import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { ApiService } from '../core/services/api.service';
import {
  FleetCheckState,
  FleetProjectHealth,
  ProjectHealthStatus
} from '../core/models/api.models';
import { FleetHealthComponent } from './fleet-health.component';

/**
 * These assert that the inconclusive styling is WIRED TO A TOKEN, not that it is any particular
 * colour — the same reason the enum-converter test asserts the converter is registered rather than
 * one enum's output. A palette change should be free; silently falling off the palette should not.
 *
 * They exist because this stylesheet referenced eight custom properties that were never defined
 * (`--danger`, `--success`, `--border`, `--muted-bg`, …). Every reference carried a hardcoded
 * fallback, so `var(--danger, #b91c1c)` resolved to `#b91c1c` on every render and the screen looked
 * deliberate while being connected to nothing. Nothing failed, because a CSS fallback cannot fail —
 * it is the same invisible-fallback shape as a `catch` that returns the unsafe answer.
 */
describe('FleetHealthComponent inconclusive styling', () => {
  let fixture: ComponentFixture<FleetHealthComponent>;

  /** Resolves a colour the way the browser does, so both sides of a comparison are in one format. */
  const asRenderedColour = (value: string): string => {
    const probe = document.createElement('span');
    probe.style.color = value;
    document.body.appendChild(probe);
    const resolved = getComputedStyle(probe).color;
    probe.remove();
    return resolved;
  };

  const token = (name: string): string =>
    getComputedStyle(document.documentElement).getPropertyValue(name).trim();

  const inconclusiveCheck: FleetCheckState = {
    checkId: 'live-urls',
    label: 'Website reachable',
    target: 'website',
    status: 'inconclusive',
    message: 'The Vercel token was rejected, so this check could not run.',
    statusChangedAt: '2026-09-20T09:00:00Z',
    lastObservedAt: '2026-09-21T09:00:00Z',
    consecutiveFailures: 0,
    consecutiveInconclusive: 3,
    lastConclusiveStatus: 'passed',
    lastConclusiveAt: '2026-09-18T09:00:00Z'
  };

  const project: FleetProjectHealth = {
    projectId: 'p1',
    name: 'sana-studio',
    status: ProjectHealthStatus.Inconclusive,
    passed: 2,
    failed: 0,
    warning: 0,
    inconclusive: 1,
    skipped: 0,
    checks: [inconclusiveCheck]
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [FleetHealthComponent],
      providers: [
        {
          provide: ApiService,
          useValue: {
            getFleetHealth: () => of({ lastSweepAt: null, projects: [project] }),
            runFleetSweep: () => of({})
          }
        }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(FleetHealthComponent);
    fixture.detectChanges();
    // The checks list only renders once its project is expanded.
    fixture.componentInstance.toggle('p1');
    fixture.detectChanges();
  });

  it('defines the tokens the inconclusive state depends on', () => {
    expect(token('--status-unknown')).not.toBe('');
    expect(token('--status-unknown-bg')).not.toBe('');
    expect(token('--border-unknown')).not.toBe('');
  });

  it('draws an inconclusive tally from the unknown tokens, not a hardcoded grey', () => {
    const tally: HTMLElement = fixture.nativeElement.querySelector('.tally--muted');
    expect(tally).withContext('an inconclusive tally should be rendered').toBeTruthy();

    const styles = getComputedStyle(tally);
    expect(styles.backgroundColor).toBe(asRenderedColour(token('--status-unknown-bg')));
    expect(styles.color).toBe(asRenderedColour(token('--status-unknown')));
  });

  it('marks an inconclusive check with the unknown border token', () => {
    const item: HTMLElement = fixture.nativeElement.querySelector('.checks__item--muted');
    expect(item).withContext('an inconclusive check row should be rendered').toBeTruthy();

    expect(getComputedStyle(item).borderLeftColor).toBe(asRenderedColour(token('--border-unknown')));
  });

  /**
   * The distinction the whole screen exists to preserve. If these two ever resolve to the same
   * colour, "DeployAI could not look" has become indistinguishable from "your app is broken".
   */
  it('never renders an inconclusive check in the failure colour', () => {
    const item: HTMLElement = fixture.nativeElement.querySelector('.checks__item--muted');
    const failureColour = asRenderedColour(token('--status-danger'));

    expect(getComputedStyle(item).borderLeftColor).not.toBe(failureColour);
  });
});
