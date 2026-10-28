import { HttpClient, HttpInterceptorFn } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Observable, firstValueFrom, switchMap, tap } from 'rxjs';
import { LoginResponse, Me } from './models';

const TOKEN_KEY = 'wnh.session';

interface StoredSession { token: string; expiresAt: string; user: LoginResponse['user']; }

function readSession(): StoredSession | null {
  try {
    const raw = sessionStorage.getItem(TOKEN_KEY);
    if (!raw) return null;
    const s = JSON.parse(raw) as StoredSession;
    return s.token && new Date(s.expiresAt).getTime() > Date.now() ? s : null;
  } catch { return null; }
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  private http = inject(HttpClient);
  private router = inject(Router);
  private expiryTimer: ReturnType<typeof setTimeout> | null = null;

  readonly session = signal<StoredSession | null>(readSession());
  readonly token = computed(() => this.session()?.token ?? null);
  readonly me = signal<Me | null>(null);
  readonly signedIn = computed(() => !!this.token() && !!this.me());
  readonly permissions = computed(() => new Set(this.me()?.permissions ?? []));
  readonly isPlatformAdmin = computed(() => !!this.me()?.grants.some(g => g.role === 'PlatformAdministrator' && !g.tenantId));
  readonly displayName = computed(() => this.me()?.actorName ?? this.session()?.user.displayName ?? '');

  /** True when the user holds any of the given permissions (on at least one scope). */
  can(...perms: string[]): boolean {
    const p = this.permissions();
    return perms.some(x => p.has(x));
  }

  /** App initializer: re-validates a token kept in sessionStorage (never blocks start-up on failure). */
  async init(): Promise<void> {
    const s = this.session();
    if (!s) return;
    try { this.me.set(await firstValueFrom(this.http.get<Me>('/api/v1/auth/me'))); this.armExpiry(); }
    catch { this.clear(); }
  }

  signIn(email: string, password: string): Observable<Me> {
    return this.http.post<LoginResponse>('/api/v1/auth/login', { email: email.trim(), password }).pipe(
      tap(r => {
        const s: StoredSession = { token: r.accessToken, expiresAt: r.expiresAt, user: r.user };
        this.session.set(s);
        try { sessionStorage.setItem(TOKEN_KEY, JSON.stringify(s)); } catch { /* storage unavailable: keep in memory */ }
      }),
      switchMap(() => this.http.get<Me>('/api/v1/auth/me')),
      tap({ next: me => { this.me.set(me); this.armExpiry(); }, error: () => this.clear() }),
    );
  }

  refreshMe(): void {
    this.http.get<Me>('/api/v1/auth/me').subscribe({ next: me => this.me.set(me), error: () => undefined });
  }

  signOut(): void {
    this.clear();
    this.router.navigate(['/sign-in']);
  }

  /** Called by the error interceptor on 401. */
  expired(): void {
    if (!this.token()) return;
    this.clear();
    const url = this.router.url;
    const returnUrl = url.startsWith('/sign-in') ? undefined : url;
    this.router.navigate(['/sign-in'], { queryParams: { returnUrl, expired: 1 } });
  }

  private armExpiry() {
    if (this.expiryTimer) clearTimeout(this.expiryTimer);
    const s = this.session();
    if (!s) return;
    const ms = new Date(s.expiresAt).getTime() - Date.now();
    if (ms > 0 && ms < 2 ** 31 - 1) this.expiryTimer = setTimeout(() => this.expired(), ms);
  }

  private clear() {
    if (this.expiryTimer) clearTimeout(this.expiryTimer);
    try { sessionStorage.removeItem(TOKEN_KEY); } catch { /* ignore */ }
    this.session.set(null);
    this.me.set(null);
  }
}

/** Adds the bearer token to API calls (same-origin /api, /connect, /hubs). */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const token = inject(AuthService).token();
  const isApi = req.url.startsWith('/api/') || req.url.startsWith('/hubs/');
  return next(token && isApi && !req.headers.has('Authorization') ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req);
};

export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  if (auth.signedIn()) return true;
  return inject(Router).createUrlTree(['/sign-in'], { queryParams: { returnUrl: state.url } });
};

/** Route guard factory: requires any of the listed permissions (route data may also hide nav entries). */
export function permissionGuard(...perms: string[]): CanActivateFn {
  return () => {
    const auth = inject(AuthService);
    return auth.can(...perms) ? true : inject(Router).createUrlTree(['/']);
  };
}
