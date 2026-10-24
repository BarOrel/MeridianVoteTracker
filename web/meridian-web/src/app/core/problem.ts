import { HttpErrorResponse } from '@angular/common/http';

/** Uniform reading of the API's ProblemDetails (RFC 7807) responses. */
export interface Problem {
  status: number;
  message: string;
  code?: string;
  fieldErrors: Record<string, string[]>;
  current?: any;
}

export function toProblem(error: unknown): Problem {
  if (error instanceof HttpErrorResponse) {
    const body = error.error ?? {};
    return {
      status: error.status,
      message: body.detail ?? body.title ?? error.message,
      code: body.code,
      fieldErrors: body.errors ?? {},
      current: body.current,
    };
  }
  return { status: 0, message: String(error), fieldErrors: {} };
}
