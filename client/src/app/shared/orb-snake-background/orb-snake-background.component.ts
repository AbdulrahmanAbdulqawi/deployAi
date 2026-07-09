import {
  AfterViewInit,
  ChangeDetectorRef,
  Component,
  computed,
  DestroyRef,
  ElementRef,
  inject,
  NgZone,
  OnDestroy,
  QueryList,
  signal,
  ViewChildren,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import {
  afterCelebration,
  applyStun,
  bounceFromWall,
  canEat,
  createInitialProgress,
  createFallingSnackWave,
  computeGameSpeedProfile,
  FallingSnack,
  fallSnack,
  FOOD_MARGIN_PX,
  GameProgress,
  GameSpeedProfile,
  hasFilledScreen,
  hasSnackFallenOff,
  hitsSelf,
  INITIAL_SEGMENT_COUNT,
  isSelfCollisionEnabled,
  isSnackFalling,
  maxSegmentsForViewport,
  nearestSnack,
  onDecoyEaten,
  onFoodEaten,
  onScreenFilled,
  onSelfCollision,
  onSnackMissed,
  Point,
  positionSegmentsFromHistory,
  randomSnackWaveCount,
  recoverFromStun,
  segmentSpacingPx,
  snackBottomY,
  speedForDelta,
  steerToward,
  STUN_FRAMES_DECOY,
  STUN_FRAMES_MISS,
  STUN_FRAMES_SELF,
  TARGET_FRAME_MS,
  tickSnackFallDelay,
} from './orb-snake-game';

const HEAD_SIZE_REM = 2.25;
const TAIL_SCALE = 0.65;
const FOOD_SIZE_REM = 1.35;
const CELEBRATION_FRAMES = 54;
const HEAD_HISTORY_PADDING = 16;

function lerp(a: number, b: number, factor: number): number {
  return a + (b - a) * factor;
}

function debugAgentLog(
  location: string,
  message: string,
  data: Record<string, unknown>,
  hypothesisId: string
): void {
  // #region agent log
  fetch('http://127.0.0.1:7621/ingest/cf701369-72fb-4b43-b17f-7654957025c3', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Debug-Session-Id': 'b41937' },
    body: JSON.stringify({
      sessionId: 'b41937',
      location,
      message,
      data,
      hypothesisId,
      timestamp: Date.now(),
      runId: 'verification',
    }),
  }).catch(() => {});
  // #endregion
}

@Component({
  selector: 'app-orb-snake-background',
  standalone: true,
  templateUrl: './orb-snake-background.component.html',
  styleUrl: './orb-snake-background.component.scss',
})
export class OrbSnakeBackgroundComponent implements AfterViewInit, OnDestroy {
  private readonly destroyRef = inject(DestroyRef);
  private readonly ngZone = inject(NgZone);
  private readonly cdr = inject(ChangeDetectorRef);

  readonly segmentCount = signal(INITIAL_SEGMENT_COUNT);
  readonly segmentIndexes = computed(() =>
    Array.from({ length: this.segmentCount() }, (_, index) => index)
  );
  readonly fallingSnacks = signal<FallingSnack[]>([]);
  readonly headStunned = signal(false);

  @ViewChildren('snackRef') private snackRefs!: QueryList<ElementRef<HTMLElement>>;
  @ViewChildren('segmentRef') private segmentRefs!: QueryList<ElementRef<HTMLElement>>;

  private readonly positions: Point[] = [];
  private readonly headHistory: Point[] = [];
  private readonly snackStates: FallingSnack[] = [];
  private gameProgress: GameProgress = createInitialProgress();
  private celebrationFrames = 0;
  private stunFrames = 0;
  private spawnIndex = 0;
  private animationFrameId: number | null = null;
  private lastFrameTime = 0;
  private speedProfile: GameSpeedProfile = computeGameSpeedProfile(0, 0);
  private viewportWidth = 0;
  private viewportHeight = 0;
  private remPx = 16;
  private debugFrame = 0;
  private lastLoggedSegmentCount = INITIAL_SEGMENT_COUNT;

  private readonly onResize = (): void => {
    this.updateViewport();
    this.recomputeSpeeds();
    if (this.snackStates.length > 0) {
      this.spawnSnackWave();
    }
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
    this.recomputeSpeeds();
    this.resetSnakeBody();

    window.addEventListener('resize', this.onResize, { passive: true });
    document.addEventListener('visibilitychange', this.onVisibilityChange);

    this.destroyRef.onDestroy(() => {
      this.stopAnimation();
      window.removeEventListener('resize', this.onResize);
      document.removeEventListener('visibilitychange', this.onVisibilityChange);
    });

    this.snackRefs.changes.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => {
      this.syncSnackElements();
    });

