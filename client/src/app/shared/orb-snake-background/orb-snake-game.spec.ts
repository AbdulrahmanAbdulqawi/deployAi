import {
  afterCelebration,
  applyStun,
  bounceFromWall,
  buildStaggeredFallDelays,
  canEat,
  computeGameSpeedProfile,
  createFallingSnack,
  createFallingSnackWave,
  createInitialProgress,
  crossDurationSec,
  fallDurationSec,
  fallSnack,
  FOOD_FALL_DURATION_SEC,
  frameScale,
  hasFilledScreen,
  hasSnackFallenOff,
  hitsSelf,
  INITIAL_SEGMENT_COUNT,
  isSelfCollisionEnabled,
  isSnackFalling,
  isSpawnValid,
  MAX_SNACKS_PER_WAVE,
  maxSegmentsForViewport,
  MIN_SNACKS_PER_WAVE,
  MIN_SNACK_STAGGER_SEC,
  moveToward,
  nearestSnack,
  onDecoyEaten,
  onFoodEaten,
  onScreenFilled,
  onSelfCollision,
  onSnackMissed,
  playAreaSize,
  positionSegmentsFromHistory,
  randomFoodPosition,
  randomSnackWaveCount,
  recoverFromStun,
  segmentSpacingPx,
  shouldSpawnDecoy,
  snackBottomY,
  snackStaggerMs,
  SNAKE_CROSS_DURATION_SEC,
  speedForDelta,
  steerToward,
  TARGET_FRAME_MS,
  tickSnackFallDelay,
} from './orb-snake-game';

