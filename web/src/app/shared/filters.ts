import { Component, computed, inject, input, model, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { ReportFilter } from '../core/api.service';
import { LookupsService } from '../core/lookups.service';
import { CHANNELS } from '../core/models';
import { channelLabel } from './ui';

export interface FilterModel { tenantId: string; companyId: string; applicationId: string; channel: string; providerAccountId: string; from: string; to: string; }
export type FilterKey = 'tenant' | 'company' | 'application' | 'channel' | 'provider' | 'from' | 'to';

export function emptyFilters(): FilterModel { return { tenantId: '', companyId: '', applicationId: '', channel: '', providerAccountId: '', from: '', to: '' }; }

/** yyyy-mm-dd (local) → ISO instant at local midnight; `to` is exclusive so it becomes the following midnight. */
export function filterQuery(f: FilterModel): ReportFilter {
  const day = (s: string, add = 0) => { const d = new Date(s + 'T00:00:00'); if (isNaN(d.getTime())) return undefined; d.setDate(d.getDate() + add); return d.toISOString(); };
  return {
    tenantId: f.tenantId || undefined, companyId: f.companyId || undefined, applicationId: f.applicationId || undefined, channel: f.channel || undefined,
    providerAccountId: f.providerAccountId || undefined, from: f.from ? day(f.from) : undefined, to: f.to ? day(f.to, 1) : undefined,
  };
}

/** Filter row shared by the dashboard, explorer, usage and reports. */
@Component({
  selector: 'app-scope-filters',
  imports: [FormsModule, MatFormFieldModule, MatSelectModule, MatInputModule, MatButtonModule, MatIconModule],
  template: `
    @if (show().includes('tenant') && lookups.tenants().length > 1) {
      <mat-form-field>
        <mat-label>Tenant</mat-label>
        <mat-select [ngModel]="value().tenantId" (ngModelChange)="set({ tenantId: $event, companyId: '', applicationId: '' })">
          <mat-option value="">All tenants</mat-option>
          @for (t of lookups.tenants(); track t.id) { <mat-option [value]="t.id">{{ t.name }}</mat-option> }
        </mat-select>
      </mat-form-field>
    }
    @if (show().includes('company')) {
      <mat-form-field>
        <mat-label>Company</mat-label>
        <mat-select [ngModel]="value().companyId" (ngModelChange)="set({ companyId: $event })">
          <mat-option value="">All companies</mat-option>
          @for (c of companies(); track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }
        </mat-select>
      </mat-form-field>
    }
    @if (show().includes('application')) {
      <mat-form-field>
        <mat-label>Application</mat-label>
        <mat-select [ngModel]="value().applicationId" (ngModelChange)="set({ applicationId: $event })">
          <mat-option value="">All applications</mat-option>
          @for (a of apps(); track a.id) { <mat-option [value]="a.id">{{ a.name }}</mat-option> }
        </mat-select>
      </mat-form-field>
    }
    @if (show().includes('channel')) {
      <mat-form-field>
        <mat-label>Channel</mat-label>
        <mat-select [ngModel]="value().channel" (ngModelChange)="set({ channel: $event })">
          <mat-option value="">All channels</mat-option>
          @for (c of channels; track c) { <mat-option [value]="c">{{ label(c) }}</mat-option> }
        </mat-select>
      </mat-form-field>
    }
    @if (show().includes('provider')) {
      <mat-form-field>
        <mat-label>Provider</mat-label>
        <mat-select [ngModel]="value().providerAccountId" (ngModelChange)="set({ providerAccountId: $event })">
          <mat-option value="">All providers</mat-option>
          @for (p of providers(); track p.id) { <mat-option [value]="p.id">{{ p.name }}</mat-option> }
        </mat-select>
      </mat-form-field>
    }
    @if (show().includes('from')) {
      <mat-form-field style="width:150px"><mat-label>From</mat-label><input matInput type="date" [ngModel]="value().from" (ngModelChange)="set({ from: $event ?? '' })"></mat-form-field>
    }
    @if (show().includes('to')) {
      <mat-form-field style="width:150px"><mat-label>To</mat-label><input matInput type="date" [ngModel]="value().to" (ngModelChange)="set({ to: $event ?? '' })"></mat-form-field>
    }
    @if (active()) { <button mat-button (click)="clear()"><mat-icon>filter_alt_off</mat-icon>Clear</button> }`,
  host: { class: 'filters' },
})
export class ScopeFilters {
  lookups = inject(LookupsService);
  value = model.required<FilterModel>();
  show = input<FilterKey[]>(['tenant', 'company', 'application', 'channel', 'provider', 'from', 'to']);
  changed = output<FilterModel>();
  channels = CHANNELS;
  label = channelLabel;
  companies = computed(() => this.lookups.companiesOf(this.value().tenantId || null));
  apps = computed(() => this.value().companyId ? this.lookups.appsForCompany(this.value().companyId) : this.lookups.appsOf(this.value().tenantId || null));
  providers = computed(() => this.lookups.providers().filter(p => !this.value().channel || p.channel === this.value().channel));
  active = computed(() => Object.values(this.value()).some(v => !!v));

  set(patch: Partial<FilterModel>) {
    const next = { ...this.value(), ...patch };
    this.value.set(next);
    this.changed.emit(next);
  }
  clear() { const e = emptyFilters(); this.value.set(e); this.changed.emit(e); }
}