    window.requestAnimationFrame(() => {
      this.spawnSnackWave();
      this.syncSegmentElements();
      this.startAnimation();
    });
  }

  ngOnDestroy(): void {
    this.stopAnimation();
  }

  toneClass(index: number): string {
    const tones = ['idle', 'working', 'success'] as const;
    return `orb-snake__segment--${tones[index % tones.length] ?? 'idle'}`;
  }

  private resetSnakeBody(): void {
    const spacing = this.segmentSpacing();
    const startHead = {
      x: this.viewportWidth * 0.2,
      y: this.viewportHeight * 0.5,
    };

    this.positions.length = 0;
    this.headHistory.length = 0;

    for (let index = 0; index < INITIAL_SEGMENT_COUNT; index += 1) {
      const point = {
        x: startHead.x - index * spacing,
        y: startHead.y,
      };
      this.positions.push({ ...point });
      if (index > 0) {
        this.headHistory.push({ ...point });
      }
    }

    this.snackStates.length = 0;

    this.segmentCount.set(INITIAL_SEGMENT_COUNT);
    this.gameProgress = createInitialProgress();
    this.recomputeSpeeds();
    this.spawnIndex = 0;
    this.stunFrames = 0;
    this.headStunned.set(false);
    this.publishSnacks();
  }

  private publishSnacks(): void {
    this.fallingSnacks.set(
      this.snackStates
        .filter(snack => isSnackFalling(snack))
        .map(snack => ({
          ...snack,
          position: { ...snack.position },
        }))
    );

    this.ngZone.run(() => {
      this.cdr.detectChanges();
      this.syncSnackElements();
    });
  }

  private recomputeSpeeds(): void {
    this.speedProfile = computeGameSpeedProfile(
      this.viewportWidth,
      this.viewportHeight,
      FOOD_MARGIN_PX,
      this.gameProgress.foodsEaten
    );
  }

  private spawnSnackWave(): void {
    const count = randomSnackWaveCount();

    const wave = createFallingSnackWave(
      count,
      this.viewportWidth,
      this.viewportHeight,
      this.positions,
      this.headHistory,
      this.spawnIndex,
      this.gameProgress.foodsEaten,
      this.speedProfile,
      FOOD_MARGIN_PX
    );

    this.spawnIndex += count;
    this.snackStates.length = 0;
    this.snackStates.push(...wave);
    this.publishSnacks();
  }

  private applyMissedSnack(isDecoy: boolean): void {
    if (isDecoy) {
      return;
    }

    const before = this.positions.length;
    this.gameProgress = onSnackMissed(this.gameProgress, false);
    debugAgentLog(
      'orb-snake-background.component.ts:applyMissedSnack',
      'missed food penalty applied without shrinking snake',
      {
        beforeSegments: before,
        afterSegments: this.positions.length,
        gameSegmentCount: this.gameProgress.segmentCount,
        foodsEaten: this.gameProgress.foodsEaten,
      },
      'B'
    );
    this.triggerStun(STUN_FRAMES_MISS);
  }

  private maxHeadHistoryLength(): number {
    const headStep = speedForDelta(this.speedProfile.headPxPerSecond, TARGET_FRAME_MS);
    const segmentCount = Math.max(this.positions.length, INITIAL_SEGMENT_COUNT);
    return (
      Math.ceil((segmentCount * this.segmentSpacing()) / Math.max(headStep, 0.01)) + HEAD_HISTORY_PADDING
    );
  }

  private endRun(reason: 'screen-full'): void {
    debugAgentLog(
      'orb-snake-background.component.ts:endRun',
      'run ended — celebrating',
      { reason, segmentCount: this.positions.length },
      'F'
    );
    this.snackStates.length = 0;
    this.publishSnacks();
    this.celebrationFrames = CELEBRATION_FRAMES;
  }

  private handleRunComplete(): void {
    this.gameProgress = onScreenFilled(this.gameProgress);
    this.endRun('screen-full');
  }

  private isCelebrating(): boolean {
    return this.gameProgress.phase === 'celebrating';
  }

  private segmentSpacing(): number {
    return segmentSpacingPx(this.remPx, HEAD_SIZE_REM);
  }

  private maxSegments(): number {
    return maxSegmentsForViewport(
      this.viewportWidth,
      this.viewportHeight,
      FOOD_MARGIN_PX,
      this.segmentSpacing()
    );
  }

  private isScreenFull(): boolean {
    return hasFilledScreen(
      this.positions.length,
      this.viewportWidth,
      this.viewportHeight,
      FOOD_MARGIN_PX,
      this.segmentSpacing()
    );
  }

  private growSnake(): void {
    if (this.positions.length >= this.maxSegments()) {
      return;
    }

    const before = this.positions.length;
    const tail = this.positions[this.positions.length - 1] ?? this.positions[0];
    this.positions.push({ ...tail });
    this.segmentCount.set(this.positions.length);
    this.gameProgress = {
      ...this.gameProgress,
      segmentCount: this.positions.length,
    };
    debugAgentLog(
      'orb-snake-background.component.ts:growSnake',
      'snake grew after eating',
      { beforeSegments: before, afterSegments: this.positions.length },
      'C'
    );
  }

  private shrinkSnakeToCount(targetCount: number): void {
    const before = this.positions.length;
    while (this.positions.length > targetCount) {
      this.positions.pop();
    }

    this.segmentCount.set(this.positions.length);
    if (before !== this.positions.length) {
      debugAgentLog(
        'orb-snake-background.component.ts:shrinkSnakeToCount',
        'segment count reduced',
        { before, after: this.positions.length, targetCount },
        'D'
      );
    }
  }

  private relayoutTailFromHead(): void {
    const head = this.positions[0];
    if (!head) {
      return;
    }

    const spacing = this.segmentSpacing();
    const retreatPoint = this.headHistory[0];
    let dirX = head.x - (retreatPoint?.x ?? head.x - spacing);
    let dirY = head.y - (retreatPoint?.y ?? head.y);
    const dirLength = Math.hypot(dirX, dirY);

    if (dirLength < 0.01) {
      dirX = 1;
      dirY = 0;
    } else {
      dirX /= dirLength;
      dirY /= dirLength;
    }

    for (let index = 1; index < this.positions.length; index += 1) {
      const previous = this.positions[index - 1] ?? head;
      this.positions[index] = {
        x: previous.x - dirX * spacing,
        y: previous.y - dirY * spacing,
      };
    }

    this.headHistory.length = 0;
    for (let index = 1; index < this.positions.length; index += 1) {
      const point = this.positions[index];
      if (point) {
        this.headHistory.push({ ...point });
      }
    }
  }

  private handleSelfCollision(): void {
    const before = this.positions.length;
    this.gameProgress = onSelfCollision(this.gameProgress);
    this.shrinkSnakeToCount(this.gameProgress.segmentCount);
    this.relayoutTailFromHead();
    this.recomputeSpeeds();
    this.triggerStun(STUN_FRAMES_SELF);
    debugAgentLog(
      'orb-snake-background.component.ts:handleSelfCollision',
      'self-collision setback applied',
      {
        beforeSegments: before,
        afterSegments: this.positions.length,
        foodsEaten: this.gameProgress.foodsEaten,
      },
      'F'
    );
  }

  private shrinkSnakeToInitial(): void {
    const head = this.positions[0] ?? {
      x: this.viewportWidth * 0.2,
      y: this.viewportHeight * 0.5,
    };
    const spacing = this.segmentSpacing();

    this.shrinkSnakeToCount(INITIAL_SEGMENT_COUNT);
    this.positions[0] = { ...head };
    this.headHistory.length = 0;

    for (let index = 1; index < this.positions.length; index += 1) {
      const point = {
        x: head.x - index * spacing,
        y: head.y,
      };
      this.positions[index] = { ...point };
      this.headHistory.push({ ...point });
    }
  }

  private recordHeadHistory(head: Point): void {
    this.headHistory.unshift({ ...head });
    const maxLength = this.maxHeadHistoryLength();
    while (this.headHistory.length > maxLength) {
      this.headHistory.pop();
    }
  }

  private triggerStun(frames: number): void {
    const stunned = applyStun(this.gameProgress, frames);
    this.gameProgress = stunned.progress;
    this.stunFrames = stunned.frames;
    this.headStunned.set(true);
  }

  private moveHeadTowardTarget(head: Point, target: Point, speed: number): Point {
    const next = steerToward(head, target, this.positions, speed);
    return bounceFromWall(next, this.viewportWidth, this.viewportHeight, FOOD_MARGIN_PX);
  }

  private startAnimation(): void {
    if (this.animationFrameId !== null) {
      return;
    }

    this.ngZone.runOutsideAngular(() => {
      const tick = (timestamp: number): void => {
        this.step(timestamp);
        this.animationFrameId = window.requestAnimationFrame(tick);
      };

      this.lastFrameTime = 0;
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

  private updateFallingSnacks(deltaMs: number): void {
    const bottomY = snackBottomY(this.viewportHeight, FOOD_MARGIN_PX);
    const fallSpeed = speedForDelta(this.speedProfile.snackFallPxPerSecond, deltaMs);
    const remaining: FallingSnack[] = [];
    let missedRealSnack = false;
    let visibilityChanged = false;

    for (const snack of this.snackStates) {
      if (!isSnackFalling(snack)) {
        if (tickSnackFallDelay(snack, deltaMs)) {
          visibilityChanged = true;
        }

        remaining.push(snack);
        continue;
      }

      snack.position = fallSnack(snack.position, fallSpeed, bottomY);

      if (hasSnackFallenOff(snack.position, bottomY)) {
        if (!snack.isDecoy) {
          missedRealSnack = true;
        }
        continue;
      }

      remaining.push(snack);
    }

    if (missedRealSnack && this.gameProgress.phase === 'hunting') {
      this.applyMissedSnack(false);
    }

    this.snackStates.length = 0;
    this.snackStates.push(...remaining);

    if (visibilityChanged) {
      this.publishSnacks();
    }

    if (this.snackStates.length === 0 && this.gameProgress.phase === 'hunting') {
      this.spawnSnackWave();
    }
  }

  private step(timestamp: number): void {
    const deltaMs = this.lastFrameTime > 0 ? timestamp - this.lastFrameTime : TARGET_FRAME_MS;
    this.lastFrameTime = timestamp;
    const headSpeed = speedForDelta(this.speedProfile.headPxPerSecond, deltaMs);

    if (this.gameProgress.phase === 'celebrating') {
      this.celebrationFrames -= 1;
      if (this.celebrationFrames <= 0) {
        this.finishCelebration();
      }

      this.syncSegmentElements();
      return;
    }

    if (this.gameProgress.phase === 'stunned') {
      this.stunFrames -= 1;
      if (this.stunFrames <= 0) {
        this.gameProgress = recoverFromStun(this.gameProgress);
        this.headStunned.set(false);
      }

      this.updateFallingSnacks(deltaMs);
      this.updateSegmentsFromHistory();
      this.syncSegmentElements();
      this.syncSnackElements();
      return;
    }

    const head = this.positions[0] ?? { x: 0, y: 0 };

    if (this.gameProgress.phase === 'hunting') {
      if (this.snackStates.length === 0) {
        this.spawnSnackWave();
      }

      this.updateFallingSnacks(deltaMs);
    }

    const chaseTarget = nearestSnack(
      head,
      this.snackStates,
      this.viewportWidth,
      this.viewportHeight,
      FOOD_MARGIN_PX
    );

    if (chaseTarget && this.gameProgress.phase === 'hunting') {
      this.recordHeadHistory(head);
      this.positions[0] = this.moveHeadTowardTarget(head, chaseTarget.position, headSpeed);
      this.checkSelfCollision();

      if (!this.isCelebrating()) {
        this.handleEatCollisions();
      }

      if (this.isCelebrating()) {
        this.updateSegmentsFromHistory();
        this.syncSegmentElements();
        this.syncSnackElements();
        return;
      }
    }

    this.updateSegmentsFromHistory();
    this.syncSegmentElements();
    this.syncSnackElements();
  }

  private updateSegmentsFromHistory(): void {
    const head = this.positions[0];
    if (!head) {
      return;
    }

    const trail = positionSegmentsFromHistory(
      head,
      this.headHistory,
      this.positions.length,
      this.segmentSpacing()
    );

    for (let index = 1; index < trail.length; index += 1) {
      const point = trail[index];
      if (point) {
        this.positions[index] = point;
      }
    }

    this.debugFrame += 1;
    if (this.debugFrame % 30 === 0) {
      const spacing = this.segmentSpacing();
      let stackedPairs = 0;
      for (let index = 1; index < this.positions.length; index += 1) {
        const current = this.positions[index];
        const previous = this.positions[index - 1];
        if (current && previous && Math.hypot(current.x - previous.x, current.y - previous.y) < spacing * 0.35) {
          stackedPairs += 1;
        }
      }

      const historyTrailPx = this.headHistory.reduce((total, point, index, history) => {
        if (index === 0) {
          return total;
        }
        const previous = history[index - 1];
        return previous ? total + Math.hypot(point.x - previous.x, point.y - previous.y) : total;
      }, 0);

      if (stackedPairs > 0 || this.positions.length !== this.segmentCount()) {
        debugAgentLog(
          'orb-snake-background.component.ts:updateSegmentsFromHistory',
          'tail collapse or segment desync detected',
          {
            segmentCount: this.positions.length,
            signalCount: this.segmentCount(),
            headHistoryLength: this.headHistory.length,
            maxHistoryLength: this.maxHeadHistoryLength(),
            historyTrailPx,
            requiredTrailPx: (this.positions.length - 1) * spacing,
            stackedPairs,
          },
          'A'
        );
      }
    }
  }

  private checkSelfCollision(): void {
    const head = this.positions[0];
    if (!head || this.gameProgress.phase !== 'hunting') {
      return;
    }

    if (
      isSelfCollisionEnabled(this.gameProgress.foodsEaten) &&
      hitsSelf(head, this.positions, this.segmentSpacing())
    ) {
      this.handleSelfCollision();
    }
  }

  private removeSnack(snackId: string): void {
    const index = this.snackStates.findIndex(snack => snack.id === snackId);
    if (index >= 0) {
      this.snackStates.splice(index, 1);
    }

    this.publishSnacks();

    if (this.snackStates.length === 0 && this.gameProgress.phase === 'hunting') {
      this.spawnSnackWave();
    }
  }

  private handleEatCollisions(): void {
    const head = this.positions[0];
    if (!head || this.gameProgress.phase !== 'hunting') {
      return;
    }

    const eatenSnack = this.snackStates.find(
      snack => isSnackFalling(snack) && canEat(head, snack.position)
    );

    if (!eatenSnack) {
      return;
    }

    if (eatenSnack.isDecoy) {
      this.gameProgress = onDecoyEaten(this.gameProgress);
      this.removeSnack(eatenSnack.id);
      this.triggerStun(STUN_FRAMES_DECOY);
      return;
    }

    this.gameProgress = onFoodEaten(this.gameProgress);
    this.recomputeSpeeds();
    this.growSnake();
    this.removeSnack(eatenSnack.id);

    if (this.isScreenFull()) {
      this.handleRunComplete();
    }
  }

  private finishCelebration(): void {
    this.gameProgress = afterCelebration();
    this.recomputeSpeeds();
    this.shrinkSnakeToInitial();
    this.spawnIndex = 0;
    this.spawnSnackWave();
    window.requestAnimationFrame(() => {
      this.syncSegmentElements();
    });
  }

  private syncSegmentElements(): void {
    const elements = this.segmentRefs?.toArray() ?? [];
    if (elements.length === 0) {
      return;
    }

    elements.forEach((elementRef, index) => {
      const element = elementRef.nativeElement;
      const point = this.positions[index];
      if (!point) {
        return;
      }

      const progress = index / Math.max(this.positions.length - 1, 1);
      const scale = lerp(1, TAIL_SCALE, progress);
      const sizePx = HEAD_SIZE_REM * this.remPx * scale;
      const x = point.x - sizePx / 2;
      const y = point.y - sizePx / 2;
      element.style.transform = `translate3d(${x}px, ${y}px, 0) scale(${scale})`;
      element.style.opacity = `${this.segmentOpacity(progress)}`;
    });

    if (this.positions.length !== this.lastLoggedSegmentCount) {
      const headScale = lerp(1, TAIL_SCALE, 0);
      debugAgentLog(
        'orb-snake-background.component.ts:syncSegmentElements',
        'visible segment count changed',
        {
          segmentCount: this.positions.length,
          signalCount: this.segmentCount(),
          domCount: elements.length,
          headScale,
          remPx: this.remPx,
        },
        'E'
      );
      this.lastLoggedSegmentCount = this.positions.length;
    }
  }

  private syncSnackElements(): void {
    const elements = this.snackRefs?.toArray() ?? [];
    if (elements.length === 0) {
      return;
    }

    const sizePx = FOOD_SIZE_REM * this.remPx;

    for (const elementRef of elements) {
      const snackId = elementRef.nativeElement.dataset['snackId'];
      const snack = this.snackStates.find(item => item.id === snackId);
      if (!snack) {
        continue;
      }

      elementRef.nativeElement.style.transform = `translate3d(${snack.position.x - sizePx / 2}px, ${snack.position.y - sizePx / 2}px, 0)`;
      elementRef.nativeElement.classList.toggle('orb-snake__food--decoy', snack.isDecoy);
      elementRef.nativeElement.classList.toggle('orb-snake__food--falling', true);
    }
  }

  private segmentOpacity(progress: number): number {
    const isLight = document.documentElement.getAttribute('data-theme') === 'light';
    const tailOpacity = isLight ? 0.58 : 0.35;
    return lerp(1, tailOpacity, progress);
  }

  private updateViewport(): void {
    this.viewportWidth = window.innerWidth;
    this.viewportHeight = window.innerHeight;
    this.remPx = Number.parseFloat(getComputedStyle(document.documentElement).fontSize) || 16;
    this.recomputeSpeeds();
  }
}
