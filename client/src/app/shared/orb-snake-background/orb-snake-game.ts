export interface Point {
  x: number;
  y: number;
}

export const INITIAL_SEGMENT_COUNT = 4;
export const FOODS_UNTIL_BOSS = 10;
export const EAT_RADIUS_PX = 28;
export const TARGET_FRAME_MS = 1000 / 60;

/** Time for food to travel from the top margin to the bottom margin. */
export const FOOD_FALL_DURATION_SEC = 11;
/** Time for the snake head to cross the play area horizontally. */
export const SNAKE_CROSS_DURATION_SEC = 7.5;
export const SNAKE_SPEED_GROWTH_PER_FOOD = 0.015;
export const MAX_SNAKE_SPEED_MULTIPLIER = 1.2;
export const MIN_SNACK_STAGGER_SEC = 0.7;
export const MAX_SNACK_STAGGER_SEC = 1.25;

/** @deprecated Use computeGameSpeedProfile instead. */
export const HEAD_MOVE_SPEED = 3;
/** @deprecated Use computeGameSpeedProfile instead. */
export const SNACK_FALL_SPEED = 2.8;
/** @deprecated Use computeGameSpeedProfile instead. */
export const MIN_SNACK_STAGGER_FRAMES = 42;
/** @deprecated Use computeGameSpeedProfile instead. */
export const MAX_SNACK_STAGGER_FRAMES = 78;
export const BOSS_WANDER_SPEED = 0.005;
export const BOSS_EAT_RADIUS_PX = 42;
export const FOOD_MARGIN_PX = 80;

export const SELF_COLLISION_SKIP = 3;
export const SELF_COLLISION_RADIUS_PX = 20;
export const SEGMENT_SPAWN_BUFFER_PX = 40;
export const AVOID_SEGMENT_RADIUS_PX = 80;

export const STUN_FRAMES_SELF = 60;
export const STUN_FRAMES_DECOY = 30;
export const STUN_FRAMES_WALL = 20;

export const FLEEING_SNACK_CHANCE = 0.3;
export const FLEEING_FLEE_RADIUS_PX = 120;
export const FLEEING_SPEED = 0.8;
export const FLEEING_TIMEOUT_FRAMES = 720;

export const WRAP_CHASE_DISTANCE_PX = 180;

export const STUN_FRAMES_MISS = 15;
export const MIN_SNACKS_PER_WAVE = 1;
export const MAX_SNACKS_PER_WAVE = 5;
export const SEGMENT_SPACING_FACTOR = 0.55;
export const SCREEN_FILL_PACKING = 0.85;

export const BOSS_PREVIEW_FOODS = 7;
export const BOSS_PREVIEW_DECOY_CHANCE = 0.5;
export const DECOY_PHASE_FOODS = 3;
export const DECOY_SPAWN_INTERVAL = 4;
export const SELF_COLLISION_PHASE_FOODS = 6;

export const SEEK_WEIGHT = 0.92;
export const AVOID_WEIGHT = 0.08;

export type GamePhase = 'hunting' | 'celebrating' | 'stunned';

export interface GameProgress {
  phase: GamePhase;
  foodsEaten: number;
  segmentCount: number;
}

export function createInitialProgress(): GameProgress {
  return {
    phase: 'hunting',
    foodsEaten: 0,
    segmentCount: INITIAL_SEGMENT_COUNT,
  };
}

export function frameScale(deltaMs: number, targetFrameMs = TARGET_FRAME_MS): number {
  if (!Number.isFinite(deltaMs) || deltaMs <= 0) {
    return 1;
  }

  return Math.min(deltaMs / targetFrameMs, 2.5);
}

export interface PlayArea {
  playWidth: number;
  playHeight: number;
}

export interface GameSpeedProfile {
  headPxPerSecond: number;
  snackFallPxPerSecond: number;
  minStaggerMs: number;
  maxStaggerMs: number;
}

export function playAreaSize(
  width: number,
  height: number,
  margin = FOOD_MARGIN_PX
): PlayArea {
  return {
    playWidth: Math.max(width - margin * 2, margin),
    playHeight: Math.max(height - margin * 2, margin),
  };
}

