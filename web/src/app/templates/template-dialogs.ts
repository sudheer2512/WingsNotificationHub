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
import { CHANNELS, Channel, MESSAGE_CLASSES, NotificationType, PRIORITIES, TemplateSummary, TemplateVersion, WhatsAppAccount } from '../core/models';
import { UI } from '../shared/ui';
import { extractVariables, isBuiltIn, sampleData } from './template-utils';

const SCOPE_FIELDS = `
  <mat-form-field><mat-label>Tenant</mat-label>
    <mat-select [(ngModel)]="m.tenantId" name="tenantId" required (selectionChange)="m.companyId = ''; m.applicationId = ''">
      @for (t of lookups.tenants(); track t.id) { <mat-option [value]="t.id">{{ t.name }}</mat-option> }
    </mat-select></mat-form-field>
  <mat-form-field><mat-label>Company</mat-label>
    <mat-select [(ngModel)]="m.companyId" name="companyId">
      <mat-option value="">All companies (tenant-wide)</mat-option>
      @for (c of lookups.companiesOf(m.tenantId); track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }
    </mat-select></mat-form-field>
  <mat-form-field><mat-label>Application</mat-label>
    <mat-select [(ngModel)]="m.applicationId" name="applicationId">
      <mat-option value="">All applications</mat-option>
      @for (a of lookups.appsOf(m.tenantId); track a.id) { <mat-option [value]="a.id">{{ a.name }}</mat-option> }
    </mat-select></mat-form-field>
  <mat-form-field><mat-label>Code</mat-label><input matInput [(ngModel)]="m.code" name="code" required placeholder="INVOICE_CREATED_V1" (blur)="m.code = m.code.toUpperCase()"></mat-form-field>
  <mat-form-field><mat-label>Name</mat-label><input matInput [(ngModel)]="m.name" name="name" required></mat-form-field>
  <mat-form-field><mat-label>Category</mat-label><input matInput [(ngModel)]="m.category" name="category" placeholder="Finance"></mat-form-field>
  <mat-form-field><mat-label>Channel</mat-label>
    <mat-select [(ngModel)]="m.channel" name="channel" required [disabled]="lockChannel">
      @for (c of channels; track c) { <mat-option [value]="c">{{ c | channel }}</mat-option> }
    </mat-select></mat-form-field>
  <mat-form-field><mat-label>Locale</mat-label><input matInput [(ngModel)]="m.locale" name="locale" placeholder="en, hi, ta-IN"></mat-form-field>
  <mat-form-field class="span-2"><mat-label>Description</mat-label><input matInput [(ngModel)]="m.description" name="description"></mat-form-field>`;

interface ScopeModel { tenantId: string; companyId: string; applicationId: string; code: string; name: string; category: string; channel: Channel; locale: string; description: string; }

function upsertBody(m: ScopeModel) {
  return { tenantId: m.tenantId, companyId: m.companyId || null, applicationId: m.applicationId || null, code: m.code.trim().toUpperCase(), name: m.name.trim(),
    description: m.description || null, category: m.category || null, channel: m.channel, locale: m.locale || 'en' };
}

/** Create a template: scope + identity + a first draft body. */
@Component({
  selector: 'app-create-template-dialog',
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, ...UI],
  template: `
    <h2 mat-dialog-title>New template</h2>
    <mat-dialog-content>
      <form class="form-grid" #f="ngForm">
        ${SCOPE_FIELDS}
        @if (m.channel === 'email') { <mat-form-field class="span-2"><mat-label>Subject</mat-label><input matInput [(ngModel)]="subject" name="subject"></mat-form-field> }
        <mat-form-field class="span-2"><mat-label>Body</mat-label><textarea matInput rows="5" [(ngModel)]="body" name="body" required [placeholder]="bodyHint"></textarea>
          <mat-hint>Further editing (rich text, HTML, variables, preview) happens in the editor after creation.</mat-hint></mat-form-field>
      </form>
      <app-error [error]="error()" />
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button (click)="save()" [disabled]="busy() || !m.tenantId || !m.code || !m.name || !body.trim()">Create draft</button>
    </mat-dialog-actions>`,
})
export class CreateTemplateDialog {
  private api = inject(ApiService);
  private ref = inject(MatDialogRef<CreateTemplateDialog>);
  lookups = inject(LookupsService);
  data = inject<{ tenantId?: string; companyId?: string; applicationId?: string; category?: string } | null>(MAT_DIALOG_DATA, { optional: true });
  channels = CHANNELS;
  lockChannel = false;
  m: ScopeModel = { tenantId: this.data?.tenantId ?? this.lookups.tenants()[0]?.id ?? '', companyId: this.data?.companyId ?? '', applicationId: this.data?.applicationId ?? '',
    code: '', name: '', category: this.data?.category ?? '', channel: 'email', locale: 'en', description: '' };
  subject = ''; body = '';
  bodyHint = 'Dear {{CustomerName}}, …';
  busy = signal(false); error = signal<unknown>(null);

