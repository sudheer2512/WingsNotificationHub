import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { interval, merge } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { Dashboard } from '../core/models';
import { RealtimeService } from '../core/realtime.service';
import { FilterModel, ScopeFilters, emptyFilters, filterQuery } from '../shared/filters';
import { Tone, UI, channelLabel, fmtMoney, fmtNum } from '../shared/ui';
import { BarItem, HBarChart, TrendChart, fillHours } from '../shared/widgets';

interface Card { label: string; value: string; icon: string; tone: Tone; hint?: string; link?: string; query?: Record<string, string>; }

@Component({
  selector: 'app-dashboard',
  imports: [MatButtonModule, MatIconModule, MatTooltipModule, RouterLink, ScopeFilters, HBarChart, TrendChart, ...UI],
  template: `
    <div class="page">
      <div class="page-head">
        <h1>Dashboard</h1>
        <span class="sub">@if (updated()) { updated {{ updated() | ago }} } · refreshes live</span>
        <span class="spacer"></span>
        <button mat-stroked-button (click)="load()"><mat-icon>refresh</mat-icon>Refresh</button>
      </div>
      <app-scope-filters [(value)]="filters" (changed)="load()" />
      <app-loading [on]="loading()" />
      <app-error [error]="error()" />

      @if (data(); as d) {
        <div class="cards">
          @for (c of cards(); track c.label) {
            <a class="card" [attr.data-tone]="c.tone" [routerLink]="c.link ?? null" [queryParams]="c.query ?? null" [matTooltip]="c.hint ?? ''">
              <mat-icon>{{ c.icon }}</mat-icon>
              <div style="min-width:0"><div class="v">{{ c.value }}</div><div class="l">{{ c.label }}</div></div>
            </a>
          }
        </div>

        <div class="grid-2" style="margin-top:12px">
          <section class="panel">
            <div class="panel-head"><h2>Deliveries per hour (last 24 h)</h2></div>
            <app-trend-chart [points]="hourly()" />
          </section>
          <section class="panel">
            <div class="panel-head"><h2>Channel usage</h2><span class="spacer"></span><span class="small muted">deliveries · delivered / failed</span></div>
            <app-hbar-chart [items]="channelBars()" ariaLabel="Deliveries by channel" />
          </section>
        </div>

        <div class="grid-2" style="margin-top:12px">
          <section class="panel">
            <div class="panel-head"><h2>Provider health</h2><span class="spacer"></span>
              @if (d.avgProviderResponseMs != null) { <span class="small muted">avg response {{ d.avgProviderResponseMs | num:1 }} ms (24 h)</span> }
              @if (auth.can('ViewProviders')) { <a routerLink="/providers" class="small">Providers</a> }
            </div>
            <div class="table-wrap">
              <table class="data">
                <thead><tr><th>Provider</th><th>Channel</th><th>Health</th><th class="num">Avg response</th><th class="num">Attempts 24 h</th></tr></thead>
                <tbody>
                  @for (p of d.providerHealth; track p.id) {
                    <tr>
                      <td>{{ p.name }}</td><td><app-channel [value]="p.channel" /></td>
                      <td>@if (p.suspended) { <app-chip value="Suspended" /> } @else { <app-chip [value]="p.healthStatus" [tone]="p.healthStatus === 'Unknown' ? 'off' : null" /> }</td>
                      <td class="num">{{ p.avgResponseMs != null ? (p.avgResponseMs | num:1) + ' ms' : '—' }}</td>
                      <td class="num">{{ p.attempts24h | num }}</td>
                    </tr>
                  } @empty { <tr><td colspan="5" class="muted">No providers visible</td></tr> }
                </tbody>
              </table>
            </div>
          </section>
          <section class="panel">
            <div class="panel-head"><h2>Queues</h2><span class="spacer"></span>@if (auth.can('ViewMonitoring')) { <a routerLink="/monitoring" class="small">Monitoring</a> }</div>
            @if (queues().length) {
              <div class="table-wrap">
                <table class="data">
                  <thead><tr><th>Queue</th><th class="num">Ready</th><th class="num">In flight</th><th class="num">Dead-lettered</th></tr></thead>
                  <tbody>
                    @for (q of queues(); track q.name) {
                      <tr><td><code>{{ q.name }}</code></td><td class="num">{{ q.ready | num }}</td><td class="num">{{ q.inFlight | num }}</td>
                        <td class="num">@if (q.deadLettered) { <app-chip tone="bad" [label]="'' + q.deadLettered" /> } @else { 0 }</td></tr>
                    }
                  </tbody>
                </table>
              </div>
            } @else { <app-empty icon="queue" text="Queue statistics are not available" /> }
            <h3 style="margin:12px 0 6px">Status breakdown</h3>
            <div class="wrap-row">
              @for (s of statuses(); track s.key) {
                @if (auth.can('ViewNotifications')) { <a [routerLink]="'/explorer'" [queryParams]="{ status: s.key }" style="text-decoration:none"><app-chip [value]="s.key" [label]="s.label" /></a> }
                @else { <app-chip [value]="s.key" [label]="s.label" /> }
              }
              @empty { <span class="muted small">No deliveries</span> }
            </div>
          </section>
        </div>
      } @else if (!loading() && !error()) {
        <app-empty icon="space_dashboard" text="No data" />
      }
    </div>`,
})
export class DashboardPage implements OnInit {
  private api = inject(ApiService);
  private rt = inject(RealtimeService);
  private destroyRef = inject(DestroyRef);
  auth = inject(AuthService);
  filters: FilterModel = emptyFilters();
  data = signal<Dashboard | null>(null);
  loading = signal(false);
  error = signal<unknown>(null);
  updated = signal<string | null>(null);

