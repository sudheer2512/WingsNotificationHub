import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { LookupsService } from '../core/lookups.service';
import { Application, CHANNELS, Credential } from '../core/models';
import { UI } from '../shared/ui';
import { confirmDialog, showSecret } from '../shared/widgets';

const ALL_SCOPES = ['notifications.send', 'notifications.read', 'notifications.batch', 'events.publish', 'templates.read', 'templates.write', 'contacts.write', 'devices.write',
  'preferences.read', 'preferences.write', 'inapp.token', 'usage.read', 'providers.health', 'webhooks.manage', 'mcp'];
const DEFAULT_SCOPES = ['notifications.send', 'notifications.read', 'notifications.batch', 'events.publish', 'templates.read', 'contacts.write', 'devices.write',
  'preferences.read', 'preferences.write', 'inapp.token', 'usage.read', 'providers.health', 'webhooks.manage'];

@Component({
  selector: 'app-application-form',
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSelectModule, ...UI],
  template: `
    <h2 mat-dialog-title>{{ data ? 'Edit ' + data.name : 'New application' }}</h2>
    <mat-dialog-content>
      <div class="form-grid">
        <mat-form-field><mat-label>Tenant</mat-label><mat-select [(ngModel)]="m.tenantId" [disabled]="!!data">@for (t of lookups.tenants(); track t.id) { <mat-option [value]="t.id">{{ t.name }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Code</mat-label><input matInput [(ngModel)]="m.code" [readonly]="!!data" required></mat-form-field>
        <mat-form-field><mat-label>Name</mat-label><input matInput [(ngModel)]="m.name" required></mat-form-field>
        <mat-form-field><mat-label>Allowed channels (empty = all)</mat-label><mat-select [(ngModel)]="m.allowedChannels" multiple>@for (c of channels; track c) { <mat-option [value]="c">{{ c | channel }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Default locale</mat-label><input matInput [(ngModel)]="m.defaultLocale"></mat-form-field>
        <mat-form-field><mat-label>Rate limit (requests/min per credential)</mat-label><input matInput type="number" min="1" [(ngModel)]="m.rateLimitPerMinute"></mat-form-field>
        <mat-form-field class="span-2"><mat-label>Description</mat-label><input matInput [(ngModel)]="m.description"></mat-form-field>
      </div>
      <mat-checkbox [(ngModel)]="m.sandbox">Sandbox (messages are not sent to real providers)</mat-checkbox>
      <app-error [error]="error()" />
    </mat-dialog-content>
    <mat-dialog-actions align="end"><button mat-button mat-dialog-close>Cancel</button><button mat-flat-button (click)="save()" [disabled]="busy() || !m.tenantId || !m.code || !m.name">Save</button></mat-dialog-actions>`,
})
export class ApplicationForm {
  private api = inject(ApiService);
  private ref = inject(MatDialogRef<ApplicationForm>);
  lookups = inject(LookupsService);
  data = inject<Application | null>(MAT_DIALOG_DATA, { optional: true });
  channels = CHANNELS;
  busy = signal(false); error = signal<unknown>(null);
  m = { tenantId: this.data?.tenantId ?? this.lookups.tenants()[0]?.id ?? '', code: this.data?.code ?? '', name: this.data?.name ?? '', description: this.data?.description ?? '',
    sandbox: this.data?.sandbox ?? false, allowedChannels: [...(this.data?.allowedChannels ?? [])], defaultLocale: this.data?.defaultLocale ?? 'en', rateLimitPerMinute: this.data?.rateLimitPerMinute ?? 600 };
  save() {
    const body = { ...this.m, description: this.m.description || null, rateLimitPerMinute: Number(this.m.rateLimitPerMinute) || 600 };
    this.busy.set(true); this.error.set(null);
    (this.data ? this.api.updateApplication(this.data.id, body) : this.api.createApplication(body)).subscribe({ next: r => this.ref.close(r.id), error: e => { this.busy.set(false); this.error.set(e); } });
  }
}

