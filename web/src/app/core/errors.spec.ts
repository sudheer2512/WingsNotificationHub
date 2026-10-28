import { toParams } from './api.service';
import { HttpClient, HttpErrorResponse, HttpHeaders, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { AuthService } from './auth.service';
import { errorInterceptor, problemText, toProblem } from './errors';

describe('toProblem', () => {
  it('reads the hub error contract', () => {
    const err = new HttpErrorResponse({ status: 422, error: { code: 'validation_failed', message: 'The rule is invalid.', errors: { rule: ['Name is required.'] }, correlationId: 'abc123' } });
    expect(toProblem(err)).toEqual({ status: 422, code: 'validation_failed', message: 'The rule is invalid.', errors: { rule: ['Name is required.'] }, correlationId: 'abc123' });
    expect(problemText(err)).toBe('The rule is invalid. Name is required. (ref abc123)');
  });

  it('falls back to the correlation header and to framework problem+json', () => {
    const err = new HttpErrorResponse({ status: 400, error: { title: 'Bad Request', detail: 'Body is missing' }, headers: new HttpHeaders({ 'X-Correlation-Id': 'h-1' }) });
    expect(toProblem(err)).toMatchObject({ status: 400, message: 'Body is missing', correlationId: 'h-1' });
  });

  it('parses JSON error bodies delivered as text and OAuth errors', () => {
    expect(toProblem(new HttpErrorResponse({ status: 409, error: '{"code":"conflict","message":"Already decided."}' }))).toMatchObject({ code: 'conflict', message: 'Already decided.' });
    expect(toProblem(new HttpErrorResponse({ status: 401, error: { error: 'invalid_client', error_description: 'Client authentication failed.' } })))
      .toMatchObject({ code: 'invalid_client', message: 'Client authentication failed.' });
  });

  it('gives friendly text for network failures and empty bodies, and ignores HTML error pages', () => {
    expect(toProblem(new HttpErrorResponse({ status: 0, error: new ProgressEvent('error') })).message).toContain('could not be reached');
    expect(toProblem(new HttpErrorResponse({ status: 404, error: null })).message).toBe('Not found (or not visible to you).');
    expect(toProblem(new HttpErrorResponse({ status: 502, error: '<html>Bad gateway</html>' })).message).toBe('The API gateway returned an error.');
    expect(toProblem(new Error('boom'))).toEqual({ status: -1, message: 'boom' });
  });
});

describe('errorInterceptor', () => {
  let http: HttpClient;
  let ctl: HttpTestingController;
  const auth = { expired: vi.fn() };

  beforeEach(() => {
    auth.expired.mockReset();
    TestBed.configureTestingModule({
      providers: [{ provide: AuthService, useValue: auth }, provideHttpClient(withInterceptors([errorInterceptor])), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpClient);
    ctl = TestBed.inject(HttpTestingController);
  });
  afterEach(() => ctl.verify());

  it('signs the user out when an API call returns 401', () => {
    let status = 0;
    http.get('/api/v1/templates').subscribe({ error: (e: HttpErrorResponse) => (status = e.status) });
    ctl.expectOne('/api/v1/templates').flush({ code: 'unauthorized', message: 'no' }, { status: 401, statusText: 'Unauthorized' });
    expect(auth.expired).toHaveBeenCalledTimes(1);
    expect(status).toBe(401);
  });

  it('does not sign out for a failed login or for 403', () => {
    http.post('/api/v1/auth/login', {}).subscribe({ error: () => undefined });
    ctl.expectOne('/api/v1/auth/login').flush({ code: 'invalid_credentials', message: 'Invalid email or password.' }, { status: 401, statusText: 'Unauthorized' });
    http.get('/api/v1/rules').subscribe({ error: () => undefined });
    ctl.expectOne('/api/v1/rules').flush({ code: 'forbidden', message: 'no' }, { status: 403, statusText: 'Forbidden' });
    expect(auth.expired).not.toHaveBeenCalled();
  });

  it('turns JSON errors in Blob bodies (exports) into the hub error contract', async () => {
    const result = new Promise<unknown>(resolve => http.get('/api/v1/deliveries/export', { responseType: 'blob' }).subscribe({ error: e => resolve(e) }));
    const body = new Blob([JSON.stringify({ code: 'forbidden', message: 'Export not allowed.', correlationId: 'c-9' })], { type: 'application/json' });
    ctl.expectOne('/api/v1/deliveries/export').flush(body, { status: 403, statusText: 'Forbidden' });
    const err = await result;
    expect(err).toBeInstanceOf(HttpErrorResponse);
    expect(toProblem(err)).toMatchObject({ status: 403, code: 'forbidden', message: 'Export not allowed.', correlationId: 'c-9' });
  });
});

describe('toParams', () => {
  it('drops empty values and sends channel filters as enum names (API query binding is case-sensitive)', () => {
    const p = toParams({ channel: 'whatsapp', status: 'Failed', page: 2, recipient: '', companyId: undefined, awaitingOperator: true });
    expect(p.toString()).toBe('channel=WhatsApp&status=Failed&page=2&awaitingOperator=true');
    expect(toParams({ channel: 'inapp' }).get('channel')).toBe('InApp');
  });
});