export function snakeSpeedMultiplier(foodsEaten: number): number {
  return Math.min(1 + foodsEaten * SNAKE_SPEED_GROWTH_PER_FOOD, MAX_SNAKE_SPEED_MULTIPLIER);
}

export function computeGameSpeedProfile(
  width: number,
  height: number,
  margin = FOOD_MARGIN_PX,
  foodsEaten = 0
): GameSpeedProfile {
  const { playWidth, playHeight } = playAreaSize(width, height, margin);

  return {
    snackFallPxPerSecond: playHeight / FOOD_FALL_DURATION_SEC,
    headPxPerSecond: (playWidth / SNAKE_CROSS_DURATION_SEC) * snakeSpeedMultiplier(foodsEaten),
    minStaggerMs: MIN_SNACK_STAGGER_SEC * 1000,
    maxStaggerMs: MAX_SNACK_STAGGER_SEC * 1000,
  };
}

export function speedForDelta(pxPerSecond: number, deltaMs: number): number {
  if (!Number.isFinite(deltaMs) || deltaMs <= 0) {
    return pxPerSecond * (TARGET_FRAME_MS / 1000);
  }

  return pxPerSecond * (deltaMs / 1000);
}

export function fallDurationSec(playHeight: number, snackFallPxPerSecond: number): number {
  if (snackFallPxPerSecond <= 0) {
    return Number.POSITIVE_INFINITY;
  }

  return playHeight / snackFallPxPerSecond;
}

export function crossDurationSec(playWidth: number, headPxPerSecond: number): number {
  if (headPxPerSecond <= 0) {
    return Number.POSITIVE_INFINITY;
  }

  return playWidth / headPxPerSecond;
}

export function positionSegmentsFromHistory(
  head: Point,
  headHistory: Point[],
  segmentCount: number,
  spacing: number
): Point[] {
  if (segmentCount <= 0) {
    return [];
  }

  const positions: Point[] = [{ ...head }];
  if (segmentCount === 1) {
    return positions;
  }

  const trail = [head, ...headHistory];

  for (let segmentIndex = 1; segmentIndex < segmentCount; segmentIndex += 1) {
    const targetDistance = segmentIndex * spacing;
    let walked = 0;
    let placed = false;

    for (let pointIndex = 0; pointIndex < trail.length - 1; pointIndex += 1) {
      const leading = trail[pointIndex];
      const trailing = trail[pointIndex + 1];
      if (!leading || !trailing) {
        continue;
      }

      const segmentLength = distance(leading, trailing);
      if (segmentLength === 0) {
        continue;
      }

      if (walked + segmentLength >= targetDistance) {
        const progress = (targetDistance - walked) / segmentLength;
        positions.push({
          x: leading.x + (trailing.x - leading.x) * progress,
          y: leading.y + (trailing.y - leading.y) * progress,
        });
        placed = true;
        break;
      }

      walked += segmentLength;
    }

    if (!placed) {
      const previous = positions[positions.length - 1] ?? head;
      const retreatPoint = headHistory[0] ?? headHistory[headHistory.length - 1];
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

      positions.push({
        x: previous.x - dirX * spacing,
        y: previous.y - dirY * spacing,
      });
    }
  }

  return positions;
}

export function distance(a: Point, b: Point): number {
  return Math.hypot(a.x - b.x, a.y - b.y);
}

export function moveToward(current: Point, target: Point, speed: number): Point {
  const dx = target.x - current.x;
  const dy = target.y - current.y;
  const dist = distance(current, target);

  if (dist <= speed || dist === 0) {
    return { ...target };
  }

  return {
    x: current.x + (dx / dist) * speed,
    y: current.y + (dy / dist) * speed,
  };
}

export function canEat(head: Point, target: Point, eatRadius = EAT_RADIUS_PX): boolean {
  return distance(head, target) <= eatRadius;
}

export function normalize(vector: Point): Point {
  const length = Math.hypot(vector.x, vector.y);
  if (length === 0) {
    return { x: 0, y: 0 };
  }

  return {
    x: vector.x / length,
    y: vector.y / length,
  };
}

