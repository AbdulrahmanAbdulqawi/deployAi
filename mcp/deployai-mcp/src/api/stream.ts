import { DeployAIApiError } from '../errors.js';
import type { StreamAggregateResult } from './types.js';

export interface NdjsonEvent {
  type: string;
  [key: string]: unknown;
}

export async function* readNdjsonStream(
  body: ReadableStream<Uint8Array>
): AsyncGenerator<NdjsonEvent> {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';

  try {
    while (true) {
      const { done, value } = await reader.read();
      if (done) {
        break;
      }

      buffer += decoder.decode(value, { stream: true });
      const lines = buffer.split('\n');
      buffer = lines.pop() ?? '';

      for (const line of lines) {
        const trimmed = line.trim();
        if (!trimmed) {
          continue;
        }
        yield JSON.parse(trimmed) as NdjsonEvent;
      }
    }

    const trailing = buffer.trim();
    if (trailing) {
      yield JSON.parse(trailing) as NdjsonEvent;
    }
  } finally {
    reader.releaseLock();
  }
}

export function parseNdjsonBuffer(buffer: string): NdjsonEvent[] {
  const events: NdjsonEvent[] = [];
  for (const line of buffer.split('\n')) {
    const trimmed = line.trim();
    if (!trimmed) {
      continue;
    }
    events.push(JSON.parse(trimmed) as NdjsonEvent);
  }
  return events;
}

export async function consumeNdjsonStream<TComplete extends NdjsonEvent>(
  body: ReadableStream<Uint8Array>,
  options?: { onLog?: (message: string) => void }
): Promise<StreamAggregateResult<TComplete>> {
  const progress: string[] = [];

  for await (const event of readNdjsonStream(body)) {
    if (event.type === 'log' && typeof event.message === 'string') {
      progress.push(event.message);
      options?.onLog?.(event.message);
    }

    if (event.type === 'complete') {
      return { progress, result: event as TComplete };
    }

    if (event.type === 'error') {
      const code = typeof event.code === 'string' ? event.code : 'stream_error';
      const message =
        typeof event.message === 'string' ? event.message : 'Stream ended with an error.';
      throw new DeployAIApiError(code, message);
    }
  }

  throw new DeployAIApiError('stream_incomplete', 'Stream ended without a complete or error event.');
}