  save() {
    this.busy.set(true);
    const html = this.m.channel === 'email';
    this.api.createTemplate(upsertBody(this.m), { subject: this.subject || null, body: html ? `<p>${this.body.replace(/\n/g, '<br>')}</p>` : this.body, bodyFormat: html ? 'Html' : 'Text', changeNote: 'Created' })
      .subscribe({ next: r => this.ref.close(r.id), error: e => { this.busy.set(false); this.error.set(e); } });
  }
}

/** Clone into a new code, scope or locale (translations). */
@Component({
  selector: 'app-clone-template-dialog',
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, ...UI],
  template: `
    <h2 mat-dialog-title>Clone {{ data.code }}</h2>
    <mat-dialog-content>
      <p class="small muted">Copies the active (or latest) version into a new template draft. Use a different locale for a translation, or a narrower company/application scope for an override.</p>
      <form class="form-grid">${SCOPE_FIELDS}</form>
      <app-error [error]="error()" />
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button (click)="save()" [disabled]="busy() || !m.code || !m.name">Clone</button>
    </mat-dialog-actions>`,
})
export class CloneTemplateDialog {
  private api = inject(ApiService);
  private ref = inject(MatDialogRef<CloneTemplateDialog>);
  lookups = inject(LookupsService);
  data = inject<TemplateSummary>(MAT_DIALOG_DATA);
  channels = CHANNELS;
  lockChannel = false;
  m: ScopeModel = { tenantId: this.data.tenantId, companyId: this.data.companyId ?? '', applicationId: this.data.applicationId ?? '', code: this.data.code, name: this.data.name + ' (copy)',
    category: this.data.category ?? '', channel: this.data.channel, locale: this.data.locale, description: this.data.description ?? '' };
  busy = signal(false); error = signal<unknown>(null);
  save() {
    this.busy.set(true);
    this.api.cloneTemplate(this.data.id, upsertBody(this.m)).subscribe({ next: r => this.ref.close(r.id), error: e => { this.busy.set(false); this.error.set(e); } });
  }
}

