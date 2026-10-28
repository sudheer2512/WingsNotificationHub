import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../core/api.service';
import { LookupsService } from '../core/lookups.service';
import { Webhook, WebhookAttempt } from '../core/models';
import { UI } from '../shared/ui';
import { confirmDialog, showSecret } from '../shared/widgets';

const EVENT_TYPES = ['delivery.status_changed', 'notification.completed', 'inbound.message_received', 'push.interaction', 'inapp.action', 'notification.webhook'];

@Component({
  selector: 'app-webhooks',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, ...UI],
  template: `
    <div class="page">
      <div class="page-head"><h1>Webhooks</h1><span class="sub">Signed callbacks to applications (X-Wings-Signature, HMAC-SHA256)</span><span class="spacer"></span>
        <button mat-stroked-button (click)="load()"><mat-icon>refresh</mat-icon>Refresh</button></div>
      <app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />
      <section class="panel">
        <div class="table-wrap"><table class="data">
          <thead><tr><th>Name</th><th>URL</th><th>Application / company</th><th>Events</th><th>Status</th><th></th></tr></thead>
          <tbody>@for (w of hooks(); track w.id) {
            <tr [class.selected]="w.id === sel()?.id">
              <td><b>{{ w.name }}</b><div class="small muted">{{ w.createdAt | dt }}</div></td><td class="small"><code>{{ w.url }}</code></td>
              <td class="small">{{ lookups.appName(w.applicationId) }}@if (w.companyId) { / {{ lookups.companyName(w.companyId) }} }</td>
              <td>@for (e of w.eventTypes; track e) { <span class="tag">{{ e }}</span> }</td>
              <td>@if (w.enabled) { <app-chip tone="good" label="Enabled" /> } @else { <app-chip tone="bad" label="Disabled" [tip]="'Disabled ' + (w.disabledAt | dt)" /> }
                @if (w.consecutiveFailures) { <div class="small error">{{ w.consecutiveFailures }} consecutive failures</div> }</td>
              <td class="nowrap">
                <button mat-button (click)="attempts(w)"><mat-icon>history</mat-icon>Attempts</button>
                <button mat-button (click)="test(w)"><mat-icon>send</mat-icon>Test</button>
                @if (!w.enabled) { <button mat-button (click)="enable(w)"><mat-icon>play_arrow</mat-icon>Enable</button> }
                <button mat-icon-button (click)="remove(w)" aria-label="Delete webhook"><mat-icon>delete</mat-icon></button>
              </td>
            </tr>
          } @empty { <tr><td colspan="6" class="muted">No webhooks</td></tr> }</tbody>
        </table></div>
      </section>
      @if (sel(); as w) {
        <section class="panel" style="margin-top:12px">
          <div class="panel-head"><h2>Delivery attempts · {{ w.name }}</h2><span class="spacer"></span><button mat-icon-button (click)="attempts(w)" aria-label="Reload"><mat-icon>refresh</mat-icon></button></div>
          <div class="table-wrap" style="max-height:360px"><table class="data">
            <thead><tr><th>Time</th><th>Event</th><th>Result</th><th class="num">HTTP</th><th class="num">Duration</th><th>Error</th></tr></thead>
            <tbody>@for (a of tries(); track a.id) {
              <tr><td class="small nowrap">{{ $any(a)['attemptedAt'] | dt }}</td><td class="small">{{ $any(a)['eventType'] ?? '—' }}</td>
                <td>@if (ok(a)) { <app-chip tone="good" label="Delivered" /> } @else { <app-chip tone="bad" label="Failed" /> }</td>
                <td class="num">{{ $any(a)['responseCode'] ?? '—' }}</td><td class="num">{{ $any(a)['durationMs'] != null ? $any(a)['durationMs'] + ' ms' : '—' }}</td>
                <td class="small error">{{ $any(a)['error'] ?? '' }}</td></tr>
            } @empty { <tr><td colspan="6" class="muted">No attempts yet</td></tr> }</tbody>
          </table></div>
        </section>
      }
      <section class="panel" style="margin-top:12px">
        <h2>Register webhook</h2>
        <div class="form-grid">
          <mat-form-field><mat-label>Application</mat-label><mat-select [(ngModel)]="m.applicationId">@for (a of lookups.applications(); track a.id) { <mat-option [value]="a.id">{{ a.name }}</mat-option> }</mat-select></mat-form-field>
          <mat-form-field><mat-label>Company (optional)</mat-label><mat-select [(ngModel)]="m.companyId"><mat-option value="">All companies</mat-option>
            @for (c of lookups.companiesOf(lookups.application(m.applicationId)?.tenantId); track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }</mat-select></mat-form-field>
          <mat-form-field><mat-label>Name</mat-label><input matInput [(ngModel)]="m.name"></mat-form-field>
          <mat-form-field class="span-2"><mat-label>HTTPS URL</mat-label><input matInput [(ngModel)]="m.url" placeholder="https://erp.example.com/hooks/notifications"></mat-form-field>
          <mat-form-field class="span-2"><mat-label>Event types</mat-label><mat-select [(ngModel)]="m.eventTypes" multiple><mat-option value="*">All events (*)</mat-option>
            @for (e of eventTypes; track e) { <mat-option [value]="e">{{ e }}</mat-option> }</mat-select></mat-form-field>
        </div>
        <button mat-flat-button (click)="create()" [disabled]="!m.applicationId || !m.name.trim() || !m.url.trim() || !m.eventTypes.length"><mat-icon>add_link</mat-icon>Register</button>
      </section>
    </div>`,
})
export class Webhooks implements OnInit {
  private api = inject(ApiService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  lookups = inject(LookupsService);
  eventTypes = EVENT_TYPES;
  hooks = signal<Webhook[]>([]);
  sel = signal<Webhook | null>(null);
  tries = signal<WebhookAttempt[]>([]);
  error = signal<unknown>(null);
  m = { applicationId: '', companyId: '', name: '', url: '', eventTypes: ['delivery.status_changed'] as string[] };

  ngOnInit() { this.load(); this.m.applicationId = this.lookups.applications()[0]?.id ?? ''; }
  load() { this.api.webhooks().subscribe({ next: w => this.hooks.set(w), error: e => this.error.set(e) }); }
  ok(a: WebhookAttempt) { const r = a as Record<string, unknown>; return r['succeeded'] === true || r['success'] === true || (typeof r['statusCode'] === 'number' && (r['statusCode'] as number) < 300 && !r['error']); }
  attempts(w: Webhook) { this.sel.set(w); this.api.webhookAttempts(w.id).subscribe({ next: a => this.tries.set(a), error: e => this.error.set(e) }); }
  test(w: Webhook) { this.api.testWebhook(w.id).subscribe({ next: r => { this.snack.open(`Test event ${r.eventId.slice(0, 8)} queued`, 'OK', { duration: 3000 }); setTimeout(() => this.attempts(w), 2500); }, error: e => this.error.set(e) }); }
  enable(w: Webhook) { this.api.enableWebhook(w.id).subscribe({ next: () => this.load(), error: e => this.error.set(e) }); }
  remove(w: Webhook) {
    confirmDialog(this.dialog, { title: 'Delete webhook', message: `Stop sending events to ${w.url}?`, confirmText: 'Delete', danger: true }).subscribe(r => {
      if (r) this.api.deleteWebhook(w.id).subscribe({ next: () => { if (this.sel()?.id === w.id) this.sel.set(null); this.load(); }, error: e => this.error.set(e) });
    });
  }
  create() {
    this.api.createWebhook({ applicationId: this.m.applicationId, companyId: this.m.companyId || null, name: this.m.name.trim(), url: this.m.url.trim(), eventTypes: this.m.eventTypes }).subscribe({
      next: r => { showSecret(this.dialog, 'Webhook registered', r.note, [{ label: `Signing secret (${r.signatureHeader})`, value: r.signingSecret, secret: true }]); this.m.name = ''; this.m.url = ''; this.load(); },
      error: e => this.error.set(e),
    });
  }
}
