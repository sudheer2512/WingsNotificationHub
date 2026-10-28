import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { LookupsService } from '../core/lookups.service';
import { CHANNELS, Company, MESSAGE_CLASSES, Tenant } from '../core/models';
import { UI } from '../shared/ui';

/** Branding keys understood by the hub's email layout (stored as a free-form JSON object). */
const BRANDING_KEYS = ['displayName', 'logoUrl', 'primaryColor', 'supportEmail', 'supportPhone', 'website', 'emailFooterHtml'];

@Component({
  selector: 'app-tenant-form',
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, ...UI],
  template: `
    <h2 mat-dialog-title>{{ data ? 'Edit tenant ' + data.code : 'New tenant' }}</h2>
    <mat-dialog-content>
      <div class="form-grid">
        <mat-form-field><mat-label>Code</mat-label><input matInput [(ngModel)]="m.code" [readonly]="!!data" required></mat-form-field>
        <mat-form-field><mat-label>Name</mat-label><input matInput [(ngModel)]="m.name" required></mat-form-field>
        <mat-form-field><mat-label>Default time zone</mat-label><input matInput [(ngModel)]="m.defaultTimeZone"></mat-form-field>
        <mat-form-field><mat-label>Message body retention (days)</mat-label><input matInput type="number" min="1" [(ngModel)]="m.bodyRetentionDays"></mat-form-field>
        <mat-form-field><mat-label>Data retention (days)</mat-label><input matInput type="number" min="30" [(ngModel)]="m.dataRetentionDays"></mat-form-field>
      </div>
      <mat-checkbox [(ngModel)]="m.allowSharedProviders">Allow shared (platform) providers</mat-checkbox>
      <mat-checkbox [(ngModel)]="m.requireMarketingConsent">Require marketing consent for promotional messages</mat-checkbox>
      <h3 style="margin-top:10px">Branding</h3>
      <div class="form-grid">@for (k of brandingKeys; track k) { <mat-form-field [class.span-2]="k === 'emailFooterHtml'"><mat-label>{{ k }}</mat-label><input matInput [(ngModel)]="branding[k]"></mat-form-field> }</div>
      <app-error [error]="error()" />
    </mat-dialog-content>
    <mat-dialog-actions align="end"><button mat-button mat-dialog-close>Cancel</button><button mat-flat-button (click)="save()" [disabled]="busy() || !m.code || !m.name">Save</button></mat-dialog-actions>`,
})
export class TenantForm {
  private api = inject(ApiService);
  private ref = inject(MatDialogRef<TenantForm>);
  data = inject<Tenant | null>(MAT_DIALOG_DATA, { optional: true });
  brandingKeys = BRANDING_KEYS;
  busy = signal(false); error = signal<unknown>(null);
  m = { code: this.data?.code ?? '', name: this.data?.name ?? '', defaultTimeZone: this.data?.defaultTimeZone ?? 'Asia/Kolkata', bodyRetentionDays: this.data?.bodyRetentionDays ?? 90,
    dataRetentionDays: this.data?.dataRetentionDays ?? 730, allowSharedProviders: this.data?.allowSharedProviders ?? true, requireMarketingConsent: this.data?.requireMarketingConsent ?? true };
  branding: Record<string, string> = Object.fromEntries(Object.entries(this.data?.branding ?? {}).map(([k, v]) => [k, String(v ?? '')]));
  save() {
    const branding = Object.fromEntries(Object.entries(this.branding).filter(([, v]) => !!v));
    const body = { ...this.m, branding: Object.keys(branding).length ? branding : null };
    this.busy.set(true); this.error.set(null);
    (this.data ? this.api.updateTenant(this.data.id, body) : this.api.createTenant(body)).subscribe({ next: r => this.ref.close(r.id), error: e => { this.busy.set(false); this.error.set(e); } });
  }
}