/** Test send: only the active version is delivered; other versions return a rendered preview. */
@Component({
  selector: 'app-test-send-dialog',
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatIconModule, ...UI],
  template: `
    <h2 mat-dialog-title>Test send · {{ data.template.code }} ({{ data.template.channel | channel }})</h2>
    <mat-dialog-content>
      <form class="form-grid">
        <mat-form-field><mat-label>Company</mat-label>
          <mat-select [(ngModel)]="companyId" name="companyId" required>
            @for (c of companies(); track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }
          </mat-select></mat-form-field>
        <mat-form-field><mat-label>Application</mat-label>
          <mat-select [(ngModel)]="applicationId" name="applicationId" required>
            @for (a of apps(); track a.id) { <mat-option [value]="a.id">{{ a.name }}</mat-option> }
          </mat-select></mat-form-field>
        <mat-form-field><mat-label>Version</mat-label>
          <mat-select [(ngModel)]="version" name="version">
            @for (v of data.versions; track v.id) { <mat-option [value]="v.versionNumber">v{{ v.versionNumber }} · {{ v.status | human }}</mat-option> }
          </mat-select>
          @if (version !== data.template.activeVersion) { <mat-hint>Not active: a rendered preview is returned instead of a delivery.</mat-hint> }
        </mat-form-field>
        @switch (data.template.channel) {
          @case ('email') { <mat-form-field><mat-label>Email</mat-label><input matInput [(ngModel)]="r.email" name="email" type="email"></mat-form-field> }
          @case ('sms') { <mat-form-field><mat-label>Mobile (E.164)</mat-label><input matInput [(ngModel)]="r.mobile" name="mobile" placeholder="+9198…"></mat-form-field> }
          @case ('whatsapp') { <mat-form-field><mat-label>WhatsApp (E.164)</mat-label><input matInput [(ngModel)]="r.whatsApp" name="wa" placeholder="+9198…"></mat-form-field> }
          @default { <mat-form-field><mat-label>User id</mat-label><input matInput [(ngModel)]="r.userId" name="userId"></mat-form-field> }
        }
        <mat-form-field><mat-label>Recipient name</mat-label><input matInput [(ngModel)]="r.name" name="rname"></mat-form-field>
        <mat-form-field class="span-2"><mat-label>Variables (JSON)</mat-label><textarea matInput class="code" rows="6" [(ngModel)]="vars" name="vars"></textarea></mat-form-field>
      </form>
      <app-error [error]="error()" />
      @if (result(); as res) {
        @if (res.delivered) {
          <div class="callout good">Accepted as notification <code>{{ res.resp?.notificationId }}</code>.
            @for (d of res.resp?.deliveries ?? []; track d.deliveryId) { <div><app-chip [value]="d.status" /> {{ d.recipient }} {{ d.reason }}</div> }
            @for (w of res.resp?.warnings ?? []; track $index) { <div class="small">{{ w }}</div> }
          </div>
        } @else {
          <div class="callout warn">{{ res.note }}</div>
          @if (res.preview; as p) { @if (p.subject) { <b>{{ p.subject }}</b> } <pre class="json">{{ p.textBody ?? p.body }}</pre> }
        }
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Close</button>
      <button mat-flat-button (click)="send()" [disabled]="busy() || !companyId || !applicationId"><mat-icon>send</mat-icon>Send test</button>
    </mat-dialog-actions>`,
})
export class TestSendDialog {
  private api = inject(ApiService);
  lookups = inject(LookupsService);
  data = inject<{ template: TemplateSummary; versions: TemplateVersion[] }>(MAT_DIALOG_DATA);
  companies = computed(() => this.lookups.companiesOf(this.data.template.tenantId).filter(c => !this.data.template.companyId || c.id === this.data.template.companyId));
  companyId = this.data.template.companyId ?? this.companies()[0]?.id ?? '';
  apps = computed(() => this.lookups.appsOf(this.data.template.tenantId).filter(a => !this.data.template.applicationId || a.id === this.data.template.applicationId)
    .sort((a, b) => Number(b.companies.some(l => l.companyId === this.companyId && l.enabled)) - Number(a.companies.some(l => l.companyId === this.companyId && l.enabled))));
  applicationId = this.data.template.applicationId ?? this.apps()[0]?.id ?? '';
  version = this.data.template.activeVersion ?? this.data.versions[0]?.versionNumber;
  r: Record<string, string> = { email: '', mobile: '', whatsApp: '', userId: '', name: 'Test recipient' };
  vars = JSON.stringify(this.sample(), null, 2);

  private sample(): Record<string, unknown> {
    const v = this.data.versions.find(x => x.versionNumber === this.version);
    const out = sampleData(v?.variables ?? this.data.template.variables);
    for (const name of extractVariables(v?.subject, v?.body, v?.textBody)) if (!(name in out) && !isBuiltIn(name)) out[name] = `[${name}]`;
    return out;
  }
  busy = signal(false); error = signal<unknown>(null);
  result = signal<{ delivered: boolean; note?: string; resp?: import('../core/models').SendResponse; preview?: import('../core/models').PreviewResponse } | null>(null);

  send() {
    let variables: Record<string, unknown>;
    try { variables = this.vars.trim() ? JSON.parse(this.vars) : {}; } catch (e) { this.error.set(new Error('Variables must be valid JSON: ' + (e as Error).message)); return; }
    const recipient = Object.fromEntries(Object.entries(this.r).filter(([, v]) => !!v.trim()));
    this.busy.set(true); this.error.set(null); this.result.set(null);
    this.api.testSend(this.data.template.id, { companyId: this.companyId, applicationId: this.applicationId, recipient, variables, version: this.version })
      .subscribe({ next: r => { this.busy.set(false); this.result.set(r); }, error: e => { this.busy.set(false); this.error.set(e); } });
  }
}

