export class DeployAIApiError extends Error {
  readonly code: string;

  constructor(code: string, message: string) {
    super(message);
    this.name = 'DeployAIApiError';
    this.code = code;
  }
}

export function formatToolError(error: unknown): { isError: true; content: { type: 'text'; text: string }[] } {
  if (error instanceof DeployAIApiError) {
    return {
      isError: true,
      content: [
        {
          type: 'text',
          text: JSON.stringify({ code: error.code, message: error.message }, null, 2),
        },
      ],
    };
  }

  const message = error instanceof Error ? error.message : String(error);
  return {
    isError: true,
    content: [
      {
        type: 'text',
        text: JSON.stringify({ code: 'unexpected_error', message }, null, 2),
      },
    ],
  };
}

export function formatToolSuccess(data: unknown): { content: { type: 'text'; text: string }[] } {
  return {
    content: [
      {
        type: 'text',
        text: typeof data === 'string' ? data : JSON.stringify(data, null, 2),
      },
    ],
  };
}