@Component({
  selector: 'app-company-form',
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSelectModule, ...UI],
  template: `
    <h2 mat-dialog-title>{{ data.company ? 'Edit ' + data.company.name : 'New company' }}</h2>
    <mat-dialog-content>
      <div class="form-grid">
        <mat-form-field><mat-label>Tenant</mat-label><mat-select [(ngModel)]="m.tenantId" [disabled]="!!data.company">@for (t of lookups.tenants(); track t.id) { <mat-option [value]="t.id">{{ t.name }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Code</mat-label><input matInput [(ngModel)]="m.code" [readonly]="!!data.company" required></mat-form-field>
        <mat-form-field><mat-label>Name</mat-label><input matInput [(ngModel)]="m.name" required></mat-form-field>
        <mat-form-field><mat-label>Country</mat-label><input matInput [(ngModel)]="m.country" placeholder="IN"></mat-form-field>
        <mat-form-field><mat-label>Time zone (IANA)</mat-label><input matInput [(ngModel)]="m.timeZone" placeholder="Asia/Kolkata"></mat-form-field>
      </div>
      <h3 style="margin-top:8px">Quiet hours (recipient local time)</h3>
      <mat-checkbox [(ngModel)]="quiet.enabled">Enabled</mat-checkbox>
      @if (quiet.enabled) {
        <div class="form-grid">
          <mat-form-field><mat-label>Start</mat-label><input matInput type="time" [(ngModel)]="quiet.start"></mat-form-field>
          <mat-form-field><mat-label>End</mat-label><input matInput type="time" [(ngModel)]="quiet.end"></mat-form-field>
          <mat-form-field><mat-label>Applies to</mat-label><mat-select [(ngModel)]="quiet.appliesTo" multiple>@for (c of classes; track c) { <mat-option [value]="c">{{ c }}</mat-option> }</mat-select></mat-form-field>
          <mat-form-field><mat-label>Channels (empty = all except in-app)</mat-label><mat-select [(ngModel)]="quiet.channels" multiple>@for (c of channels; track c) { <mat-option [value]="c">{{ c | channel }}</mat-option> }</mat-select></mat-form-field>
        </div>
      }
      <h3 style="margin-top:8px">Branding</h3>
      <div class="form-grid">@for (k of brandingKeys; track k) { <mat-form-field [class.span-2]="k === 'emailFooterHtml'"><mat-label>{{ k }}</mat-label><input matInput [(ngModel)]="branding[k]"></mat-form-field> }</div>
      <app-error [error]="error()" />
    </mat-dialog-content>
    <mat-dialog-actions align="end"><button mat-button mat-dialog-close>Cancel</button><button mat-flat-button (click)="save()" [disabled]="busy() || !m.tenantId || !m.code || !m.name">Save</button></mat-dialog-actions>`,
})
export class CompanyForm {
  private api = inject(ApiService);
  private ref = inject(MatDialogRef<CompanyForm>);
  lookups = inject(LookupsService);
  data = inject<{ company?: Company; tenantId?: string }>(MAT_DIALOG_DATA);
  channels = CHANNELS; classes = MESSAGE_CLASSES; brandingKeys = BRANDING_KEYS;
  busy = signal(false); error = signal<unknown>(null);
  c = this.data.company;
  m = { tenantId: this.c?.tenantId ?? this.data.tenantId ?? this.lookups.tenants()[0]?.id ?? '', code: this.c?.code ?? '', name: this.c?.name ?? '', country: this.c?.country ?? '', timeZone: this.c?.timeZone ?? '' };
  quiet = { enabled: !!this.c?.quietHours?.enabled, start: this.c?.quietHours?.start ?? '21:00', end: this.c?.quietHours?.end ?? '08:00',
    appliesTo: this.c?.quietHours?.appliesTo ?? ['Promotional'], channels: this.c?.quietHours?.channels ?? [] };
  branding: Record<string, string> = Object.fromEntries(Object.entries(this.c?.branding ?? {}).map(([k, v]) => [k, String(v ?? '')]));
  save() {
    const branding = Object.fromEntries(Object.entries(this.branding).filter(([, v]) => !!v));
    const body = { tenantId: this.m.tenantId, code: this.m.code, name: this.m.name, country: this.m.country || null, timeZone: this.m.timeZone || null,
      branding: Object.keys(branding).length ? branding : null, quietHours: this.quiet.enabled ? { ...this.quiet, enabled: true } : null };
    this.busy.set(true); this.error.set(null);
    (this.c ? this.api.updateCompany(this.c.id, body) : this.api.createCompany(body)).subscribe({ next: r => this.ref.close(r.id), error: e => { this.busy.set(false); this.error.set(e); } });
  }
}