/** Submit a WhatsApp template version to Meta through a WhatsApp provider account. */
@Component({
  selector: 'app-wa-submit-dialog',
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatSelectModule, ...UI],
  template: `
    <h2 mat-dialog-title>Submit to WhatsApp</h2>
    <mat-dialog-content>
      <p class="small">Submits the provider template name, language, category and components to the WhatsApp Business account for approval. The approval status is recorded on the version.</p>
      @if (providers()?.length === 0) { <div class="callout warn">No WhatsApp provider account is available. Ask an administrator to add one under Providers.</div> }
      <mat-form-field class="full"><mat-label>WhatsApp provider account</mat-label>
        <mat-select [(ngModel)]="providerAccountId">
          @for (p of providers() ?? []; track p.id) { <mat-option [value]="p.id">{{ p.name }} ({{ p.providerType }})</mat-option> }
        </mat-select></mat-form-field>
      <mat-form-field class="full"><mat-label>Version</mat-label>
        <mat-select [(ngModel)]="version">
          @for (v of data.versions; track v.id) { <mat-option [value]="v.versionNumber">v{{ v.versionNumber }} · {{ v.status | human }}@if (v.providerTemplateName) { · {{ v.providerTemplateName }} }</mat-option> }
        </mat-select></mat-form-field>
      <app-error [error]="error()" />
      @if (status()) { <div class="callout good">Provider status: <b>{{ status() }}</b></div> }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button [mat-dialog-close]="!!status()">Close</button>
      <button mat-flat-button (click)="submit()" [disabled]="busy() || !providerAccountId || !version">Submit</button>
    </mat-dialog-actions>`,
})
export class WhatsAppSubmitDialog implements OnInit {
  private api = inject(ApiService);
  data = inject<{ template: TemplateSummary; versions: TemplateVersion[] }>(MAT_DIALOG_DATA);
  providers = signal<WhatsAppAccount[] | null>(null);
  providerAccountId = '';
  version = this.data.versions[0]?.versionNumber;
  busy = signal(false); error = signal<unknown>(null); status = signal<string | null>(null);
  ngOnInit() {
    this.api.whatsAppAccounts(this.data.template.id).subscribe({ next: a => { this.providers.set(a); this.providerAccountId = a[0]?.id ?? ''; }, error: e => this.error.set(e) });
  }
  submit() {
    this.busy.set(true); this.error.set(null);
    this.api.whatsAppSubmit(this.data.template.id, this.providerAccountId, this.version!).subscribe({
      next: r => { this.busy.set(false); this.status.set(r.status); }, error: e => { this.busy.set(false); this.error.set(e); },
    });
  }
}