export function seekForce(head: Point, target: Point): Point {
  return normalize({
    x: target.x - head.x,
    y: target.y - head.y,
  });
}

export function avoidTailForce(head: Point, segments: Point[]): Point {
  let forceX = 0;
  let forceY = 0;

  for (let index = SELF_COLLISION_SKIP; index < segments.length; index += 1) {
    const segment = segments[index];
    if (!segment) {
      continue;
    }

    const dx = head.x - segment.x;
    const dy = head.y - segment.y;
    const dist = Math.hypot(dx, dy);

    if (dist > 0 && dist < AVOID_SEGMENT_RADIUS_PX) {
      const strength = (AVOID_SEGMENT_RADIUS_PX - dist) / AVOID_SEGMENT_RADIUS_PX;
      forceX += (dx / dist) * strength;
      forceY += (dy / dist) * strength;
    }
  }

  return { x: forceX, y: forceY };
}

export function steerToward(
  head: Point,
  target: Point,
  segments: Point[],
  speed: number
): Point {
  const seek = seekForce(head, target);
  const avoid = avoidTailForce(head, segments);
  const blended = normalize({
    x: seek.x * SEEK_WEIGHT + avoid.x * AVOID_WEIGHT,
    y: seek.y * SEEK_WEIGHT + avoid.y * AVOID_WEIGHT,
  });

  if (blended.x === 0 && blended.y === 0) {
    return moveToward(head, target, speed);
  }

  return {
    x: head.x + blended.x * speed,
    y: head.y + blended.y * speed,
  };
}

export function wrapCoordinate(value: number, min: number, max: number): number {
  const span = max - min;
  if (span <= 0) {
    return min;
  }

  let wrapped = value;
  while (wrapped < min) {
    wrapped += span;
  }
  while (wrapped > max) {
    wrapped -= span;
  }

  return wrapped;
}

export function wrapPoint(
  point: Point,
  width: number,
  height: number,
  margin = FOOD_MARGIN_PX
): Point {
  return {
    x: wrapCoordinate(point.x, margin, width - margin),
    y: wrapCoordinate(point.y, margin, height - margin),
  };
}

export function wrappedDelta(
  from: Point,
  to: Point,
  width: number,
  height: number,
  margin = FOOD_MARGIN_PX
): Point {
  const minX = margin;
  const maxX = width - margin;
  const minY = margin;
  const maxY = height - margin;
  const spanX = maxX - minX;
  const spanY = maxY - minY;

  let dx = to.x - from.x;
  let dy = to.y - from.y;

  if (spanX > 0 && Math.abs(dx) > spanX / 2) {
    dx = dx > 0 ? dx - spanX : dx + spanX;
  }

  if (spanY > 0 && Math.abs(dy) > spanY / 2) {
    dy = dy > 0 ? dy - spanY : dy + spanY;
  }

  return { x: dx, y: dy };
}

export function wrappedDistance(
  from: Point,
  to: Point,
  width: number,
  height: number,
  margin = FOOD_MARGIN_PX
): number {
  const delta = wrappedDelta(from, to, width, height, margin);
  return Math.hypot(delta.x, delta.y);
}

export function shouldUseWrappedChase(
  head: Point,
  target: Point,
  width: number,
  height: number,
  margin = FOOD_MARGIN_PX,
  chaseDistance = WRAP_CHASE_DISTANCE_PX
): boolean {
  const direct = distance(head, target);
  const wrapped = wrappedDistance(head, target, width, height, margin);
  return wrapped + 1 < direct && wrapped <= chaseDistance;
}

export function steerTowardWrapped(
  head: Point,
  target: Point,
  segments: Point[],
  speed: number,
  width: number,
  height: number,
  margin = FOOD_MARGIN_PX
): Point {
  const delta = wrappedDelta(head, target, width, height, margin);
  const dist = Math.hypot(delta.x, delta.y);

  if (dist === 0) {
    return wrapPoint(head, width, height, margin);
  }

  const seek = { x: delta.x / dist, y: delta.y / dist };
  const avoid = avoidTailForce(head, segments);
  const blended = normalize({
    x: seek.x * SEEK_WEIGHT + avoid.x * AVOID_WEIGHT,
    y: seek.y * SEEK_WEIGHT + avoid.y * AVOID_WEIGHT,
  });

  const direction = blended.x === 0 && blended.y === 0 ? seek : blended;
  const next = {
    x: head.x + direction.x * speed,
    y: head.y + direction.y * speed,
  };

  return wrapPoint(next, width, height, margin);
}