@Component({
  selector: 'app-applications',
  imports: [FormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, MatSlideToggleModule, ...UI],
  template: `
    <div class="page">
      <div class="page-head"><h1>Applications</h1><span class="sub">ERP, HRMS, CRM and other systems that send through the hub</span><span class="spacer"></span>
        @if (auth.can('ManageTenancy')) { <button mat-flat-button (click)="edit()"><mat-icon>add</mat-icon>Application</button> }</div>
      <app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />
      <div class="split apps">
        <section class="panel">
          <div class="list-scroll">
            @for (a of apps(); track a.id) {
              <div class="app-item" [class.selected]="a.id === sel()?.id" (click)="select(a)" tabindex="0" (keyup.enter)="select(a)">
                <div class="row"><b class="ellipsis">{{ a.name }}</b><span class="spacer"></span><app-chip [value]="a.status" />@if (a.sandbox) { <span class="tag">sandbox</span> }</div>
                <div class="small muted"><code>{{ a.code }}</code> · {{ lookups.tenantName(a.tenantId) }} · {{ a.companies.length }} companies</div>
              </div>
            } @empty { <app-empty icon="apps" text="No applications" /> }
          </div>
        </section>
        <section style="min-width:0">
          @if (sel(); as a) {
            <div class="panel">
              <div class="panel-head"><h2>{{ a.name }}</h2><span class="spacer"></span>
                @if (auth.can('ManageTenancy', 'ManageCredentials')) { <button mat-stroked-button (click)="edit(a)"><mat-icon>edit</mat-icon>Edit</button> }</div>
              <dl class="kv">
                <dt>Code</dt><dd><code>{{ a.code }}</code></dd><dt>Tenant</dt><dd>{{ lookups.tenantName(a.tenantId) }}</dd>
                <dt>Status</dt><dd><app-chip [value]="a.status" [tip]="a.suspendedReason ?? null" /></dd>
                <dt>Channels</dt><dd>@for (c of a.allowedChannels; track c) { <span class="tag">{{ c | channel }}</span> } @empty { all }</dd>
                <dt>Locale / rate limit</dt><dd>{{ a.defaultLocale }} · {{ a.rateLimitPerMinute | num }} req/min</dd>
                @if (a.description) { <dt>Description</dt><dd>{{ a.description }}</dd> }
              </dl>
            </div>
            <div class="panel">
              <h2>Company links</h2>
              <p class="small muted">An application can send only for companies it is enabled for.</p>
              <div class="table-wrap"><table class="data">
                <thead><tr><th>Company</th><th>Enabled</th><th>Default senders</th></tr></thead>
                <tbody>@for (c of lookups.companiesOf(a.tenantId); track c.id) {
                  <tr><td>{{ c.name }}</td>
                    <td><mat-slide-toggle [checked]="linked(a, c.id)" (change)="link(a, c.id, $event.checked)" [disabled]="!auth.can('ManageTenancy')" [attr.aria-label]="'Enable for ' + c.name" /></td>
                    <td class="small">@for (s of senders(a, c.id); track s[0]) { <span class="tag">{{ s[0] }}: {{ s[1] | short }}</span> } @empty { — }</td></tr>
                }</tbody>
              </table></div>
            </div>
            @if (auth.can('ManageCredentials')) {
              <div class="panel">
                <h2>Client credentials</h2>
                <p class="small muted">OAuth 2.0 client-credentials (<code>POST /connect/token</code>). The secret is shown once when created.</p>
                <div class="table-wrap"><table class="data">
                  <thead><tr><th>Name</th><th>Client id</th><th>Scopes</th><th>Companies</th><th>Last used</th><th>Status</th><th></th></tr></thead>
                  <tbody>@for (c of creds(); track c.id) {
                    <tr><td>{{ c.name }}@if (c.isAiAgent) { <span class="tag ai">AI agent</span> }</td><td><code>{{ c.clientId }}</code></td>
                      <td class="small" style="max-width:280px">{{ c.scopes.join(' ') }}</td>
                      <td class="small">@for (id of c.allowedCompanyIds; track id) { <span class="tag">{{ lookups.companyName(id) }}</span> } @empty { all linked }</td>
                      <td class="small nowrap">{{ c.lastUsedAt ? (c.lastUsedAt | ago) : 'never' }}</td>
                      <td>@if (c.revokedAt) { <app-chip tone="bad" label="Revoked" /> } @else if (expired(c)) { <app-chip tone="off" label="Expired" /> } @else { <app-chip tone="good" label="Active" /> }</td>
                      <td>@if (!c.revokedAt) { <button mat-button class="danger" (click)="revoke(c)">Revoke</button> }</td></tr>
                  } @empty { <tr><td colspan="7" class="muted">No credentials</td></tr> }</tbody>
                </table></div>
                <h3 style="margin:12px 0 4px">New credential</h3>
                <div class="form-grid">
                  <mat-form-field><mat-label>Name</mat-label><input matInput [(ngModel)]="cred.name" placeholder="Production ERP"></mat-form-field>
                  <mat-form-field><mat-label>Scopes</mat-label><mat-select [(ngModel)]="cred.scopes" multiple>@for (s of allScopes; track s) { <mat-option [value]="s">{{ s }}</mat-option> }</mat-select></mat-form-field>
                  <mat-form-field><mat-label>Restrict to companies</mat-label><mat-select [(ngModel)]="cred.allowedCompanyIds" multiple>@for (c of lookups.companiesOf(a.tenantId); track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }</mat-select></mat-form-field>
                  <mat-form-field><mat-label>Expires (optional)</mat-label><input matInput type="date" [(ngModel)]="cred.expiresAt"></mat-form-field>
                </div>
                <div class="wrap-row"><mat-checkbox [(ngModel)]="cred.isAiAgent">AI agent (stricter policy; may need human approval per notification type)</mat-checkbox><span class="spacer"></span>
                  <button mat-flat-button (click)="createCred(a)" [disabled]="!cred.name.trim() || !cred.scopes.length"><mat-icon>key</mat-icon>Create credential</button></div>
              </div>
            }
          } @else { <div class="panel"><app-empty icon="apps" text="Select an application" /></div> }
        </section>
      </div>
    </div>`,
  styles: [`
    .apps { grid-template-columns: minmax(240px, 340px) 1fr; } @media (max-width: 1200px) { .apps { grid-template-columns: 1fr; } .apps .list-scroll { max-height: 300px; } }
    .app-item { padding: 6px 8px; border-radius: 6px; cursor: pointer; border: 1px solid transparent; }
    .app-item:hover { background: var(--hover); } .app-item.selected { background: var(--selected); border-color: var(--border); }
    section > .panel + .panel { margin-top: 12px; }`],
})
export class Applications implements OnInit {
  private api = inject(ApiService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  auth = inject(AuthService);
  lookups = inject(LookupsService);
  allScopes = ALL_SCOPES;
  apps = signal<Application[]>([]);
  sel = signal<Application | null>(null);
  creds = signal<Credential[]>([]);
  error = signal<unknown>(null);
  cred = { name: '', scopes: [...DEFAULT_SCOPES], allowedCompanyIds: [] as string[], isAiAgent: false, expiresAt: '' };

  ngOnInit() { this.load(); }
  load(selectId?: string) {
    this.api.applications().subscribe({
      next: a => { this.apps.set(a); this.lookups.applications.set(a); const id = selectId ?? this.sel()?.id; const f = id ? a.find(x => x.id === id) : null; if (f) this.select(f); },
      error: e => this.error.set(e),
    });
  }
  select(a: Application) {
    this.sel.set(a);
    this.creds.set([]);
    if (this.auth.can('ManageCredentials')) this.api.credentials(a.id).subscribe({ next: c => this.creds.set(c), error: e => this.error.set(e) });
  }
  linked(a: Application, companyId: string) { return a.companies.some(l => l.companyId === companyId && l.enabled); }
  senders(a: Application, companyId: string) { return Object.entries(a.companies.find(l => l.companyId === companyId)?.defaultSenders ?? {}); }
  expired(c: Credential) { return !!c.expiresAt && new Date(c.expiresAt).getTime() < Date.now(); }

  link(a: Application, companyId: string, enabled: boolean) {
    this.api.linkCompany(a.id, { companyId, enabled }).subscribe({ next: () => { this.snack.open(enabled ? 'Enabled for company' : 'Disabled for company', 'OK', { duration: 2500 }); this.load(a.id); }, error: e => { this.error.set(e); this.load(a.id); } });
  }
  edit(a?: Application) {
    this.dialog.open(ApplicationForm, { data: a ?? null, width: '760px', maxWidth: '96vw' }).afterClosed().subscribe(id => { if (id) { this.snack.open('Application saved', 'OK', { duration: 2500 }); this.load(id); } });
  }
  createCred(a: Application) {
    const c = this.cred;
    this.api.createCredential(a.id, { name: c.name.trim(), scopes: c.scopes, allowedCompanyIds: c.allowedCompanyIds.length ? c.allowedCompanyIds : null, isAiAgent: c.isAiAgent,
      expiresAt: c.expiresAt ? new Date(c.expiresAt + 'T23:59:59').toISOString() : null }).subscribe({
      next: r => {
        showSecret(this.dialog, 'Client credential created', r.note, [{ label: 'Client id', value: r.clientId, secret: true }, { label: 'Client secret', value: r.clientSecret, secret: true }]);
        this.cred = { name: '', scopes: [...DEFAULT_SCOPES], allowedCompanyIds: [], isAiAgent: false, expiresAt: '' };
        this.select(a);
      },
      error: e => this.error.set(e),
    });
  }
  revoke(c: Credential) {
    confirmDialog(this.dialog, { title: `Revoke ${c.clientId}`, message: 'Tokens issued to this credential stop working within seconds. This cannot be undone.', confirmText: 'Revoke', danger: true }).subscribe(r => {
      if (r) this.api.revokeCredential(c.id).subscribe({ next: () => { this.snack.open('Credential revoked', 'OK', { duration: 2500 }); this.select(this.sel()!); }, error: e => this.error.set(e) });
    });
  }
}
