import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { interval } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { LookupsService } from '../core/lookups.service';
import { Monitoring } from '../core/models';
import { Tone, UI, fmtNum, parseDate } from '../shared/ui';
import { confirmDialog } from '../shared/widgets';

@Component({
  selector: 'app-monitoring',
  imports: [FormsModule, MatButtonModule, MatCheckboxModule, MatIconModule, MatTooltipModule, RouterLink, ...UI],
  template: `
    <div class="page">
      <div class="page-head">
        <h1>System monitoring</h1>@if (data(); as d) { <span class="sub">transport: {{ d.transport }} · refreshed {{ updated() | ago }} · auto every 15 s</span> }
        <span class="spacer"></span>
        <button mat-stroked-button (click)="load()"><mat-icon>refresh</mat-icon>Refresh</button>
      </div>
      <app-loading [on]="loading()" />
      <app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />
      @if (data(); as d) {
        <div class="cards">
          @for (c of cards(); track c.label) {
            <a class="card" [attr.data-tone]="c.tone" [routerLink]="c.link ?? null" [queryParams]="c.query ?? null"><mat-icon>{{ c.icon }}</mat-icon><div><div class="v">{{ c.value }}</div><div class="l">{{ c.label }}</div></div></a>
          }
        </div>
        <div class="grid-2" style="margin-top:12px">
          <section class="panel">
            <h2>Queues</h2>
            <div class="table-wrap"><table class="data">
              <thead><tr><th>Queue</th><th class="num">Ready</th><th class="num">In flight</th><th class="num">Dead-lettered</th></tr></thead>
              <tbody>@for (q of queues(); track q.name) { <tr><td><code>{{ q.name }}</code></td><td class="num">{{ q.ready | num }}</td><td class="num">{{ q.inFlight | num }}</td>
                <td class="num">@if (q.deadLettered) { <app-chip tone="bad" [label]="'' + q.deadLettered" /> } @else { 0 }</td></tr> }</tbody>
            </table></div>
            <div class="small muted" style="margin-top:6px">Outbox backlog (not yet published to queues): <b>{{ d.outboxBacklog | num }}</b></div>
          </section>
          <section class="panel">
            <div class="panel-head"><h2>Workers</h2><span class="spacer"></span><mat-checkbox [(ngModel)]="showStopped">Show stopped instances</mat-checkbox></div>
            <div class="table-wrap" style="max-height:340px"><table class="data">
              <thead><tr><th>Worker</th><th>Instance</th><th>Status</th><th class="num">Processed</th><th class="num">Failed</th><th>Heartbeat</th></tr></thead>
              <tbody>@for (w of workers(); track w.worker + w.instance) {
                <tr><td><code>{{ w.worker }}</code></td><td class="small">{{ w.instance }}</td><td><app-chip [value]="w.status" [tip]="w.lastError ?? null" /></td>
                  <td class="num">{{ w.processed | num }}</td><td class="num">{{ w.failed | num }}</td><td class="small nowrap" [title]="w.lastHeartbeatAt | dt">{{ w.lastHeartbeatAt | ago }}</td></tr>
              } @empty { <tr><td colspan="6" class="muted">No worker heartbeats</td></tr> }</tbody>
            </table></div>
          </section>
        </div>

        <section class="panel" style="margin-top:12px">
          <div class="panel-head"><h2>Alerts</h2><span class="small muted">{{ d.alerts.length }} unacknowledged</span></div>
          @for (a of d.alerts; track a.id) {
            <div class="alert-row">
              <app-chip [value]="a.severity" />
              <div style="flex:1;min-width:0"><div>{{ a.message }}</div><div class="small muted">{{ a.source }} · {{ a.createdAt | ago }}@if (a.tenantId) { · {{ lookups.tenantName(a.tenantId) }} }</div></div>
              @if (auth.can('AcknowledgeAlerts')) { <button mat-stroked-button (click)="ack(a.id)"><mat-icon>done</mat-icon>Acknowledge</button> }
            </div>
          } @empty { <app-empty icon="task_alt" text="No open alerts" /> }
        </section>

        <div class="grid-2" style="margin-top:12px">
          <section class="panel">
            <h2>Provider errors (24 h)</h2>
            <div class="table-wrap"><table class="data">
              <thead><tr><th>Provider</th><th>Error code</th><th class="num">Count</th></tr></thead>
              <tbody>@for (e of d.providerErrors24h; track $index) { <tr><td>{{ lookups.providerName(e.providerAccountId) }}</td><td><code>{{ e.errorCode ?? '—' }}</code></td><td class="num">{{ e.n | num }}</td></tr> }
                @empty { <tr><td colspan="3" class="muted">No provider errors</td></tr> }</tbody>
            </table></div>
          </section>
          <section class="panel">
            <h2>Circuit breakers</h2>
            <div class="table-wrap"><table class="data">
              <thead><tr><th>Provider</th><th class="num">Consecutive failures</th><th>State</th></tr></thead>
              <tbody>@for (c of d.circuits; track c.providerAccountId) {
                <tr><td>{{ lookups.providerName(c.providerAccountId) }}</td><td class="num">{{ c.failures }}</td>
                  <td>@if (isOpen(c.openUntil)) { <app-chip tone="bad" icon="power_off" [label]="'Open until ' + (c.openUntil | dt)" /> } @else { <app-chip tone="good" icon="power" label="Closed" /> }</td></tr>
              } @empty { <tr><td colspan="3" class="muted">No circuit activity</td></tr> }</tbody>
            </table></div>
          </section>
        </div>

        <section class="panel" style="margin-top:12px">
          <div class="panel-head"><h2>Dead letters</h2><span class="small muted">messages that exhausted their retries in the queue</span></div>
          <div class="table-wrap"><table class="data">
            <thead><tr><th>Queue</th><th>Type</th><th class="num">Dequeues</th><th>Last error</th><th>Dead-lettered</th><th></th></tr></thead>
            <tbody>@for (m of d.deadLetters; track m.id) {
              <tr><td><code>{{ m.queue }}</code></td><td>{{ m.type }}</td><td class="num">{{ m.dequeueCount }}</td><td class="small error" style="max-width:420px">{{ m.lastError ?? '—' }}</td>
                <td class="small nowrap">{{ m.deadLetteredAt | dt }}</td>
                <td>@if (d.canRequeue) { <button mat-stroked-button (click)="requeue(m.id)"><mat-icon>replay</mat-icon>Requeue</button> }</td></tr>
            } @empty { <tr><td colspan="6" class="muted">No dead letters</td></tr> }</tbody>
          </table></div>
        </section>
      }
    </div>`,
  styles: [`.alert-row { display: flex; gap: 8px; align-items: center; padding: 6px 0; border-bottom: 1px solid var(--border); } .alert-row:last-child { border-bottom: 0; }`],
})
export class MonitoringPage implements OnInit {
  private api = inject(ApiService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  private destroyRef = inject(DestroyRef);
  auth = inject(AuthService);
  lookups = inject(LookupsService);
  data = signal<Monitoring | null>(null);
  loading = signal(false);
  error = signal<unknown>(null);
  updated = signal<string | null>(null);
  showStopped = false;

  queues = computed(() => Object.entries(this.data()?.queues ?? {}).map(([name, q]) => ({ name, ...q })));
  workers = () => (this.data()?.workers ?? []).filter(w => this.showStopped || w.status !== 'stopped');
  cards = computed(() => {
    const d = this.data();
    if (!d) return [];
    const q = Object.values(d.queues ?? {});
    const ready = q.reduce((s, x) => s + x.ready, 0), dead = q.reduce((s, x) => s + x.deadLettered, 0) + d.deadLetters.length;
    const unresponsive = d.workers.filter(w => w.status === 'unresponsive').length;
    const open = d.circuits.filter(c => this.isOpen(c.openUntil)).length;
    const t = (bad: boolean, warn = false): Tone => bad ? 'bad' : warn ? 'warn' : 'good';
    return [
      { label: 'Queued messages', value: fmtNum(ready), icon: 'queue', tone: t(false, ready > 1000) },
      { label: 'Retry backlog', value: fmtNum(d.retries), icon: 'replay', tone: t(false, d.retries > 0), link: '/explorer', query: { status: 'RetryPending' } },
      { label: 'Awaiting operator', value: fmtNum(d.awaitingOperator), icon: 'support_agent', tone: t(d.awaitingOperator > 0), link: '/explorer', query: { awaitingOperator: 'true' } },
      { label: 'Dead letters', value: fmtNum(dead), icon: 'report', tone: t(dead > 0) },
      { label: 'Unresponsive workers', value: fmtNum(unresponsive), icon: 'memory', tone: t(unresponsive > 0) },
      { label: 'Open circuits', value: fmtNum(open), icon: 'power_off', tone: t(open > 0) },
      { label: 'Open alerts', value: fmtNum(d.alerts.length), icon: 'notification_important', tone: t(d.alerts.some(a => a.severity === 'critical'), d.alerts.length > 0) },
    ] as { label: string; value: string; icon: string; tone: Tone; link?: string; query?: Record<string, string> }[];
  });

  ngOnInit() {
    this.load();
    interval(15000).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.load(true));
  }

  load(silent = false) {
    if (!silent) this.loading.set(true);
    this.api.monitoring().subscribe({
      next: d => { this.data.set(d); this.loading.set(false); this.error.set(null); this.updated.set(new Date().toISOString()); },
      error: e => { this.error.set(e); this.loading.set(false); },
    });
  }

  isOpen(until?: string | null) { const d = parseDate(until); return !!d && d.getTime() > Date.now(); }

  ack(id: string) { this.api.ackAlert(id).subscribe({ next: () => this.load(true), error: e => this.error.set(e) }); }

  requeue(id: string) {
    confirmDialog(this.dialog, { title: 'Requeue message', message: 'Put this message back on its queue for another processing attempt?', confirmText: 'Requeue' }).subscribe(r => {
      if (r) this.api.requeueDeadLetter(id).subscribe({ next: () => { this.snack.open('Requeued', 'OK', { duration: 2500 }); this.load(true); }, error: e => this.error.set(e) });
    });
  }
}
