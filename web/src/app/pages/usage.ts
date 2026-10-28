import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTabsModule } from '@angular/material/tabs';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { LookupsService } from '../core/lookups.service';
import { BillingRate, CHANNELS, CostRate, CreditAccount, MESSAGE_CLASSES, Quota, UsageRow } from '../core/models';
import { FilterModel, ScopeFilters, emptyFilters, filterQuery } from '../shared/filters';
import { UI, channelLabel } from '../shared/ui';
import { BarItem, HBarChart, confirmDialog } from '../shared/widgets';

@Component({
  selector: 'app-usage',
  imports: [FormsModule, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, MatTabsModule, ScopeFilters, HBarChart, ...UI],
  template: `
    <div class="page">
      <div class="page-head"><h1>Usage & billing</h1><span class="spacer"></span><button mat-stroked-button (click)="loadAll()"><mat-icon>refresh</mat-icon>Refresh</button></div>
      <app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />
      <mat-tab-group mat-stretch-tabs="false">
        <mat-tab label="Usage">
          <app-scope-filters [(value)]="filters" (changed)="loadUsage()" />
          <div class="row wrap-row" style="margin-bottom:8px">
            <span class="small muted">Group by</span>
            <mat-button-toggle-group [(ngModel)]="groupBy" (change)="loadUsage()" hideSingleSelectionIndicator aria-label="Group by">
              @for (g of groupings; track g) { <mat-button-toggle [value]="g">{{ g | human }}</mat-button-toggle> }
            </mat-button-toggle-group>
          </div>
          <app-loading [on]="loading()" />
          <div class="cards">
            <div class="card" data-tone="info"><mat-icon>forum</mat-icon><div><div class="v">{{ totals().messages | num }}</div><div class="l">Messages ({{ totals().units | num }} units)</div></div></div>
            <div class="card" data-tone="info"><mat-icon>calculate</mat-icon><div><div class="v">{{ totals().estimatedCost | money }}</div><div class="l">Estimated cost</div></div></div>
            <div class="card" data-tone="info"><mat-icon>receipt</mat-icon><div><div class="v">{{ totals().providerReportedCost != null ? (totals().providerReportedCost | money) : '—' }}</div><div class="l">Provider-reported cost</div></div></div>
            <div class="card" data-tone="good"><mat-icon>request_quote</mat-icon><div><div class="v">{{ totals().billedAmount | money }}</div><div class="l">Billed</div></div></div>
            <div class="card" [attr.data-tone]="totals().margin < 0 ? 'bad' : 'good'"><mat-icon>trending_up</mat-icon><div><div class="v">{{ totals().margin | money }}</div><div class="l">Margin</div></div></div>
          </div>
          <div class="grid-2" style="margin-top:12px">
            <section class="panel">
              <h2>Messages by {{ groupBy | human }}</h2>
              <app-hbar-chart [items]="bars()" [ariaLabel]="'Messages by ' + groupBy" />
            </section>
            <section class="panel">
              <h2>Cost and billing by {{ groupBy | human }}</h2>
              <div class="table-wrap"><table class="data">
                <thead><tr><th>{{ groupBy | human }}</th><th class="num">Messages</th><th class="num">Units</th><th class="num">Estimated</th><th class="num">Provider-reported</th><th class="num">Billed</th><th class="num">Margin</th></tr></thead>
                <tbody>@for (r of rows(); track r.key) {
                  <tr><td>{{ label(r.key) }}</td><td class="num">{{ r.messages | num }}</td><td class="num">{{ r.units | num }}</td><td class="num">{{ r.estimatedCost | money }}</td>
                    <td class="num">{{ r.providerReportedCost != null ? (r.providerReportedCost | money) : '—' }}</td><td class="num">{{ r.billedAmount | money }}</td>
                    <td class="num" [class.error]="r.margin < 0">{{ r.margin | money }}</td></tr>
                } @empty { <tr><td colspan="7" class="muted">No usage in this period</td></tr> }</tbody>
              </table></div>
            </section>
          </div>
        </mat-tab>

        <mat-tab label="Quotas">
          <section class="panel">
            @for (q of quotas(); track q.id) {
              <div class="quota">
                <div class="row"><b>{{ quotaLabel(q) }}</b><span class="spacer"></span><span class="small">{{ q.used | num }} / {{ q.monthlyLimit | num }} this month ({{ pct(q) }} %)</span>
                  @if (canQuota()) { <button mat-icon-button (click)="deleteQuota(q)" aria-label="Delete quota"><mat-icon>delete</mat-icon></button> }</div>
                <app-meter [value]="q.used" [max]="q.monthlyLimit" [warnAt]="q.alertThresholdPercent" />
                <div class="small muted">Alert at {{ q.alertThresholdPercent }} %</div>
              </div>
            } @empty { <app-empty icon="data_usage" text="No quotas configured" /> }
            @if (canQuota()) {
              <h3 style="margin:12px 0 4px">Set quota</h3>
              <div class="form-grid">
                <mat-form-field><mat-label>Tenant</mat-label><mat-select [(ngModel)]="quota.tenantId">@for (t of lookups.tenants(); track t.id) { <mat-option [value]="t.id">{{ t.name }}</mat-option> }</mat-select></mat-form-field>
                <mat-form-field><mat-label>Company</mat-label><mat-select [(ngModel)]="quota.companyId"><mat-option value="">All</mat-option>@for (c of lookups.companiesOf(quota.tenantId); track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }</mat-select></mat-form-field>
                <mat-form-field><mat-label>Application</mat-label><mat-select [(ngModel)]="quota.applicationId"><mat-option value="">All</mat-option>@for (a of lookups.appsOf(quota.tenantId); track a.id) { <mat-option [value]="a.id">{{ a.name }}</mat-option> }</mat-select></mat-form-field>
                <mat-form-field><mat-label>Channel</mat-label><mat-select [(ngModel)]="quota.channel"><mat-option value="">All</mat-option>@for (c of channels; track c) { <mat-option [value]="c">{{ c | channel }}</mat-option> }</mat-select></mat-form-field>
                <mat-form-field><mat-label>Monthly limit</mat-label><input matInput type="number" min="0" [(ngModel)]="quota.monthlyLimit"></mat-form-field>
                <mat-form-field><mat-label>Alert threshold %</mat-label><input matInput type="number" min="0" max="100" [(ngModel)]="quota.alertThresholdPercent"></mat-form-field>
              </div>
              <button mat-stroked-button (click)="saveQuota()" [disabled]="!quota.tenantId"><mat-icon>save</mat-icon>Save quota</button>
            }
          </section>
        </mat-tab>

        <mat-tab label="Credits">
          <section class="panel">
            @for (a of credits(); track a.id) {
              <div class="panel" style="margin-bottom:10px">
                <div class="row wrap-row"><b>{{ lookups.tenantName(a.tenantId) }}@if (a.companyId) { / {{ lookups.companyName(a.companyId) }} }</b><span class="spacer"></span>
                  @if (a.lowBalanceThreshold != null && a.available < a.lowBalanceThreshold) { <app-chip tone="bad" icon="warning" label="Low balance" /> }
                  <span>Balance <b>{{ a.balance | money:a.currency }}</b> · reserved {{ a.reserved | money:a.currency }} · available <b>{{ a.available | money:a.currency }}</b></span></div>
                <details><summary class="small">Recent transactions ({{ a.transactions.length }})</summary>
                  <table class="data small"><thead><tr><th>Time</th><th>Kind</th><th class="num">Amount</th><th>Note</th></tr></thead>
                    <tbody>@for (t of a.transactions; track t.id) { <tr><td class="nowrap">{{ t.createdAt | dt }}</td><td>{{ t.kind }}</td><td class="num">{{ t.amount | money:a.currency }}</td><td>{{ t.note ?? '' }}</td></tr> }</tbody></table>
                </details>
              </div>
            } @empty { <app-empty icon="account_balance_wallet" text="No prepaid credit accounts" hint="Without a credit account, sending is not limited by balance." /> }
            @if (auth.isPlatformAdmin()) {
              <h3 style="margin:12px 0 4px">Top up</h3>
              <div class="form-grid">
                <mat-form-field><mat-label>Tenant</mat-label><mat-select [(ngModel)]="topUp.tenantId">@for (t of lookups.tenants(); track t.id) { <mat-option [value]="t.id">{{ t.name }}</mat-option> }</mat-select></mat-form-field>
                <mat-form-field><mat-label>Company</mat-label><mat-select [(ngModel)]="topUp.companyId"><mat-option value="">Tenant-wide</mat-option>@for (c of lookups.companiesOf(topUp.tenantId); track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }</mat-select></mat-form-field>
                <mat-form-field><mat-label>Amount</mat-label><input matInput type="number" min="0" [(ngModel)]="topUp.amount"></mat-form-field>
                <mat-form-field><mat-label>Low-balance threshold</mat-label><input matInput type="number" min="0" [(ngModel)]="topUp.lowBalanceThreshold"></mat-form-field>
                <mat-form-field class="span-2"><mat-label>Note</mat-label><input matInput [(ngModel)]="topUp.note"></mat-form-field>
              </div>
              <button mat-stroked-button (click)="doTopUp()" [disabled]="!topUp.tenantId || !(topUp.amount > 0)"><mat-icon>add_card</mat-icon>Add credits</button>
            }
          </section>
        </mat-tab>

        <mat-tab label="Rates">
          <div class="grid-2">
            <section class="panel">
              <h2>Customer billing rates</h2>
              <div class="table-wrap"><table class="data">
                <thead><tr><th>Tenant</th><th>Channel</th><th class="num">Unit price</th><th class="num">Margin %</th></tr></thead>
                <tbody>@for (r of billing(); track r.id) { <tr><td>{{ r.tenantId ? lookups.tenantName(r.tenantId) : 'Default' }}</td><td><app-channel [value]="r.channel" /></td>
                  <td class="num">{{ r.unitPrice != null ? (r.unitPrice | money:r.currency) : 'cost + margin' }}</td><td class="num">{{ r.marginPercent }}</td></tr> }
                  @empty { <tr><td colspan="4" class="muted">No billing rates</td></tr> }</tbody>
              </table></div>
              @if (auth.isPlatformAdmin()) {
                <div class="form-grid" style="margin-top:8px">
                  <mat-form-field><mat-label>Tenant</mat-label><mat-select [(ngModel)]="bill.tenantId"><mat-option value="">Default (all tenants)</mat-option>@for (t of lookups.tenants(); track t.id) { <mat-option [value]="t.id">{{ t.name }}</mat-option> }</mat-select></mat-form-field>
                  <mat-form-field><mat-label>Channel</mat-label><mat-select [(ngModel)]="bill.channel">@for (c of channels; track c) { <mat-option [value]="c">{{ c | channel }}</mat-option> }</mat-select></mat-form-field>
                  <mat-form-field><mat-label>Unit price (optional)</mat-label><input matInput type="number" step="0.0001" [(ngModel)]="bill.unitPrice"></mat-form-field>
                  <mat-form-field><mat-label>Margin %</mat-label><input matInput type="number" [(ngModel)]="bill.marginPercent"></mat-form-field>
                </div>
                <button mat-stroked-button (click)="saveBilling()"><mat-icon>save</mat-icon>Save billing rate</button>
              }
            </section>
            <section class="panel">
              <h2>Provider cost rates</h2>
              <div class="table-wrap"><table class="data">
                <thead><tr><th>Channel</th><th>Provider</th><th>Country</th><th>Class</th><th class="num">Unit cost</th><th>Effective</th></tr></thead>
                <tbody>@for (r of costs(); track r.id) { <tr><td><app-channel [value]="r.channel" /></td><td>{{ r.providerAccountId ? lookups.providerName(r.providerAccountId) : 'any' }}</td>
                  <td>{{ r.countryPrefix ?? 'any' }}</td><td>{{ r.messageClass ?? 'any' }}</td><td class="num">{{ r.unitCost | money:r.currency }}</td><td class="small nowrap">{{ r.effectiveFrom | dt }}</td></tr> }
                  @empty { <tr><td colspan="6" class="muted">No cost rates</td></tr> }</tbody>
              </table></div>
              @if (auth.isPlatformAdmin()) {
                <div class="form-grid" style="margin-top:8px">
                  <mat-form-field><mat-label>Channel</mat-label><mat-select [(ngModel)]="cost.channel">@for (c of channels; track c) { <mat-option [value]="c">{{ c | channel }}</mat-option> }</mat-select></mat-form-field>
                  <mat-form-field><mat-label>Provider</mat-label><mat-select [(ngModel)]="cost.providerAccountId"><mat-option value="">Any</mat-option>@for (p of providersFor(cost.channel); track p.id) { <mat-option [value]="p.id">{{ p.name }}</mat-option> }</mat-select></mat-form-field>
                  <mat-form-field><mat-label>Country prefix</mat-label><input matInput [(ngModel)]="cost.countryPrefix" placeholder="+91"></mat-form-field>
                  <mat-form-field><mat-label>Message class</mat-label><mat-select [(ngModel)]="cost.messageClass"><mat-option value="">Any</mat-option>@for (c of classes; track c) { <mat-option [value]="c">{{ c }}</mat-option> }</mat-select></mat-form-field>
                  <mat-form-field><mat-label>Unit cost</mat-label><input matInput type="number" step="0.0001" [(ngModel)]="cost.unitCost"></mat-form-field>
                </div>
                <button mat-stroked-button (click)="addCost()" [disabled]="cost.unitCost == null"><mat-icon>add</mat-icon>Add cost rate</button>
              }
            </section>
          </div>
        </mat-tab>
      </mat-tab-group>
    </div>`,
  styles: [`.quota { padding: 8px 0; border-bottom: 1px solid var(--border); } .quota:last-of-type { border-bottom: 0; }`],
})
export class Usage implements OnInit {
  private api = inject(ApiService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  auth = inject(AuthService);
  lookups = inject(LookupsService);
  channels = CHANNELS; classes = MESSAGE_CLASSES;
  groupings = ['channel', 'application', 'company', 'provider', 'month'];
  groupBy = 'channel';
  filters: FilterModel = emptyFilters();
  rows = signal<UsageRow[]>([]);
  quotas = signal<Quota[]>([]);
  credits = signal<CreditAccount[]>([]);
  billing = signal<BillingRate[]>([]);
  costs = signal<CostRate[]>([]);
  loading = signal(false);
  error = signal<unknown>(null);
  quota = { tenantId: '', companyId: '', applicationId: '', channel: '', monthlyLimit: 100000, alertThresholdPercent: 80 };
  topUp = { tenantId: '', companyId: '', amount: 1000, lowBalanceThreshold: null as number | null, note: '' };
  bill = { tenantId: '', channel: 'sms', unitPrice: null as number | null, marginPercent: 20 };
  cost = { channel: 'sms', providerAccountId: '', countryPrefix: '', messageClass: '', unitCost: null as number | null };

  canQuota = computed(() => this.auth.can('ManageQuotas'));
  totals = computed(() => this.rows().reduce((t, r) => ({
    messages: t.messages + r.messages, units: t.units + r.units, estimatedCost: t.estimatedCost + r.estimatedCost, billedAmount: t.billedAmount + r.billedAmount, margin: t.margin + r.margin,
    providerReportedCost: r.providerReportedCost != null ? (t.providerReportedCost ?? 0) + r.providerReportedCost : t.providerReportedCost,
  }), { messages: 0, units: 0, estimatedCost: 0, billedAmount: 0, margin: 0, providerReportedCost: null as number | null }));
  bars = computed<BarItem[]>(() => this.rows().slice().sort((a, b) => b.messages - a.messages).map(r => ({ key: r.key, label: this.label(r.key), value: r.messages })));

  ngOnInit() {
    const t = this.lookups.tenants()[0]?.id ?? '';
    this.quota.tenantId = t; this.topUp.tenantId = t;
    this.loadAll();
  }

  loadAll() {
    this.loadUsage();
    this.api.quotas().subscribe({ next: q => this.quotas.set(q), error: e => this.error.set(e) });
    this.api.credits().subscribe({ next: c => this.credits.set(c), error: e => this.error.set(e) });
    this.api.billingRates().subscribe({ next: b => this.billing.set(b), error: e => this.error.set(e) });
    this.api.costRates().subscribe({ next: c => this.costs.set(c), error: e => this.error.set(e) });
  }

  loadUsage() {
    this.loading.set(true);
    this.api.usage(this.groupBy, filterQuery(this.filters)).subscribe({
      next: r => { this.rows.set(r); this.loading.set(false); }, error: e => { this.error.set(e); this.loading.set(false); },
    });
  }

  label(key: string) {
    if (this.groupBy === 'channel') return channelLabel(key);
    if (this.groupBy === 'month' && /^\d{6}$/.test(key)) return `${key.slice(0, 4)}-${key.slice(4)}`;
    return key;
  }
  pct(q: Quota) { return q.monthlyLimit ? Math.round((100 * q.used) / q.monthlyLimit) : 0; }
  quotaLabel(q: Quota) {
    return [this.lookups.tenantName(q.tenantId), q.companyId && this.lookups.companyName(q.companyId), q.applicationId && this.lookups.appName(q.applicationId), q.channel ? channelLabel(q.channel) : 'all channels']
      .filter(Boolean).join(' / ');
  }
  providersFor(ch: string) { return this.lookups.providers().filter(p => p.channel === ch); }

  saveQuota() {
    const q = this.quota;
    this.api.saveQuota({ tenantId: q.tenantId, companyId: q.companyId || null, applicationId: q.applicationId || null, channel: q.channel || null, monthlyLimit: Number(q.monthlyLimit), alertThresholdPercent: Number(q.alertThresholdPercent) })
      .subscribe({ next: () => { this.snack.open('Quota saved', 'OK', { duration: 2500 }); this.api.quotas().subscribe(x => this.quotas.set(x)); }, error: e => this.error.set(e) });
  }
  deleteQuota(q: Quota) {
    confirmDialog(this.dialog, { title: 'Delete quota', message: `Remove the quota for ${this.quotaLabel(q)}?`, confirmText: 'Delete', danger: true }).subscribe(r => {
      if (r) this.api.deleteQuota(q.id).subscribe({ next: () => this.api.quotas().subscribe(x => this.quotas.set(x)), error: e => this.error.set(e) });
    });
  }
  doTopUp() {
    const t = this.topUp;
    this.api.topUp({ tenantId: t.tenantId, companyId: t.companyId || null, amount: Number(t.amount), lowBalanceThreshold: t.lowBalanceThreshold, note: t.note || null })
      .subscribe({ next: r => { this.snack.open(`Balance is now ${r.balance}`, 'OK', { duration: 3000 }); this.api.credits().subscribe(x => this.credits.set(x)); }, error: e => this.error.set(e) });
  }
  saveBilling() {
    const b = this.bill;
    this.api.saveBillingRate({ tenantId: b.tenantId || null, channel: b.channel, unitPrice: b.unitPrice, marginPercent: Number(b.marginPercent), currency: 'INR' })
      .subscribe({ next: () => this.api.billingRates().subscribe(x => this.billing.set(x)), error: e => this.error.set(e) });
  }
  addCost() {
    const c = this.cost;
    this.api.addCostRate({ providerAccountId: c.providerAccountId || null, channel: c.channel, countryPrefix: c.countryPrefix || null, messageClass: c.messageClass || null, unitCost: Number(c.unitCost), currency: 'INR' })
      .subscribe({ next: () => this.api.costRates().subscribe(x => this.costs.set(x)), error: e => this.error.set(e) });
  }
}
