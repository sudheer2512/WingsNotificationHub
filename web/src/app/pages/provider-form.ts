import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { LookupsService } from '../core/lookups.service';
import { Channel, DuplicateRiskPolicy, ProviderAccount, ProviderScope, ProviderType } from '../core/models';
import { UI } from '../shared/ui';

interface FormModel {
  name: string; providerType: string; channel: Channel | ''; scope: ProviderScope; tenantId: string; companyId: string;
  settings: Record<string, string>; secrets: Record<string, string>; extraSettings: string;
  priority: number; rateLimitPerSecond: number | null; monthlyCapacity: number | null; allowedCountryPrefixes: string; unitCost: number | null; currency: string;
  failoverProviderAccountId: string; duplicateRiskPolicy: DuplicateRiskPolicy; timeoutSeconds: number; enabled: boolean;
}

/** Add / edit provider account. The form is generated from the provider type's settingKeys and secretKeys; secrets are write-only. */
@Component({
  selector: 'app-provider-form',
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, ...UI],
  template: `
    <h2 mat-dialog-title>{{ data.account ? 'Edit ' + data.account.name : 'Add provider account' }}</h2>
    <mat-dialog-content>
      <h3>Account</h3>
      <div class="form-grid">
        <mat-form-field><mat-label>Name</mat-label><input matInput [(ngModel)]="m.name" required></mat-form-field>
        <mat-form-field><mat-label>Provider type</mat-label>
          <mat-select [(ngModel)]="m.providerType" (selectionChange)="typeChanged()" [disabled]="!!data.account">
            @for (t of data.types; track t.providerType) { <mat-option [value]="t.providerType">{{ t.displayName }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Channel</mat-label>
          <mat-select [(ngModel)]="m.channel" [disabled]="!!data.account">@for (c of type()?.channels ?? []; track c) { <mat-option [value]="c">{{ c | channel }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Scope</mat-label>
          <mat-select [(ngModel)]="m.scope">
            @if (auth.isPlatformAdmin()) { <mat-option value="Shared">Shared (all tenants)</mat-option> }
            <mat-option value="Tenant">Tenant</mat-option><mat-option value="Company">Company</mat-option></mat-select></mat-form-field>
        @if (m.scope !== 'Shared') {
          <mat-form-field><mat-label>Tenant</mat-label>
            <mat-select [(ngModel)]="m.tenantId" (selectionChange)="m.companyId = ''">@for (t of lookups.tenants(); track t.id) { <mat-option [value]="t.id">{{ t.name }}</mat-option> }</mat-select></mat-form-field>
        }
        @if (m.scope === 'Company') {
          <mat-form-field><mat-label>Company</mat-label>
            <mat-select [(ngModel)]="m.companyId">@for (c of lookups.companiesOf(m.tenantId); track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }</mat-select></mat-form-field>
        }
      </div>

      @if (type(); as t) {
        <h3>Settings</h3>
        @if (t.settingKeys.length) {
          <div class="form-grid">
            @for (k of t.settingKeys; track k) {
              <mat-form-field><mat-label>{{ k }}</mat-label>
                @if (k === 'bodyTemplate' || k.endsWith('Json')) { <textarea matInput rows="3" class="code" [(ngModel)]="m.settings[k]"></textarea> }
                @else { <input matInput [(ngModel)]="m.settings[k]"> }
              </mat-form-field>
            }
          </div>
        } @else { <div class="small muted">This provider type has no settings.</div> }
        <h3>Credentials</h3>
        @if (t.secretKeys.length) {
          <div class="callout small">Secrets are encrypted at rest and never shown again. Leave a field empty to keep the stored value.
            @if (data.account) { Changing credentials of an existing account requires approval by another administrator. }</div>
          <div class="form-grid">
            @for (k of t.secretKeys; track k) {
              <mat-form-field><mat-label>{{ k }}</mat-label>
                @if (k.endsWith('Json') || k.endsWith('P8')) { <textarea matInput rows="3" class="code" [(ngModel)]="m.secrets[k]" autocomplete="off" [placeholder]="stored(k) ? '•••••••• (stored)' : ''"></textarea> }
                @else { <input matInput type="password" [(ngModel)]="m.secrets[k]" autocomplete="new-password" [placeholder]="stored(k) ? '•••••••• (stored)' : ''"> }
                @if (stored(k)) { <mat-hint>Stored · enter a value to replace it</mat-hint> } @else { <mat-hint>Not set</mat-hint> }
              </mat-form-field>
            }
          </div>
        } @else { <div class="small muted">No credentials needed.</div> }
      }

      <h3>Routing & limits</h3>
      <div class="form-grid">
        <mat-form-field><mat-label>Priority (lower is preferred)</mat-label><input matInput type="number" [(ngModel)]="m.priority"></mat-form-field>
        <mat-form-field><mat-label>Fallback provider</mat-label>
          <mat-select [(ngModel)]="m.failoverProviderAccountId"><mat-option value="">None</mat-option>
            @for (p of fallbacks(); track p.id) { <mat-option [value]="p.id">{{ p.name }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Duplicate-risk policy</mat-label>
          <mat-select [(ngModel)]="m.duplicateRiskPolicy">
            <mat-option value="Never">Never resend after unknown outcome</mat-option><mat-option value="SameProviderOnly">Resend via same provider only</mat-option>
            <mat-option value="AllowDuplicates">Duplicates acceptable</mat-option></mat-select></mat-form-field>
        <mat-form-field><mat-label>Timeout (s)</mat-label><input matInput type="number" min="1" max="300" [(ngModel)]="m.timeoutSeconds"></mat-form-field>
        <mat-form-field><mat-label>Rate limit (msg/s)</mat-label><input matInput type="number" [(ngModel)]="m.rateLimitPerSecond"></mat-form-field>
        <mat-form-field><mat-label>Monthly capacity</mat-label><input matInput type="number" [(ngModel)]="m.monthlyCapacity"></mat-form-field>
        <mat-form-field><mat-label>Allowed country prefixes</mat-label><input matInput [(ngModel)]="m.allowedCountryPrefixes" placeholder="+91, +971"></mat-form-field>
        <mat-form-field><mat-label>Unit cost</mat-label><input matInput type="number" step="0.0001" [(ngModel)]="m.unitCost"></mat-form-field>
        <mat-form-field><mat-label>Currency</mat-label><input matInput [(ngModel)]="m.currency"></mat-form-field>
      </div>
      <mat-checkbox [(ngModel)]="m.enabled">Enabled</mat-checkbox>
      <app-error [error]="error()" />
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button (click)="save()" [disabled]="busy() || !m.name || !m.providerType || !m.channel || (m.scope !== 'Shared' && !m.tenantId) || (m.scope === 'Company' && !m.companyId)">Save</button>
    </mat-dialog-actions>`,
})
export class ProviderForm implements OnInit {
  private api = inject(ApiService);
  private ref = inject(MatDialogRef<ProviderForm>);
  auth = inject(AuthService);
  lookups = inject(LookupsService);
  data = inject<{ account?: ProviderAccount; types: ProviderType[] }>(MAT_DIALOG_DATA);
  busy = signal(false);
  error = signal<unknown>(null);
  typeName = signal('');
  type = computed(() => this.data.types.find(t => t.providerType === this.typeName()) ?? null);
  m!: FormModel;
  private original: Record<string, unknown> = {};