/** Notification types (catalog of business message kinds with their policy defaults). */
@Component({
  selector: 'app-notification-types-dialog',
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatCheckboxModule, MatIconModule, ...UI],
  template: `
    <h2 mat-dialog-title>Notification types</h2>
    <mat-dialog-content>
      <app-error [error]="error()" />
      <div class="table-wrap" style="max-height:34vh">
        <table class="data">
          <thead><tr><th>Code</th><th>Name</th><th>Class</th><th>Channels</th><th>Default template</th><th>Flags</th></tr></thead>
          <tbody>
            @for (t of types(); track t.id) {
              <tr class="clickable" [class.selected]="m.code === t.code && m.tenantId === t.tenantId" (click)="edit(t)">
                <td><code>{{ t.code }}</code></td><td>{{ t.name }}</td><td>{{ t.messageClass }}</td>
                <td>@for (c of t.defaultChannels; track c) { <span class="tag">{{ c | channel }}</span> }</td><td class="small">{{ t.defaultTemplateCode ?? '—' }}</td>
                <td class="small">@if (t.mandatory) {<span class="tag">mandatory</span>}@if (t.isSensitive) {<span class="tag">sensitive</span>}@if (t.aiAllowed) {<span class="tag ai">AI allowed</span>}</td>
              </tr>
            } @empty { <tr><td colspan="6" class="muted">No notification types</td></tr> }
          </tbody>
        </table>
      </div>
      @if (canManage) {
        <h3 style="margin:14px 0 6px">{{ editing ? 'Edit ' + m.code : 'New notification type' }}</h3>
        <form class="form-grid">
          <mat-form-field><mat-label>Tenant</mat-label>
            <mat-select [(ngModel)]="m.tenantId" name="tenantId">@for (t of lookups.tenants(); track t.id) { <mat-option [value]="t.id">{{ t.name }}</mat-option> }</mat-select></mat-form-field>
          <mat-form-field><mat-label>Application (optional)</mat-label>
            <mat-select [(ngModel)]="m.applicationId" name="applicationId"><mat-option [value]="null">All applications</mat-option>
              @for (a of lookups.appsOf(m.tenantId); track a.id) { <mat-option [value]="a.id">{{ a.name }}</mat-option> }</mat-select></mat-form-field>
          <mat-form-field><mat-label>Code</mat-label><input matInput [(ngModel)]="m.code" name="code" [readonly]="editing"></mat-form-field>
          <mat-form-field><mat-label>Name</mat-label><input matInput [(ngModel)]="m.name" name="name"></mat-form-field>
          <mat-form-field><mat-label>Category</mat-label><input matInput [(ngModel)]="m.category" name="category"></mat-form-field>
          <mat-form-field><mat-label>Message class</mat-label>
            <mat-select [(ngModel)]="m.messageClass" name="messageClass">@for (c of classes; track c) { <mat-option [value]="c">{{ c }}</mat-option> }</mat-select></mat-form-field>
          <mat-form-field><mat-label>Default channels</mat-label>
            <mat-select [(ngModel)]="m.defaultChannels" name="defaultChannels" multiple>@for (c of channels; track c) { <mat-option [value]="c">{{ c | channel }}</mat-option> }</mat-select></mat-form-field>
          <mat-form-field><mat-label>Default template code</mat-label><input matInput [(ngModel)]="m.defaultTemplateCode" name="dtc"></mat-form-field>
          <mat-form-field><mat-label>Default priority</mat-label>
            <mat-select [(ngModel)]="m.defaultPriority" name="prio">@for (p of priorities; track p) { <mat-option [value]="p">{{ p }}</mat-option> }</mat-select></mat-form-field>
          <mat-form-field class="span-2"><mat-label>Description</mat-label><input matInput [(ngModel)]="m.description" name="desc"></mat-form-field>
        </form>
        <div class="wrap-row">
          <mat-checkbox [(ngModel)]="m.mandatory" name="mand">Mandatory (ignores opt-outs)</mat-checkbox>
          <mat-checkbox [(ngModel)]="m.isSensitive" name="sens">Sensitive (codes masked in logs)</mat-checkbox>
          <mat-checkbox [(ngModel)]="m.aiAllowed" name="ai">AI agents may send</mat-checkbox>
          <mat-checkbox [(ngModel)]="m.requiresHumanApprovalForAi" name="aih" [disabled]="!m.aiAllowed">AI sends need human approval</mat-checkbox>
        </div>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Close</button>
      @if (canManage) {
        <button mat-button (click)="reset()">New</button>
        <button mat-flat-button (click)="save()" [disabled]="busy() || !m.tenantId || !m.code || !m.name">{{ editing ? 'Save' : 'Create' }}</button>
      }
    </mat-dialog-actions>`,
})
export class NotificationTypesDialog implements OnInit {
  private api = inject(ApiService);
  lookups = inject(LookupsService);
  canManage = inject(AuthService).can('ManageTemplates');
  channels = CHANNELS; classes = MESSAGE_CLASSES; priorities = PRIORITIES;
  types = signal<NotificationType[]>([]);
  busy = signal(false); error = signal<unknown>(null);
  editing = false;
  m: Partial<NotificationType> = {};

  ngOnInit() { this.reset(); this.load(); }
  load() { this.api.notificationTypes().subscribe({ next: t => this.types.set(t), error: e => this.error.set(e) }); }
  reset() {
    this.editing = false;
    this.m = { tenantId: this.lookups.tenants()[0]?.id, applicationId: null, code: '', name: '', category: 'General', messageClass: 'Transactional', defaultChannels: ['email'],
      defaultTemplateCode: '', defaultPriority: 'Normal', mandatory: false, aiAllowed: false, requiresHumanApprovalForAi: true, isSensitive: false, description: '' };
  }
  edit(t: NotificationType) { this.editing = true; this.m = { ...t }; }
  save() {
    this.busy.set(true); this.error.set(null);
    const { id: _id, createdAt: _c, ...body } = this.m as NotificationType & { concurrencyStamp?: string };
    this.api.saveNotificationType({ ...body, defaultTemplateCode: body.defaultTemplateCode || null }).subscribe({
      next: () => { this.busy.set(false); this.load(); this.reset(); }, error: e => { this.busy.set(false); this.error.set(e); },
    });
  }
}
