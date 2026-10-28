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
import { AuthService } from '../core/auth.service';
import { LookupsService } from '../core/lookups.service';
import { AdminUser, CHANNELS, RoleAssignment } from '../core/models';
import { UI } from '../shared/ui';
import { confirmDialog } from '../shared/widgets';

const ROLES = ['PlatformAdministrator', 'TenantAdministrator', 'CompanyAdministrator', 'ApplicationAdministrator', 'TemplateDesigner', 'TemplateApprover',
  'CampaignManager', 'NotificationOperator', 'Auditor'];

@Component({
  selector: 'app-users',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, ...UI],
  template: `
    <div class="page">
      <div class="page-head"><h1>Users & roles</h1><span class="sub">Roles are granted per scope: tenant → company → application (→ channel)</span></div>
      <app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />
      <section class="panel">
        <div class="table-wrap"><table class="data">
          <thead><tr><th>User</th><th>Sign-in</th><th>Last login</th><th>Role grants</th></tr></thead>
          <tbody>@for (u of users(); track u.id) {
            <tr [class.selected]="u.id === sel()?.id">
              <td><b>{{ u.displayName }}</b><div class="small muted">{{ u.email }}</div>@if (!u.enabled) { <app-chip tone="off" label="Disabled" /> }</td>
              <td>{{ u.external ? 'External (OIDC)' : 'Local password' }}</td>
              <td class="small nowrap">{{ u.lastLoginAt ? (u.lastLoginAt | ago) : 'never' }}</td>
              <td>@for (r of u.roles; track r.id) {
                  <span class="tag">{{ r.role | human }} · {{ scope(r) }}<button class="x" (click)="removeRole(u, r)" [attr.aria-label]="'Remove ' + r.role" title="Remove role">×</button></span>
                } @empty { <span class="muted small">no roles</span> }
                <button mat-button (click)="pick(u)"><mat-icon>add</mat-icon>Grant</button></td>
            </tr>
          }</tbody>
        </table></div>
      </section>
      <section class="panel" style="margin-top:12px">
        <h2>{{ sel() ? 'Grant a role to ' + sel()!.displayName : 'Add user' }}</h2>
        <div class="form-grid">
          <mat-form-field><mat-label>Email</mat-label><input matInput type="email" [(ngModel)]="m.email" [readonly]="!!sel()"></mat-form-field>
          <mat-form-field><mat-label>Display name</mat-label><input matInput [(ngModel)]="m.displayName"></mat-form-field>
          @if (!sel()) { <mat-form-field><mat-label>Password (min 12; empty = external OIDC)</mat-label><input matInput type="password" [(ngModel)]="m.password" autocomplete="new-password"></mat-form-field> }
          <mat-form-field><mat-label>Role</mat-label><mat-select [(ngModel)]="m.role">@for (r of roles; track r) { <mat-option [value]="r" [disabled]="r === 'PlatformAdministrator' && !auth.isPlatformAdmin()">{{ r | human }}</mat-option> }</mat-select></mat-form-field>
          <mat-form-field><mat-label>Tenant</mat-label><mat-select [(ngModel)]="m.tenantId">@if (auth.isPlatformAdmin()) { <mat-option value="">All tenants (platform)</mat-option> }
            @for (t of lookups.tenants(); track t.id) { <mat-option [value]="t.id">{{ t.name }}</mat-option> }</mat-select></mat-form-field>
          <mat-form-field><mat-label>Company</mat-label><mat-select [(ngModel)]="m.companyId" [disabled]="!m.tenantId"><mat-option value="">All companies</mat-option>
            @for (c of lookups.companiesOf(m.tenantId); track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }</mat-select></mat-form-field>
          <mat-form-field><mat-label>Application</mat-label><mat-select [(ngModel)]="m.applicationId" [disabled]="!m.tenantId"><mat-option value="">All applications</mat-option>
            @for (a of lookups.appsOf(m.tenantId); track a.id) { <mat-option [value]="a.id">{{ a.name }}</mat-option> }</mat-select></mat-form-field>
          <mat-form-field><mat-label>Channel</mat-label><mat-select [(ngModel)]="m.channel"><mat-option value="">All channels</mat-option>
            @for (c of channels; track c) { <mat-option [value]="c">{{ c | channel }}</mat-option> }</mat-select></mat-form-field>
        </div>
        <div class="row"><span class="spacer"></span>
          @if (sel()) { <button mat-button (click)="reset()">Add a different user</button> }
          <button mat-flat-button (click)="save()" [disabled]="busy() || !m.email.trim() || !m.displayName.trim()"><mat-icon>save</mat-icon>{{ sel() ? 'Grant role' : 'Create user' }}</button></div>
      </section>
    </div>`,
  styles: [`.tag .x { all: unset; cursor: pointer; margin-left: 4px; color: var(--bad); font-weight: 700; } .tag .x:focus-visible { outline: 2px solid var(--accent); }`],
})
export class Users implements OnInit {
  private api = inject(ApiService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  auth = inject(AuthService);
  lookups = inject(LookupsService);
  roles = ROLES; channels = CHANNELS;
  users = signal<AdminUser[]>([]);
  sel = signal<AdminUser | null>(null);
  busy = signal(false);
  error = signal<unknown>(null);
  m = this.blank();

  ngOnInit() { this.load(); }
  load() { this.api.users().subscribe({ next: u => this.users.set(u), error: e => this.error.set(e) }); }
  blank() { return { email: '', displayName: '', password: '', role: 'NotificationOperator', tenantId: this.lookups.tenants()[0]?.id ?? '', companyId: '', applicationId: '', channel: '' }; }
  reset() { this.sel.set(null); this.m = this.blank(); }
  pick(u: AdminUser) { this.sel.set(u); this.m = { ...this.blank(), email: u.email, displayName: u.displayName }; }

  scope(r: RoleAssignment) {
    const p = [r.tenantId ? this.lookups.tenantName(r.tenantId) : 'platform'];
    if (r.companyId) p.push(this.lookups.companyName(r.companyId));
    if (r.applicationId) p.push(this.lookups.appName(r.applicationId));
    if (r.channel) p.push(r.channel);
    return p.join(' / ');
  }

  save() {
    const m = this.m;
    this.busy.set(true); this.error.set(null);
    this.api.saveUser({ email: m.email.trim(), displayName: m.displayName.trim(), password: this.sel() ? null : m.password || null,
      roles: [{ role: m.role, tenantId: m.tenantId || null, companyId: m.companyId || null, applicationId: m.applicationId || null, channel: m.channel || null }] }).subscribe({
      next: () => { this.busy.set(false); this.snack.open(this.sel() ? 'Role granted' : 'User created', 'OK', { duration: 2500 }); this.m.password = ''; this.load(); },
      error: e => { this.busy.set(false); this.error.set(e); },
    });
  }

  removeRole(u: AdminUser, r: RoleAssignment) {
    confirmDialog(this.dialog, { title: 'Remove role', message: `Remove ${r.role} (${this.scope(r)}) from ${u.displayName}?`, confirmText: 'Remove', danger: true }).subscribe(ok => {
      if (ok) this.api.removeRole(r.id).subscribe({ next: () => this.load(), error: e => this.error.set(e) });
    });
  }
}