  hourly = computed(() => fillHours(this.data()?.hourly ?? []));
  channelBars = computed<BarItem[]>(() => (this.data()?.byChannel ?? []).slice().sort((a, b) => b.total - a.total)
    .map(c => ({ key: c.channel, label: channelLabel(c.channel), value: c.total, note: `${fmtNum(c.delivered)} / ${fmtNum(c.failed)}` })));
  queues = computed(() => Object.entries(this.data()?.queues ?? {}).map(([name, q]) => ({ name, ...q })));
  statuses = computed(() => Object.entries(this.data()?.byStatus ?? {}).sort((a, b) => b[1] - a[1]).map(([key, n]) => ({ key, label: `${key.replace(/([a-z])([A-Z])/g, '$1 $2')}: ${fmtNum(n)}` })));

  cards = computed<Card[]>(() => {
    const d = this.data();
    if (!d) return [];
    const rate = d.successRate;
    // Roles without the explorer see the numbers, not links that would turn them away.
    const explorer = this.auth.can('ViewNotifications') ? '/explorer' : undefined;
    const cards: Card[] = [
      { label: 'Notifications', value: fmtNum(d.totalNotifications), icon: 'notifications', tone: 'info', hint: `${fmtNum(d.totalDeliveries)} deliveries`, link: explorer },
      { label: 'Sent today', value: fmtNum(d.sentToday), icon: 'today', tone: 'info', link: explorer },
      { label: 'Delivered', value: fmtNum(d.delivered), icon: 'mark_email_read', tone: 'good', hint: `${fmtNum(d.accepted)} more accepted by providers`, link: explorer, query: { status: 'Delivered' } },
      { label: 'Failed', value: fmtNum(d.failed), icon: 'error', tone: d.failed ? 'bad' : 'good', link: explorer, query: { status: 'Failed' } },
      { label: 'Pending', value: fmtNum(d.pending), icon: 'hourglass_top', tone: d.pending ? 'warn' : 'good', hint: 'Scheduled, queued, processing, submitted or retrying', link: explorer },
      { label: 'Success rate', value: rate == null ? '—' : `${rate.toFixed(1)} %`, icon: 'percent', tone: rate == null ? 'off' : rate >= 95 ? 'good' : rate >= 80 ? 'warn' : 'bad',
        hint: '(delivered + accepted) / (delivered + accepted + failed)' },
      { label: 'Retry backlog', value: fmtNum(d.retryBacklog), icon: 'replay', tone: d.retryBacklog ? 'warn' : 'good', link: explorer, query: { status: 'RetryPending' } },
      { label: 'Awaiting operator', value: fmtNum(d.awaitingOperator), icon: 'support_agent', tone: d.awaitingOperator ? 'bad' : 'good',
        hint: 'Unknown-outcome deliveries waiting for an operator decision', link: explorer, query: { awaitingOperator: 'true' } },
      { label: 'Suppressed', value: fmtNum(d.suppressed), icon: 'block', tone: 'off', hint: 'Blocked by consent, preference or suppression policy', link: explorer, query: { status: 'Suppressed' } },
      { label: 'Estimated cost', value: fmtMoney(d.estimatedCost), icon: 'payments', tone: 'info', link: this.auth.can('ViewUsage') ? '/usage' : undefined },
    ];
    return cards;
  });

  ngOnInit() {
    this.load();
    merge(this.rt.changes(2500, ['delivery']), interval(60000)).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.load(true));
  }

  load(silent = false) {
    if (!silent) this.loading.set(true);
    this.api.dashboard(filterQuery(this.filters)).subscribe({
      next: d => { this.data.set(d); this.error.set(null); this.loading.set(false); this.updated.set(new Date().toISOString()); },
      error: e => { this.error.set(e); this.loading.set(false); },
    });
  }
}