export function canEatWrapped(
  head: Point,
  target: Point,
  width: number,
  height: number,
  margin = FOOD_MARGIN_PX,
  eatRadius = EAT_RADIUS_PX
): boolean {
  return wrappedDistance(head, target, width, height, margin) <= eatRadius;
}

export function isSpawnValid(
  point: Point,
  segments: Point[],
  headHistory: Point[],
  buffer = SEGMENT_SPAWN_BUFFER_PX
): boolean {
  for (const segment of segments) {
    if (distance(point, segment) < buffer) {
      return false;
    }
  }

  for (const historyPoint of headHistory) {
    if (distance(point, historyPoint) < buffer * 0.75) {
      return false;
    }
  }

  return true;
}

export function randomFoodPosition(
  width: number,
  height: number,
  margin = FOOD_MARGIN_PX,
  random: () => number = Math.random
): Point {
  const safeWidth = Math.max(width - margin * 2, margin);
  const safeHeight = Math.max(height - margin * 2, margin);

  return {
    x: margin + random() * safeWidth,
    y: margin + random() * safeHeight,
  };
}

export function findSafeFoodPosition(
  width: number,
  height: number,
  segments: Point[],
  headHistory: Point[],
  margin = FOOD_MARGIN_PX,
  random: () => number = Math.random,
  maxAttempts = 40
): Point {
  for (let attempt = 0; attempt < maxAttempts; attempt += 1) {
    const candidate = randomFoodPosition(width, height, margin, random);
    if (isSpawnValid(candidate, segments, headHistory)) {
      return candidate;
    }
  }

  return randomFoodPosition(width, height, margin, random);
}

export interface FallingSnack {
  id: string;
  position: Point;
  isDecoy: boolean;
  fallDelayMs: number;
}

export function snackStaggerMs(
  profile: GameSpeedProfile,
  random: () => number = Math.random
): number {
  const span = profile.maxStaggerMs - profile.minStaggerMs;
  return profile.minStaggerMs + Math.floor(random() * (span + 1));
}

export function buildStaggeredFallDelays(
  count: number,
  profile: GameSpeedProfile,
  random: () => number = Math.random
): number[] {
  const delays: number[] = [];
  let cumulative = 0;

  for (let index = 0; index < count; index += 1) {
    delays.push(cumulative);
    if (index < count - 1) {
      cumulative += snackStaggerMs(profile, random);
    }
  }

  return delays;
}

export function isSnackFalling(snack: FallingSnack): boolean {
  return snack.fallDelayMs <= 0;
}

export function tickSnackFallDelay(snack: FallingSnack, deltaMs: number): boolean {
  if (snack.fallDelayMs <= 0) {
    return false;
  }

  const previousDelay = snack.fallDelayMs;
  snack.fallDelayMs = Math.max(0, snack.fallDelayMs - deltaMs);
  return previousDelay > 0 && snack.fallDelayMs <= 0;
}

export function randomSnackWaveCount(random: () => number = Math.random): number {
  return MIN_SNACKS_PER_WAVE + Math.floor(random() * MAX_SNACKS_PER_WAVE);
}

export function createFallingSnack(
  width: number,
  height: number,
  segments: Point[],
  headHistory: Point[],
  margin = FOOD_MARGIN_PX,
  random: () => number = Math.random,
  maxAttempts = 40
): Point {
  const safeWidth = Math.max(width - margin * 2, margin);

  for (let attempt = 0; attempt < maxAttempts; attempt += 1) {
    const x = margin + random() * safeWidth;
    const start = { x, y: margin };
    if (isSpawnValid(start, segments, headHistory)) {
      return start;
    }
  }

  return {
    x: margin + random() * safeWidth,
    y: margin,
  };
}

