import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { problemText } from '../core/errors';
import { LookupsService } from '../core/lookups.service';
import { CHANNELS, MESSAGE_CLASSES, ProviderAccount, ProviderDetail, ProviderType, RoutingRule } from '../core/models';
import { UI } from '../shared/ui';
import { confirmDialog } from '../shared/widgets';
import { ProviderForm } from './provider-form';

@Component({
  selector: 'app-providers',
  imports: [FormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, MatTabsModule, MatTooltipModule, RouterLink, ...UI],
  template: `
    <div class="page">
      <div class="page-head">
        <h1>Provider configuration</h1><span class="spacer"></span>
        <button mat-stroked-button (click)="load()"><mat-icon>refresh</mat-icon>Refresh</button>
        @if (canManage()) { <button mat-flat-button (click)="edit()"><mat-icon>add</mat-icon>Add provider</button> }
      </div>
      <app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />
      <mat-tab-group mat-stretch-tabs="false">
        <mat-tab label="Accounts">
          <div class="split prov">
            <section class="panel">
              <mat-form-field class="full"><mat-label>Channel</mat-label>
                <mat-select [(ngModel)]="channel"><mat-option value="">All channels</mat-option>@for (c of channels; track c) { <mat-option [value]="c">{{ c | channel }}</mat-option> }</mat-select></mat-form-field>
              <app-loading [on]="loading()" />
              <div class="list-scroll">
                @for (p of filtered(); track p.id) {
                  <div class="prov-item" [class.selected]="p.id === detail()?.account?.id" (click)="open(p.id)" tabindex="0" (keyup.enter)="open(p.id)">
                    <div class="row"><b class="ellipsis">{{ p.name }}</b><span class="spacer"></span>
                      @if (p.suspended) { <app-chip value="Suspended" /> } @else if (!p.enabled) { <app-chip tone="off" label="Disabled" /> } @else { <app-chip [value]="p.healthStatus" [tone]="p.healthStatus === 'Unknown' ? 'off' : null" /> }</div>
                    <div class="small muted"><app-channel [value]="p.channel" /> · {{ typeName(p.providerType) }} · {{ p.scope }} · priority {{ p.priority }}
                      @if (p.pendingApprovalId) { · <span class="warn-text">credential change pending</span> }</div>
                  </div>
                } @empty { <app-empty icon="hub" text="No provider accounts" /> }
              </div>
            </section>
            <section style="min-width:0">
              @if (detail(); as d) {
                <div class="panel">
                  <div class="panel-head">
                    <h2>{{ d.account.name }}</h2><app-channel [value]="d.account.channel" /><span class="tag">{{ typeName(d.account.providerType) }}</span>
                    @if (d.account.suspended) { <app-chip value="Suspended" [tip]="d.account.suspendedReason ?? null" /> } @else { <app-chip [value]="d.account.healthStatus" [tone]="d.account.healthStatus === 'Unknown' ? 'off' : null" [tip]="d.account.lastHealthDetail ?? null" /> }
                    <span class="spacer"></span>
                    @if (canManage()) {
                      <button mat-stroked-button (click)="test(d.account)"><mat-icon>network_check</mat-icon>Test</button>
                      <button mat-stroked-button (click)="edit(d.account)"><mat-icon>edit</mat-icon>Edit</button>
                    }
                    @if (auth.can('EmergencySuspend')) {
                      <button mat-stroked-button [class.danger]="!d.account.suspended" (click)="suspend(d.account)"><mat-icon>{{ d.account.suspended ? 'play_arrow' : 'block' }}</mat-icon>{{ d.account.suspended ? 'Resume' : 'Suspend' }}</button>
                    }
                  </div>
                  @if (d.account.pendingApprovalId) {
                    <div class="callout warn">A credential change is waiting for approval by another administrator. The stored credentials stay in use until it is approved.
                      @if (auth.can('ApproveProviderChanges')) { <a routerLink="/approvals">Open approvals</a> }</div>
                  }
                  @if (testResult(); as t) {
                    <div class="callout" [class.good]="t.ok" [class.bad]="!t.ok"><b>Connectivity test:</b> <pre class="json" style="margin-top:4px">{{ t.text }}</pre></div>
                  }
                  <dl class="kv">
                    <dt>Scope</dt><dd>{{ d.account.scope }}@if (d.account.tenantId) { · {{ lookups.tenantName(d.account.tenantId) }} }@if (d.account.companyId) { · {{ lookups.companyName(d.account.companyId) }} }</dd>
                    <dt>Priority</dt><dd>{{ d.account.priority }}</dd>
                    <dt>Fallback provider</dt><dd>{{ d.account.failoverProviderAccountId ? lookups.providerName(d.account.failoverProviderAccountId) : '—' }}</dd>
                    <dt>Duplicate-risk policy</dt><dd>{{ d.account.duplicateRiskPolicy | human }}</dd>
                    <dt>Timeout</dt><dd>{{ d.account.timeoutSeconds }} s</dd>
                    <dt>Rate limit / capacity</dt><dd>{{ d.account.rateLimitPerSecond ?? '—' }} msg/s · {{ d.account.monthlyCapacity != null ? (d.account.monthlyCapacity | num) + '/month' : 'no monthly cap' }}</dd>
                    <dt>Countries</dt><dd>{{ d.account.allowedCountryPrefixes.join(', ') || 'any' }}</dd>
                    <dt>Unit cost</dt><dd>{{ d.account.unitCost != null ? (d.account.unitCost | money:d.account.currency) : '—' }}</dd>
                    <dt>Settings</dt><dd>@for (s of settings(d.account); track s[0]) { <span class="tag">{{ s[0] }} = {{ s[1] }}</span> } @empty { — }</dd>
                    <dt>Credentials</dt><dd>@for (k of secretKeys(d.account); track k) { <span class="tag">{{ k }}: ••••••••</span> } @empty { none stored }</dd>
                    <dt>Callback URL</dt><dd><code>{{ d.callbackUrl }}</code></dd>
                    <dt>Last health check</dt><dd>{{ d.account.lastHealthCheckAt | ago }} · {{ d.account.lastHealthDetail ?? '' }}</dd>
                  </dl>
                </div>
                <div class="grid-2" style="margin-top:12px">
                  <section class="panel">
                    <h2>Last 24 hours</h2>
                    <div class="table-wrap"><table class="data">
                      <thead><tr><th>Outcome</th><th class="num">Attempts</th><th class="num">Avg response</th></tr></thead>
                      <tbody>@for (s of d.last24h; track $index) { <tr><td><app-chip [value]="s.outcome ?? 'Unknown'" /></td><td class="num">{{ s.count | num }}</td><td class="num">{{ s.avgMs != null ? (s.avgMs | num:1) + ' ms' : '—' }}</td></tr> }
                        @empty { <tr><td colspan="3" class="muted">No attempts</td></tr> }</tbody>
                    </table></div>
                  </section>
                  <section class="panel">
                    <h2>Health history</h2>
                    <div class="table-wrap" style="max-height:260px"><table class="data">
                      <thead><tr><th>Time</th><th>Status</th><th>Detail</th></tr></thead>
                      <tbody>@for (h of d.health; track h.id) { <tr><td class="nowrap small">{{ h.occurredAt | dt }}</td><td><app-chip [value]="h.status" /></td><td class="small">{{ h.detail }}@if (h.latencyMs != null) { · {{ h.latencyMs }} ms }</td></tr> }
                        @empty { <tr><td colspan="3" class="muted">No health changes recorded (status changes are logged)</td></tr> }</tbody>
                    </table></div>
                  </section>
                </div>
                <section class="panel" style="margin-top:12px">
                  <div class="panel-head"><h2>Sender identities</h2></div>
                  <div class="table-wrap"><table class="data">
                    <thead><tr><th>Address</th><th>Display name</th><th>Scope</th><th>Details</th><th>Status</th><th></th></tr></thead>
                    <tbody>
                      @for (s of d.senders; track s.id) {
                        <tr><td><code>{{ s.address }}</code>@if (s.isDefault) { <span class="tag">default</span> }</td><td>{{ s.displayName ?? '—' }}</td>
                          <td class="small">{{ s.companyId ? lookups.companyName(s.companyId) : s.tenantId ? lookups.tenantName(s.tenantId) : 'shared' }}</td>
                          <td class="small">@if (s.dltEntityId) { DLT {{ s.dltEntityId }} }@if (s.whatsAppPhoneNumberId) { WA {{ s.whatsAppPhoneNumberId }} }@if (s.replyTo) { reply-to {{ s.replyTo }} }</td>
                          <td>@if (s.suspended) { <app-chip value="Suspended" /> } @else { <app-chip tone="good" label="Active" /> }</td>
                          <td>@if (auth.can('EmergencySuspend')) { <button mat-button (click)="suspendSender(s.id, !s.suspended)">{{ s.suspended ? 'Resume' : 'Suspend' }}</button> }</td></tr>
                      } @empty { <tr><td colspan="6" class="muted">No sender identities</td></tr> }
                    </tbody>
                  </table></div>
                  @if (canManage()) {
                    <h3 style="margin:10px 0 4px">Add sender</h3>
                    <div class="form-grid">
                      <mat-form-field><mat-label>{{ d.account.channel === 'email' ? 'From address' : d.account.channel === 'sms' ? 'Sender id / number' : 'Address' }}</mat-label><input matInput [(ngModel)]="sender.address"></mat-form-field>
                      <mat-form-field><mat-label>Display name</mat-label><input matInput [(ngModel)]="sender.displayName"></mat-form-field>
                      <mat-form-field><mat-label>Company (optional)</mat-label>
                        <mat-select [(ngModel)]="sender.companyId"><mat-option value="">Account scope</mat-option>@for (c of lookups.companiesOf(d.account.tenantId); track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }</mat-select></mat-form-field>
                      @if (d.account.channel === 'email') { <mat-form-field><mat-label>Reply-to</mat-label><input matInput [(ngModel)]="sender.replyTo"></mat-form-field> }
                      @if (d.account.channel === 'sms') { <mat-form-field><mat-label>DLT entity id</mat-label><input matInput [(ngModel)]="sender.dltEntityId"></mat-form-field> }
                      @if (d.account.channel === 'whatsapp') {
                        <mat-form-field><mat-label>Phone number id</mat-label><input matInput [(ngModel)]="sender.whatsAppPhoneNumberId"></mat-form-field>
                        <mat-form-field><mat-label>Business account id</mat-label><input matInput [(ngModel)]="sender.whatsAppBusinessAccountId"></mat-form-field>
                      }
                    </div>
                    <div class="wrap-row"><mat-checkbox [(ngModel)]="sender.isDefault">Default sender</mat-checkbox><span class="spacer"></span>
                      <button mat-stroked-button (click)="addSender(d.account)" [disabled]="!sender.address.trim()"><mat-icon>add</mat-icon>Add sender</button></div>
                  }
                </section>
              } @else { <div class="panel"><app-empty icon="hub" text="Select a provider account" /></div> }
            </section>
          </div>
        </mat-tab>

        <mat-tab label="Routing rules">
          <section class="panel">
            <p class="small muted">Routing rules pin a channel (optionally per country prefix or message class) to a provider account. Without a matching rule, enabled healthy providers are tried by priority.</p>
            <div class="table-wrap"><table class="data">
              <thead><tr><th>Order</th><th>Channel</th><th>Scope</th><th>Country</th><th>Class</th><th>Provider</th><th>Enabled</th><th></th></tr></thead>
              <tbody>
                @for (r of routing(); track r.id) {
                  <tr><td>{{ r.order }}</td><td><app-channel [value]="r.channel" /></td>
                    <td class="small">{{ r.tenantId ? lookups.tenantName(r.tenantId) : 'platform' }}@if (r.companyId) { / {{ lookups.companyName(r.companyId) }} }@if (r.applicationId) { / {{ lookups.appName(r.applicationId) }} }</td>
                    <td>{{ r.countryPrefix ?? 'any' }}</td><td>{{ r.messageClass ?? 'any' }}</td><td>{{ lookups.providerName(r.providerAccountId) }}</td>
                    <td>{{ r.enabled ? 'yes' : 'no' }}</td><td>@if (canManage()) { <button mat-icon-button (click)="deleteRoute(r)" aria-label="Delete rule"><mat-icon>delete</mat-icon></button> }</td></tr>
                } @empty { <tr><td colspan="8" class="muted">No routing rules</td></tr> }
              </tbody>
            </table></div>
            @if (canManage()) {
              <h3 style="margin:12px 0 4px">Add routing rule</h3>
              <div class="form-grid">
                <mat-form-field><mat-label>Channel</mat-label><mat-select [(ngModel)]="route.channel">@for (c of channels; track c) { <mat-option [value]="c">{{ c | channel }}</mat-option> }</mat-select></mat-form-field>
                <mat-form-field><mat-label>Provider</mat-label><mat-select [(ngModel)]="route.providerAccountId">@for (p of providersFor(route.channel); track p.id) { <mat-option [value]="p.id">{{ p.name }}</mat-option> }</mat-select></mat-form-field>
                <mat-form-field><mat-label>Tenant</mat-label><mat-select [(ngModel)]="route.tenantId">@if (auth.isPlatformAdmin()) { <mat-option value="">Platform-wide</mat-option> }
                  @for (t of lookups.tenants(); track t.id) { <mat-option [value]="t.id">{{ t.name }}</mat-option> }</mat-select></mat-form-field>
                <mat-form-field><mat-label>Company</mat-label><mat-select [(ngModel)]="route.companyId"><mat-option value="">Any</mat-option>@for (c of lookups.companiesOf(route.tenantId); track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }</mat-select></mat-form-field>
                <mat-form-field><mat-label>Country prefix</mat-label><input matInput [(ngModel)]="route.countryPrefix" placeholder="+91"></mat-form-field>
                <mat-form-field><mat-label>Message class</mat-label><mat-select [(ngModel)]="route.messageClass"><mat-option value="">Any</mat-option>@for (c of classes; track c) { <mat-option [value]="c">{{ c }}</mat-option> }</mat-select></mat-form-field>
                <mat-form-field><mat-label>Order</mat-label><input matInput type="number" [(ngModel)]="route.order"></mat-form-field>
              </div>
              <button mat-stroked-button (click)="addRoute()" [disabled]="!route.providerAccountId"><mat-icon>add</mat-icon>Add rule</button>
            }
          </section>
        </mat-tab>

        <mat-tab label="Sender domain guidance">
          <section class="panel">
            <div class="wrap-row">
              <mat-form-field class="wide" style="width:300px"><mat-label>Sender domain</mat-label><input matInput [(ngModel)]="domain" (keyup.enter)="guidance()" placeholder="mail.example.com"></mat-form-field>
              <button mat-stroked-button (click)="guidance()" [disabled]="!domain.trim()">Show DNS records</button>
            </div>
            @if (guide(); as g) {
              <div class="table-wrap"><table class="data">
                <thead><tr><th>Record</th><th>Type</th><th>Host</th><th>Value</th><th>Note</th></tr></thead>
                <tbody>@for (r of guideRows(); track r.name) { <tr><td><b>{{ r.name }}</b></td><td>{{ r.type }}</td><td><code>{{ r.host }}</code></td><td><code>{{ r.value }}</code></td><td class="small">{{ r.note }}</td></tr> }</tbody>
              </table></div>
              @if (g['returnPath']) { <p class="small">{{ g['returnPath'] }}</p> }
            }
          </section>
        </mat-tab>
      </mat-tab-group>
    </div>`,
  styles: [`
    .prov { grid-template-columns: minmax(240px, 340px) 1fr; }
    @media (max-width: 1200px) { .prov { grid-template-columns: 1fr; } .prov .list-scroll { max-height: 300px; } }
    .prov-item { padding: 6px 8px; border-radius: 6px; cursor: pointer; border: 1px solid transparent; }
    .prov-item:hover { background: var(--hover); } .prov-item.selected { background: var(--selected); border-color: var(--border); }
    .warn-text { color: var(--warn); }`],
})
export class Providers implements OnInit {
  private api = inject(ApiService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  auth = inject(AuthService);
  lookups = inject(LookupsService);
  id = input<string>();
  channels = CHANNELS; classes = MESSAGE_CLASSES;
  channel = '';
  providers = signal<ProviderAccount[]>([]);
  types = signal<ProviderType[]>([]);
  routing = signal<RoutingRule[]>([]);
  detail = signal<ProviderDetail | null>(null);
  loading = signal(false);
  error = signal<unknown>(null);
  testResult = signal<{ ok: boolean; text: string } | null>(null);
  guide = signal<Record<string, unknown> | null>(null);
  domain = '';
  sender = { address: '', displayName: '', companyId: '', replyTo: '', dltEntityId: '', whatsAppPhoneNumberId: '', whatsAppBusinessAccountId: '', isDefault: false };
  route = { channel: 'sms', providerAccountId: '', tenantId: '', companyId: '', countryPrefix: '', messageClass: '', order: 0 };

  canManage = computed(() => this.auth.can('ManageProviders'));
  filtered = computed(() => this.providers().filter(p => !this.channel || p.channel === this.channel));
  guideRows = computed(() => {
    const g = this.guide();
    if (!g) return [];
    return (['spf', 'dkim', 'dmarc'] as const).map(k => ({ name: k.toUpperCase(), ...(g[k] as { type: string; host: string; value?: string; note?: string }) }));
  });

  ngOnInit() {
    this.load();
    this.api.providerTypes().subscribe({ next: t => this.types.set(t), error: () => undefined });
    this.loadRouting();
  }

  load(selectId?: string) {
    this.loading.set(true);
    this.api.providers().subscribe({
      next: p => { this.providers.set(p); this.lookups.providers.set(p); this.loading.set(false); const id = selectId ?? this.detail()?.account.id ?? this.id(); if (id) this.open(id); },
      error: e => { this.error.set(e); this.loading.set(false); },
    });
  }

  loadRouting() { this.api.routingRules().subscribe({ next: r => this.routing.set(r), error: e => this.error.set(e) }); }

  typeName(t: string) { return this.types().find(x => x.providerType === t)?.displayName ?? t; }
  settings(a: ProviderAccount) { return Object.entries(a.settings ?? {}).map(([k, v]) => [k, typeof v === 'object' ? JSON.stringify(v) : String(v)]); }
  secretKeys(a: ProviderAccount) { return Object.keys(a.secrets ?? {}); }
  providersFor(ch: string) { return this.providers().filter(p => p.channel === ch); }

  open(id: string) {
    if (this.detail()?.account.id !== id) this.testResult.set(null);
    this.api.provider(id).subscribe({ next: d => this.detail.set(d), error: e => this.error.set(e) });
  }

  edit(a?: ProviderAccount) {
    this.dialog.open(ProviderForm, { data: { account: a, types: this.types() }, width: '860px', maxWidth: '96vw' }).afterClosed().subscribe(r => {
      if (!r) return;
      const res = r as { pendingApprovalId?: string; note?: string; account?: ProviderAccount; id?: string };
      this.snack.open(res.note ?? 'Provider saved', 'OK', { duration: res.note ? 8000 : 3000 });
      this.load(res.account?.id ?? res.id);
    });
  }

  test(a: ProviderAccount) {
    confirmDialog(this.dialog, { title: `Test ${a.name}`, message: 'Checks connectivity and credentials. Optionally also sends a real test message.', confirmText: 'Run test',
      comment: { label: 'Send a test message to (optional)', placeholder: a.channel === 'email' ? 'someone@example.com' : '+9198…' } }).subscribe(res => {
      if (!res) return;
      this.testResult.set({ ok: true, text: 'Testing…' });
      this.api.testProvider(a.id, res.comment || undefined).subscribe({
        next: r => { const h = r.health as { status?: string }; this.testResult.set({ ok: h?.status === 'Healthy', text: JSON.stringify(r, null, 2) }); this.open(a.id); },
        error: e => this.testResult.set({ ok: false, text: problemText(e) }),
      });
    });
  }

  suspend(a: ProviderAccount) {
    const suspending = !a.suspended;
    confirmDialog(this.dialog, {
      title: suspending ? `Suspend ${a.name}` : `Resume ${a.name}`, danger: suspending, confirmText: suspending ? 'Suspend' : 'Resume',
      message: suspending ? 'Traffic stops using this account immediately; routing falls back to other providers.' : 'The account becomes eligible for routing again.',
      comment: { label: 'Reason', required: suspending },
    }).subscribe(r => {
      if (!r) return;
      this.api.suspendProvider(a.id, suspending, r.comment || undefined).subscribe({ next: () => { this.load(); this.snack.open(suspending ? 'Provider suspended' : 'Provider resumed', 'OK', { duration: 3000 }); }, error: e => this.error.set(e) });
    });
  }

  suspendSender(id: string, suspended: boolean) {
    this.api.suspendSender(id, suspended, suspended ? 'Suspended from console' : 'Resumed from console').subscribe({ next: () => this.open(this.detail()!.account.id), error: e => this.error.set(e) });
  }

  addSender(a: ProviderAccount) {
    const s = this.sender;
    this.api.addSender(a.id, { tenantId: a.tenantId ?? null, companyId: s.companyId || null, channel: a.channel, address: s.address.trim(), displayName: s.displayName || null, replyTo: s.replyTo || null,
      signature: null, dltEntityId: s.dltEntityId || null, whatsAppPhoneNumberId: s.whatsAppPhoneNumberId || null, whatsAppBusinessAccountId: s.whatsAppBusinessAccountId || null, isDefault: s.isDefault })
      .subscribe({ next: () => { this.sender = { address: '', displayName: '', companyId: '', replyTo: '', dltEntityId: '', whatsAppPhoneNumberId: '', whatsAppBusinessAccountId: '', isDefault: false }; this.open(a.id); }, error: e => this.error.set(e) });
  }

  addRoute() {
    const r = this.route;
    this.api.createRoutingRule({ tenantId: r.tenantId || null, companyId: r.companyId || null, applicationId: null, channel: r.channel, countryPrefix: r.countryPrefix || null,
      messageClass: r.messageClass || null, providerAccountId: r.providerAccountId, order: Number(r.order) || 0, enabled: true })
      .subscribe({ next: () => { this.loadRouting(); this.snack.open('Routing rule added', 'OK', { duration: 3000 }); }, error: e => this.error.set(e) });
  }

  deleteRoute(r: RoutingRule) {
    confirmDialog(this.dialog, { title: 'Delete routing rule', message: 'Remove this routing rule?', confirmText: 'Delete', danger: true }).subscribe(ok => {
      if (ok) this.api.deleteRoutingRule(r.id).subscribe({ next: () => this.loadRouting(), error: e => this.error.set(e) });
    });
  }

  guidance() { this.api.senderGuidance(this.domain.trim()).subscribe({ next: g => this.guide.set(g), error: e => this.error.set(e) }); }
}