@Component({
  selector: 'app-tenancy',
  imports: [FormsModule, MatButtonModule, MatIconModule, MatFormFieldModule, MatSelectModule, ...UI],
  template: `
    <div class="page">
      <div class="page-head"><h1>Tenants & companies</h1><span class="spacer"></span>
        @if (auth.isPlatformAdmin()) { <button mat-stroked-button (click)="editTenant()"><mat-icon>add</mat-icon>Tenant</button> }
        @if (auth.can('ManageTenancy')) { <button mat-flat-button (click)="editCompany()"><mat-icon>add</mat-icon>Company</button> }
      </div>
      <app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />
      <section class="panel">
        <h2>Tenants</h2>
        <div class="table-wrap"><table class="data">
          <thead><tr><th>Code</th><th>Name</th><th>Status</th><th>Time zone</th><th>Shared providers</th><th>Marketing consent</th><th>Retention (body / data)</th><th></th></tr></thead>
          <tbody>@for (t of tenants(); track t.id) {
            <tr><td><code>{{ t.code }}</code></td><td>{{ t.name }}</td><td><app-chip [value]="t.status" [tip]="t.suspendedReason ?? null" /></td><td>{{ t.defaultTimeZone }}</td>
              <td>{{ t.allowSharedProviders ? 'allowed' : 'blocked' }}</td><td>{{ t.requireMarketingConsent ? 'required' : 'not required' }}</td><td>{{ t.bodyRetentionDays }} / {{ t.dataRetentionDays }} days</td>
              <td>@if (auth.can('ManageTenancy')) { <button mat-icon-button (click)="editTenant(t)" aria-label="Edit tenant"><mat-icon>edit</mat-icon></button> }</td></tr>
          }</tbody>
        </table></div>
      </section>
      <section class="panel">
        <div class="panel-head"><h2>Companies</h2><span class="spacer"></span>
          <mat-form-field style="width:220px"><mat-label>Tenant</mat-label><mat-select [(ngModel)]="tenantFilter"><mat-option value="">All tenants</mat-option>
            @for (t of tenants(); track t.id) { <mat-option [value]="t.id">{{ t.name }}</mat-option> }</mat-select></mat-form-field></div>
        <div class="table-wrap"><table class="data">
          <thead><tr><th>Code</th><th>Name</th><th>Tenant</th><th>Status</th><th>Country / TZ</th><th>Quiet hours</th><th>Branding</th><th></th></tr></thead>
          <tbody>@for (c of companies(); track c.id) {
            <tr><td><code>{{ c.code }}</code></td><td>{{ c.name }}</td><td class="small">{{ lookups.tenantName(c.tenantId) }}</td><td><app-chip [value]="c.status" [tip]="c.suspendedReason ?? null" /></td>
              <td>{{ c.country ?? '—' }} · {{ c.timeZone ?? 'tenant default' }}</td>
              <td class="small">@if (c.quietHours?.enabled) { {{ c.quietHours!.start }}–{{ c.quietHours!.end }} · {{ c.quietHours!.appliesTo.join(', ') }} } @else { off }</td>
              <td class="small">@for (b of brandingOf(c); track b) { <span class="tag">{{ b }}</span> } @empty { — }</td>
              <td>@if (auth.can('ManageTenancy')) { <button mat-icon-button (click)="editCompany(c)" aria-label="Edit company"><mat-icon>edit</mat-icon></button> }</td></tr>
          } @empty { <tr><td colspan="8" class="muted">No companies</td></tr> }</tbody>
        </table></div>
      </section>
    </div>`,
  styles: [`.panel + .panel { margin-top: 12px; }`],
})
export class Tenancy implements OnInit {
  private api = inject(ApiService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  auth = inject(AuthService);
  lookups = inject(LookupsService);
  tenantFilter = '';
  allTenants = signal<Tenant[]>([]);
  allCompanies = signal<Company[]>([]);
  error = signal<unknown>(null);
  tenants = computed(() => this.allTenants());
  companies = () => this.allCompanies().filter(c => !this.tenantFilter || c.tenantId === this.tenantFilter);

  ngOnInit() { this.load(); }
  load() {
    this.api.tenants().subscribe({ next: t => this.allTenants.set(t), error: e => this.error.set(e) });
    this.api.companies().subscribe({ next: c => this.allCompanies.set(c), error: e => this.error.set(e) });
  }
  brandingOf(c: Company) { return Object.keys(c.branding ?? {}); }
  private done(msg: string) { this.snack.open(msg, 'OK', { duration: 2500 }); this.load(); this.lookups.refresh(); }
  editTenant(t?: Tenant) { this.dialog.open(TenantForm, { data: t ?? null, width: '720px', maxWidth: '96vw' }).afterClosed().subscribe(id => id && this.done('Tenant saved')); }
  editCompany(c?: Company) {
    this.dialog.open(CompanyForm, { data: { company: c, tenantId: this.tenantFilter || undefined }, width: '760px', maxWidth: '96vw' }).afterClosed().subscribe(id => id && this.done('Company saved'));
  }
}