export function createFallingSnackWave(
  count: number,
  width: number,
  height: number,
  segments: Point[],
  headHistory: Point[],
  spawnIndexStart: number,
  foodsEaten: number,
  speedProfile: GameSpeedProfile,
  margin = FOOD_MARGIN_PX,
  random: () => number = Math.random
): FallingSnack[] {
  const snacks: FallingSnack[] = [];
  const occupied: Point[] = [...segments];
  const fallDelays = buildStaggeredFallDelays(count, speedProfile, random);

  for (let index = 0; index < count; index += 1) {
    const spawnIndex = spawnIndexStart + index;
    const position = createFallingSnack(width, height, occupied, headHistory, margin, random);
    snacks.push({
      id: `snack-${spawnIndex}-${index}`,
      position,
      isDecoy: shouldSpawnDecoy(spawnIndex, foodsEaten, random),
      fallDelayMs: fallDelays[index] ?? 0,
    });
    occupied.push(position);
  }

  return snacks;
}

export function nearestSnack(
  head: Point,
  snacks: FallingSnack[],
  width: number,
  height: number,
  margin = FOOD_MARGIN_PX
): FallingSnack | null {
  let closest: FallingSnack | null = null;
  let closestDistance = Number.POSITIVE_INFINITY;

  for (const snack of snacks) {
    if (!isSnackFalling(snack)) {
      continue;
    }

    const snackDistance = distance(head, snack.position);
    if (snackDistance < closestDistance) {
      closestDistance = snackDistance;
      closest = snack;
    }
  }

  return closest;
}

export function fallSnack(current: Point, speed: number, bottomY: number): Point {
  return {
    x: current.x,
    y: Math.min(current.y + speed, bottomY),
  };
}

export function snackBottomY(height: number, margin = FOOD_MARGIN_PX): number {
  return height - margin;
}

export function hasSnackFallenOff(current: Point, bottomY: number): boolean {
  return current.y >= bottomY;
}

export function onSnackMissed(progress: GameProgress, isDecoy: boolean): GameProgress {
  if (isDecoy) {
    return progress;
  }

  return {
    ...progress,
    foodsEaten: reduceFoodsEaten(progress.foodsEaten, 1),
  };
}

export function shouldSpawnDecoy(
  spawnIndex: number,
  foodsEaten: number,
  random: () => number = Math.random
): boolean {
  if (foodsEaten >= BOSS_PREVIEW_FOODS) {
    return random() < BOSS_PREVIEW_DECOY_CHANCE;
  }

  if (foodsEaten >= DECOY_PHASE_FOODS) {
    return spawnIndex % DECOY_SPAWN_INTERVAL === 0;
  }

  return false;
}

export function shouldFoodFlee(isDecoy: boolean, random: () => number = Math.random): boolean {
  if (isDecoy) {
    return false;
  }

  return random() < FLEEING_SNACK_CHANCE;
}

export function isSelfCollisionEnabled(_foodsEaten: number): boolean {
  return true;
}

export function segmentSpacingPx(remPx: number, headSizeRem = 2.25): number {
  return headSizeRem * remPx * SEGMENT_SPACING_FACTOR;
}

export function maxSegmentsForViewport(
  width: number,
  height: number,
  margin: number,
  segmentSpacing: number
): number {
  const playWidth = Math.max(width - margin * 2, margin);
  const playHeight = Math.max(height - margin * 2, margin);
  const area = playWidth * playHeight;
  const segmentArea = Math.PI * Math.pow(segmentSpacing / 2, 2) * SCREEN_FILL_PACKING;

  return Math.max(INITIAL_SEGMENT_COUNT, Math.floor(area / segmentArea));
}

export function hasFilledScreen(
  segmentCount: number,
  width: number,
  height: number,
  margin: number,
  segmentSpacing: number
): boolean {
  return segmentCount >= maxSegmentsForViewport(width, height, margin, segmentSpacing);
}