describe('orb-snake-game', () => {
  it('creates initial progress with hunting phase', () => {
    const progress = createInitialProgress();
    expect(progress.phase).toBe('hunting');
    expect(progress.foodsEaten).toBe(0);
    expect(progress.segmentCount).toBe(INITIAL_SEGMENT_COUNT);
  });

  it('balances snake and food speed against the play area', () => {
    const profile = computeGameSpeedProfile(800, 600, 80, 0);
    const { playWidth, playHeight } = playAreaSize(800, 600, 80);

    expect(fallDurationSec(playHeight, profile.snackFallPxPerSecond)).toBeCloseTo(
      FOOD_FALL_DURATION_SEC,
      1
    );
    expect(crossDurationSec(playWidth, profile.headPxPerSecond)).toBeCloseTo(
      SNAKE_CROSS_DURATION_SEC,
      1
    );
    expect(profile.headPxPerSecond).toBeGreaterThan(profile.snackFallPxPerSecond);
  });

  it('scales snake speed gently as food is eaten', () => {
    const base = computeGameSpeedProfile(800, 600, 80, 0);
    const faster = computeGameSpeedProfile(800, 600, 80, 10);

    expect(faster.headPxPerSecond).toBeGreaterThan(base.headPxPerSecond);
    expect(faster.snackFallPxPerSecond).toBe(base.snackFallPxPerSecond);
  });

  it('converts per-second speeds into frame movement', () => {
    const profile = computeGameSpeedProfile(800, 600, 80, 0);
    const step = speedForDelta(profile.headPxPerSecond, TARGET_FRAME_MS);

    expect(step).toBeCloseTo(profile.headPxPerSecond / 60, 5);
    expect(frameScale(TARGET_FRAME_MS)).toBeCloseTo(1, 5);
    expect(frameScale(0)).toBe(1);
  });

  it('moves toward a target without overshooting', () => {
    const next = moveToward({ x: 0, y: 0 }, { x: 10, y: 0 }, 3);
    expect(next).toEqual({ x: 3, y: 0 });
  });

  it('places body segments along the head trail', () => {
    const head = { x: 100, y: 100 };
    const history = [
      { x: 80, y: 100 },
      { x: 60, y: 100 },
      { x: 40, y: 100 },
    ];

    const segments = positionSegmentsFromHistory(head, history, 4, 20);
    expect(segments[1]).toEqual({ x: 80, y: 100 });
    expect(segments[2]).toEqual({ x: 60, y: 100 });
    expect(segments[3]).toEqual({ x: 40, y: 100 });
  });

  it('spreads fallback segments when the head trail is too short', () => {
    const head = { x: 100, y: 100 };
    const history = [{ x: 95, y: 100 }];
    const segments = positionSegmentsFromHistory(head, history, 4, 20);

    expect(segments[1]?.x).toBeLessThan(100);
    expect(Math.hypot(segments[1]!.x - segments[2]!.x, segments[1]!.y - segments[2]!.y)).toBeCloseTo(20, 0);
    expect(Math.hypot(segments[2]!.x - segments[3]!.x, segments[2]!.y - segments[3]!.y)).toBeCloseTo(20, 0);
  });

  it('steers toward food while still advancing', () => {
    const next = steerToward({ x: 0, y: 0 }, { x: 100, y: 0 }, [{ x: 0, y: 0 }], 3);
    expect(next.x).toBeGreaterThan(0);
    expect(next.y).toBe(0);
  });

  it('keeps the snake inside the play area instead of wrapping edges', () => {
    const clamped = bounceFromWall({ x: 10, y: 300 }, 800, 600, 80);
    expect(clamped.x).toBe(80);
    expect(clamped.y).toBe(300);

    const clampedBottom = bounceFromWall({ x: 400, y: 590 }, 800, 600, 80);
    expect(clampedBottom.y).toBe(520);
  });

  it('uses direct distance for eating', () => {
    expect(canEat({ x: 100, y: 100 }, { x: 120, y: 100 })).toBeTrue();
    expect(canEat({ x: 705, y: 300 }, { x: 90, y: 300 })).toBeFalse();
  });

  it('creates falling snacks from the top', () => {
    const food = createFallingSnack(800, 600, [], [], 80, () => 0.5);
    expect(food.y).toBe(80);
    expect(food.x).toBe(400);
  });

  it('spawns a random wave between one and five snacks', () => {
    const profile = computeGameSpeedProfile(800, 600, 80, 0);

    expect(randomSnackWaveCount(() => 0)).toBe(MIN_SNACKS_PER_WAVE);
    expect(randomSnackWaveCount(() => 0.99)).toBe(MAX_SNACKS_PER_WAVE);

    const wave = createFallingSnackWave(3, 800, 600, [], [], 0, 0, profile, 80, () => 0.5);
    expect(wave.length).toBe(3);
    expect(wave.every(snack => snack.position.y === 80)).toBeTrue();
    expect(wave[0]?.fallDelayMs).toBe(0);
    expect(wave[1]?.fallDelayMs).toBeGreaterThan(0);
    expect(wave[2]?.fallDelayMs).toBeGreaterThan(wave[1]?.fallDelayMs ?? 0);
  });

  it('staggers snack release times across a wave', () => {
    const profile = computeGameSpeedProfile(800, 600, 80, 0);

    expect(snackStaggerMs(profile, () => 0)).toBe(profile.minStaggerMs);
    expect(buildStaggeredFallDelays(1, profile, () => 0.5)).toEqual([0]);
    expect(buildStaggeredFallDelays(3, profile, () => 0)).toEqual([
      0,
      profile.minStaggerMs,
      profile.minStaggerMs * 2,
    ]);

    const waiting = {
      id: 'wait',
      position: { x: 100, y: 80 },
      isDecoy: false,
      fallDelayMs: MIN_SNACK_STAGGER_SEC * 1000,
    };
    expect(isSnackFalling(waiting)).toBeFalse();
    expect(tickSnackFallDelay(waiting, MIN_SNACK_STAGGER_SEC * 1000)).toBeTrue();
    expect(isSnackFalling(waiting)).toBeTrue();
  });

  it('targets the nearest falling snack', () => {
    const snacks = [
      { id: 'a', position: { x: 500, y: 200 }, isDecoy: false, fallDelayMs: 0 },
      { id: 'b', position: { x: 120, y: 220 }, isDecoy: false, fallDelayMs: 0 },
      { id: 'c', position: { x: 130, y: 180 }, isDecoy: false, fallDelayMs: 1200 },
    ];

    expect(nearestSnack({ x: 130, y: 300 }, snacks, 800, 600, 80)?.id).toBe('b');
  });

  it('drops snacks and removes them at the bottom', () => {
    const profile = computeGameSpeedProfile(800, 600, 80, 0);
    const start = createFallingSnack(800, 600, [], [], 80, () => 0.5);
    const bottomY = snackBottomY(600, 80);
    const midFall = fallSnack(start, speedForDelta(profile.snackFallPxPerSecond, TARGET_FRAME_MS), bottomY);

    expect(midFall.y).toBeGreaterThan(start.y);
    expect(hasSnackFallenOff({ x: 400, y: bottomY }, bottomY)).toBeTrue();
  });

  it('grows the snake when food is eaten', () => {
    const progress = onFoodEaten(createInitialProgress());
    expect(progress.foodsEaten).toBe(1);
    expect(progress.segmentCount).toBe(INITIAL_SEGMENT_COUNT + 1);
    expect(progress.phase).toBe('hunting');
  });

  it('detects when the snake fills the screen', () => {
    const spacing = segmentSpacingPx(16);
    const maxSegments = maxSegmentsForViewport(800, 600, 80, spacing);

    expect(maxSegments).toBeGreaterThan(INITIAL_SEGMENT_COUNT);
    expect(hasFilledScreen(maxSegments, 800, 600, 80, spacing)).toBeTrue();
    expect(hasFilledScreen(INITIAL_SEGMENT_COUNT, 800, 600, 80, spacing)).toBeFalse();
  });

  it('applies a setback on self collision instead of ending the run', () => {
    const progress = onSelfCollision({
      phase: 'hunting',
      foodsEaten: 5,
      segmentCount: 7,
    });

    expect(progress.phase).toBe('hunting');
    expect(progress.segmentCount).toBe(5);
    expect(progress.foodsEaten).toBe(4);
  });

  it('ends the run when the screen is filled', () => {
    const progress = onScreenFilled({
      phase: 'hunting',
      foodsEaten: 12,
      segmentCount: 40,
    });
    expect(progress.phase).toBe('celebrating');
  });

  it('applies a miss penalty when a real snack reaches the bottom', () => {
    const missed = onSnackMissed(
      { phase: 'hunting', foodsEaten: 4, segmentCount: 7 },
      false
    );

    expect(missed.foodsEaten).toBe(3);
    expect(missed.segmentCount).toBe(7);
  });

  it('detects self-collision from the first segment onward', () => {
    expect(isSelfCollisionEnabled(0)).toBeTrue();
    expect(hitsSelf({ x: 0, y: 0 }, [{ x: 0, y: 0 }, { x: 1, y: 1 }, { x: 2, y: 2 }, { x: 0, y: 0 }])).toBeTrue();
  });

  it('resets after celebration', () => {
    const restarted = afterCelebration();
    expect(restarted.phase).toBe('hunting');
    expect(restarted.foodsEaten).toBe(0);
    expect(restarted.segmentCount).toBe(INITIAL_SEGMENT_COUNT);
  });

  it('recovers from stun back into hunting', () => {
    const stunned = applyStun(createInitialProgress(), 30);
    expect(stunned.progress.phase).toBe('stunned');

    const recovered = recoverFromStun(stunned.progress);
    expect(recovered.phase).toBe('hunting');
  });
});
