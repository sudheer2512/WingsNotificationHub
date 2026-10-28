import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Observable, catchError, from, switchMap, throwError } from 'rxjs';
import { AuthService } from './auth.service';

/** Normalised error shown by the UI. The API returns {code, message, errors?, correlationId}. */
export interface Problem {
  status: number;
  code?: string;
  message: string;
  errors?: Record<string, string[]> | null;
  correlationId?: string | null;
}

const FALLBACK: Record<number, string> = {
  0: 'The Notification Hub API could not be reached. Check that the service is running.',
  401: 'Not signed in, or the session has expired.',
  403: 'You are not allowed to perform this action.',
  404: 'Not found (or not visible to you).',
  409: 'The request conflicts with the current state. Reload and try again.',
  413: 'The upload is too large.',
  423: 'The account is temporarily locked.',
  429: 'Too many requests. Wait a moment and retry.',
  500: 'The server hit an unexpected error.',
  502: 'The API gateway returned an error.',
  503: 'The service is temporarily unavailable.',
};

function isRecord(v: unknown): v is Record<string, unknown> {
  return !!v && typeof v === 'object' && !Array.isArray(v);
}

/** Converts anything thrown by HttpClient (or code) into a {@link Problem}. Pure; unit-tested. */
export function toProblem(err: unknown): Problem {
  if (err instanceof HttpErrorResponse) {
    let body: unknown = err.error;
    if (typeof body === 'string' && body.trim().startsWith('{')) {
      try { body = JSON.parse(body); } catch { /* keep text */ }
    }
    const headerCorrelation = err.headers?.get?.('X-Correlation-Id') ?? null;
    if (isRecord(body)) {
      // Hub error contract.
      if (typeof body['message'] === 'string' || typeof body['code'] === 'string') {
        return {
          status: err.status, code: (body['code'] as string) || undefined,
          message: (body['message'] as string) || FALLBACK[err.status] || `HTTP ${err.status}`,
          errors: isRecord(body['errors']) ? body['errors'] as Record<string, string[]> : null,
          correlationId: (body['correlationId'] as string) ?? headerCorrelation,
        };
      }
      // ASP.NET problem+json (framework-level failures) and OAuth errors.
      if (typeof body['detail'] === 'string' || typeof body['title'] === 'string') {
        return { status: err.status, message: (body['detail'] as string) ?? (body['title'] as string), errors: isRecord(body['errors']) ? body['errors'] as Record<string, string[]> : null, correlationId: headerCorrelation };
      }
      if (typeof body['error_description'] === 'string') {
        return { status: err.status, code: body['error'] as string, message: body['error_description'] as string, correlationId: headerCorrelation };
      }
    }
    const text = typeof body === 'string' && body && !body.trim().startsWith('<') ? body : null;
    return { status: err.status, message: text ?? FALLBACK[err.status] ?? (err.message || `HTTP ${err.status}`), correlationId: headerCorrelation };
  }
  if (err instanceof Error) return { status: -1, message: err.message };
  if (isRecord(err) && typeof err['message'] === 'string') return { status: -1, message: err['message'] as string };
  return { status: -1, message: String(err) };
}

/** One-line message including validation details, for snack bars. */
export function problemText(err: unknown): string {
  const p = toProblem(err);
  const details = p.errors ? Object.values(p.errors).flat().filter(Boolean) : [];
  const msg = details.length && !details.every(d => p.message.includes(d)) ? `${p.message} ${details.join(' ')}` : p.message;
  return p.correlationId ? `${msg} (ref ${p.correlationId})` : msg;
}

function readBlobText(blob: Blob): Promise<string> {
  if (typeof blob.text === 'function') return blob.text();
  return new Promise((resolve, reject) => {
    const r = new FileReader();
    r.onload = () => resolve(String(r.result ?? ''));
    r.onerror = () => reject(r.error);
    r.readAsText(blob);
  });
}

/** Blob responses (exports) carry JSON errors as a Blob: read them so callers see the hub error contract. */
function unwrapBlobError(err: HttpErrorResponse): Observable<never> {
  const blob = err.error as Blob;
  return from(readBlobText(blob)).pipe(switchMap(text => {
    let parsed: unknown = text;
    try { parsed = JSON.parse(text); } catch { /* not JSON */ }
    return throwError(() => new HttpErrorResponse({ error: parsed, headers: err.headers, status: err.status, statusText: err.statusText, url: err.url ?? undefined }));
  }));
}

function isApiUrl(url: string): boolean {
  return url.startsWith('/api/') || url.startsWith('/hubs/') || url.startsWith('/connect/') || /^https?:\/\/[^/]+\/(api|hubs|connect)\//.test(url);
}

/**
 * Error interceptor: reads JSON errors out of Blob bodies and signs the user out when the API rejects the session (401)
 * on anything other than the login call itself.
 */
export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  return next(req).pipe(
    catchError((err: unknown) => {
      if (!(err instanceof HttpErrorResponse)) return throwError(() => err);
      if (err.status === 401 && isApiUrl(req.url) && !req.url.includes('/auth/login')) auth.expired();
      if (typeof Blob !== 'undefined' && err.error instanceof Blob) return unwrapBlobError(err);
      return throwError(() => err);
    }),
  );
};