export function hitsSelf(
  head: Point,
  segments: Point[],
  segmentSpacing = 0
): boolean {
  for (let index = SELF_COLLISION_SKIP; index < segments.length; index += 1) {
    const segment = segments[index];
    if (!segment) {
      continue;
    }

    const previous = segments[index - 1];
    if (
      segmentSpacing > 0 &&
      previous &&
      distance(previous, segment) < segmentSpacing * 0.5
    ) {
      continue;
    }

    if (distance(head, segment) <= SELF_COLLISION_RADIUS_PX) {
      return true;
    }
  }

  return false;
}

export function isOutsideWall(
  head: Point,
  width: number,
  height: number,
  margin = FOOD_MARGIN_PX
): boolean {
  return (
    head.x < margin ||
    head.x > width - margin ||
    head.y < margin ||
    head.y > height - margin
  );
}

export function bounceFromWall(
  head: Point,
  width: number,
  height: number,
  margin = FOOD_MARGIN_PX
): Point {
  const clampedX = Math.min(Math.max(head.x, margin), width - margin);
  const clampedY = Math.min(Math.max(head.y, margin), height - margin);

  return { x: clampedX, y: clampedY };
}

export function fleeFoodPosition(
  food: Point,
  head: Point,
  width: number,
  height: number,
  margin = FOOD_MARGIN_PX,
  speed = FLEEING_SPEED
): Point {
  const dist = wrappedDistance(food, head, width, height, margin);
  if (dist > FLEEING_FLEE_RADIUS_PX || dist === 0) {
    return food;
  }

  const delta = wrappedDelta(food, head, width, height, margin);
  const away = normalize({
    x: -delta.x,
    y: -delta.y,
  });

  return wrapPoint(
    {
      x: food.x + away.x * speed,
      y: food.y + away.y * speed,
    },
    width,
    height,
    margin
  );
}

export function clampFoodToBounds(
  food: Point,
  width: number,
  height: number,
  margin = FOOD_MARGIN_PX
): Point {
  return {
    x: Math.min(Math.max(food.x, margin), width - margin),
    y: Math.min(Math.max(food.y, margin), height - margin),
  };
}

export function shrinkSegments(segmentCount: number, amount: number): number {
  return Math.max(INITIAL_SEGMENT_COUNT, segmentCount - amount);
}

export function reduceFoodsEaten(foodsEaten: number, amount: number): number {
  return Math.max(0, foodsEaten - amount);
}

export function applyStun(progress: GameProgress, frames: number): { progress: GameProgress; frames: number } {
  return {
    progress: {
      ...progress,
      phase: 'stunned',
    },
    frames,
  };
}

export function recoverFromStun(progress: GameProgress): GameProgress {
  return {
    ...progress,
    phase: 'hunting',
  };
}

export const SELF_COLLISION_SEGMENT_PENALTY = 2;
export const SELF_COLLISION_FOOD_PENALTY = 1;

export function onSelfCollision(progress: GameProgress): GameProgress {
  return {
    ...progress,
    foodsEaten: reduceFoodsEaten(progress.foodsEaten, SELF_COLLISION_FOOD_PENALTY),
    segmentCount: shrinkSegments(progress.segmentCount, SELF_COLLISION_SEGMENT_PENALTY),
  };
}

export function onScreenFilled(progress: GameProgress): GameProgress {
  return {
    ...progress,
    phase: 'celebrating',
  };
}

export function onDecoyEaten(progress: GameProgress): GameProgress {
  return {
    ...progress,
    foodsEaten: reduceFoodsEaten(progress.foodsEaten, 1),
  };
}

export function onWallHit(progress: GameProgress): GameProgress {
  return {
    ...progress,
    segmentCount: shrinkSegments(progress.segmentCount, 1),
  };
}

export function bossPositionAt(time: number, width: number, height: number): Point {
  const centerX = width * 0.5;
  const centerY = height * 0.5;
  const radius = Math.min(width, height) * 0.22;

  return {
    x: centerX + Math.cos(time) * radius,
    y: centerY + Math.sin(time * 1.35) * radius * 0.75,
  };
}

export function onFoodEaten(progress: GameProgress): GameProgress {
  return {
    phase: 'hunting',
    foodsEaten: progress.foodsEaten + 1,
    segmentCount: progress.segmentCount + 1,
  };
}

export function afterCelebration(): GameProgress {
  return createInitialProgress();
}