  fallbacks = computed(() => this.lookups.providers().filter(p => p.id !== this.data.account?.id && (!this.m?.channel || p.channel === this.m.channel)));

  ngOnInit() {
    const a = this.data.account;
    this.original = (a?.settings as Record<string, unknown>) ?? {};
    this.m = {
      name: a?.name ?? '', providerType: a?.providerType ?? '', channel: a?.channel ?? '', scope: a?.scope ?? 'Tenant', tenantId: a?.tenantId ?? this.lookups.tenants()[0]?.id ?? '',
      companyId: a?.companyId ?? '', settings: {}, secrets: {}, extraSettings: '', priority: a?.priority ?? 100, rateLimitPerSecond: a?.rateLimitPerSecond ?? null,
      monthlyCapacity: a?.monthlyCapacity ?? null, allowedCountryPrefixes: (a?.allowedCountryPrefixes ?? []).join(', '), unitCost: a?.unitCost ?? null, currency: a?.currency ?? 'INR',
      failoverProviderAccountId: a?.failoverProviderAccountId ?? '', duplicateRiskPolicy: a?.duplicateRiskPolicy ?? 'Never', timeoutSeconds: a?.timeoutSeconds ?? 30, enabled: a?.enabled ?? true,
    };
    for (const [k, v] of Object.entries(this.original)) this.m.settings[k] = v === null || v === undefined ? '' : typeof v === 'object' ? JSON.stringify(v) : String(v);
    this.typeName.set(this.m.providerType);
  }

  typeChanged() {
    this.typeName.set(this.m.providerType);
    const t = this.type();
    if (t && !t.channels.includes(this.m.channel as Channel)) this.m.channel = t.channels[0] ?? '';
    this.m.secrets = {};
  }

  stored(k: string) { return !!this.data.account?.secrets && k in this.data.account.secrets; }

  /** Keeps the original JSON type of each setting (numbers and booleans stay typed). */
  private settingsJson(): Record<string, unknown> {
    const out: Record<string, unknown> = {};
    for (const [k, raw] of Object.entries(this.m.settings)) {
      if (raw === '' || raw === null || raw === undefined) continue;
      const orig = this.original[k];
      if (typeof orig === 'number' && !isNaN(Number(raw))) out[k] = Number(raw);
      else if (typeof orig === 'boolean' || raw === 'true' || raw === 'false') out[k] = raw === 'true';
      else if (/^-?\d+(\.\d+)?$/.test(raw) && orig === undefined) out[k] = Number(raw);
      else if (typeof orig === 'object' && orig !== null) { try { out[k] = JSON.parse(raw); } catch { out[k] = raw; } }
      else out[k] = raw;
    }
    return out;
  }

  save() {
    const secrets = Object.fromEntries(Object.entries(this.m.secrets).filter(([, v]) => !!v));
    const body = {
      name: this.m.name, channel: this.m.channel, providerType: this.m.providerType, scope: this.m.scope,
      tenantId: this.m.scope === 'Shared' ? null : this.m.tenantId || null, companyId: this.m.scope === 'Company' ? this.m.companyId || null : null,
      settings: this.settingsJson(), secrets: Object.keys(secrets).length ? secrets : null, priority: Number(this.m.priority) || 0,
      rateLimitPerSecond: this.m.rateLimitPerSecond || null, monthlyCapacity: this.m.monthlyCapacity || null,
      allowedCountryPrefixes: this.m.allowedCountryPrefixes.split(',').map(s => s.trim()).filter(Boolean), unitCost: this.m.unitCost ?? null, currency: this.m.currency || 'INR',
      failoverProviderAccountId: this.m.failoverProviderAccountId || null, duplicateRiskPolicy: this.m.duplicateRiskPolicy, timeoutSeconds: Number(this.m.timeoutSeconds) || 30, enabled: this.m.enabled,
    };
    this.busy.set(true); this.error.set(null);
    const a = this.data.account;
    (a ? this.api.updateProvider(a.id, body) : this.api.createProvider(body)).subscribe({
      next: r => { this.m.secrets = {}; this.ref.close(r); },
      error: e => { this.busy.set(false); this.error.set(e); },
    });
  }
}
