import { describe, expect, it } from 'vitest';
import { parseNdjsonBuffer, consumeNdjsonStream } from './stream.js';
import { DeployAIApiError } from '../errors.js';

describe('parseNdjsonBuffer', () => {
  it('parses multiple NDJSON lines', () => {
    const buffer = '{"type":"started","startedAt":"2026-01-01"}\n{"type":"log","message":"hello"}\n';
    const events = parseNdjsonBuffer(buffer);
    expect(events).toHaveLength(2);
    expect(events[0].type).toBe('started');
    expect(events[1].message).toBe('hello');
  });

  it('ignores empty lines', () => {
    const events = parseNdjsonBuffer('\n\n{"type":"log","message":"x"}\n\n');
    expect(events).toHaveLength(1);
  });
});

describe('consumeNdjsonStream', () => {
  it('aggregates log lines and returns complete event', async () => {
    const payload =
      '{"type":"started","startedAt":"2026-01-01"}\n' +
      '{"type":"log","message":"step 1"}\n' +
      '{"type":"log","message":"step 2"}\n' +
      '{"type":"complete","branchName":"deployai/setup","pullRequestNumber":1,"pullRequestUrl":"https://github.com/pr/1","committedFiles":["vercel.json"]}\n';

    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(new TextEncoder().encode(payload));
        controller.close();
      },
    });

    const result = await consumeNdjsonStream(body);
    expect(result.progress).toEqual(['step 1', 'step 2']);
    expect(result.result.type).toBe('complete');
    expect(result.result.branchName).toBe('deployai/setup');
  });

  it('throws DeployAIApiError on error event', async () => {
    const payload = '{"type":"error","code":"setup_failed","message":"Could not generate files."}\n';
    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(new TextEncoder().encode(payload));
        controller.close();
      },
    });

    await expect(consumeNdjsonStream(body)).rejects.toMatchObject({
      code: 'setup_failed',
      message: 'Could not generate files.',
    });
  });

  it('throws when stream ends without complete or error', async () => {
    const payload = '{"type":"log","message":"only log"}\n';
    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(new TextEncoder().encode(payload));
        controller.close();
      },
    });

    await expect(consumeNdjsonStream(body)).rejects.toMatchObject({
      code: 'stream_incomplete',
    });
  });
});
