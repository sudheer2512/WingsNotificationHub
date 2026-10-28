import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom, forkJoin, of, catchError, map } from 'rxjs';
import { ApiService } from './api.service';
import { AuthService } from './auth.service';
import { Application, Company, ProviderAccount, Tenant } from './models';

/** Cached tenants/companies/applications/providers for filters and id → name display. Refresh after admin edits. */
@Injectable({ providedIn: 'root' })
export class LookupsService {
  private api = inject(ApiService);
  private auth = inject(AuthService);
  private loadedFor: string | null = null;

  readonly tenants = signal<Tenant[]>([]);
  readonly companies = signal<Company[]>([]);
  readonly applications = signal<Application[]>([]);
  readonly providers = signal<ProviderAccount[]>([]);

  private tenantMap = computed(() => new Map(this.tenants().map(t => [t.id, t])));
  private companyMap = computed(() => new Map(this.companies().map(c => [c.id, c])));
  private appMap = computed(() => new Map(this.applications().map(a => [a.id, a])));
  private providerMap = computed(() => new Map(this.providers().map(p => [p.id, p])));

  /** Loads once per signed-in user (cheap no-op afterwards). */
  ensure(): void {
    const who = this.auth.me()?.actorId ?? null;
    if (!who || this.loadedFor === who) return;
    this.loadedFor = who;
    this.refresh();
  }

  /** Loads the lookups and resolves when they are in place (used before the first screen renders). */
  load(): Promise<void> {
    this.loadedFor = this.auth.me()?.actorId ?? null;
    return firstValueFrom(this.fetch().pipe(map(() => undefined)));
  }

  refresh(): void { this.fetch().subscribe(); }

  private fetch() {
    const safe = <T>(o: import('rxjs').Observable<T[]>) => o.pipe(catchError(() => of([] as T[])));
    const canTenancy = this.auth.can('ViewTenancy');
    return forkJoin({
      tenants: canTenancy ? safe(this.api.tenants()) : of([] as Tenant[]),
      companies: canTenancy ? safe(this.api.companies()) : of([] as Company[]),
      applications: canTenancy ? safe(this.api.applications()) : of([] as Application[]),
      providers: this.auth.can('ViewProviders') ? safe(this.api.providers()) : of([] as ProviderAccount[]),
    }).pipe(map(r => {
      // Fall back to the tenants listed by /auth/me when the user cannot list tenancy.
      this.tenants.set(r.tenants.length ? r.tenants : (this.auth.me()?.tenants ?? []).map(t => ({ id: t.id, name: t.name, code: t.code } as Tenant)));
      this.companies.set(r.companies);
      this.applications.set(r.applications);
      this.providers.set(r.providers);
      return r;
    }));
  }

  reset(): void { this.loadedFor = null; }

  tenantName(id?: string | null) { return id ? this.tenantMap().get(id)?.name ?? short(id) : '—'; }
  companyName(id?: string | null) { return id ? this.companyMap().get(id)?.name ?? short(id) : '—'; }
  appName(id?: string | null) { return id ? this.appMap().get(id)?.name ?? short(id) : '—'; }
  providerName(id?: string | null) { return id ? this.providerMap().get(id)?.name ?? short(id) : '—'; }
  company(id?: string | null) { return id ? this.companyMap().get(id) ?? null : null; }
  application(id?: string | null) { return id ? this.appMap().get(id) ?? null : null; }
  companiesOf(tenantId?: string | null) { return this.companies().filter(c => !tenantId || c.tenantId === tenantId); }
  appsOf(tenantId?: string | null) { return this.applications().filter(a => !tenantId || a.tenantId === tenantId); }
  /** Applications enabled for a company. */
  appsForCompany(companyId?: string | null) {
    if (!companyId) return this.applications();
    return this.applications().filter(a => a.companies.some(l => l.companyId === companyId && l.enabled));
  }
}

function short(id: string) { return id.length > 8 ? id.slice(0, 8) + '…' : id; }
