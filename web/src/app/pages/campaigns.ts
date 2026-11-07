import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { Observable } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { LookupsService } from '../core/lookups.service';
import { CHANNELS, Campaign, CampaignDraft, CampaignRecipient, Channel, Group, MESSAGE_CLASSES, MessageClass, NotificationType, SendResponse, TemplateSummary } from '../core/models';
import { ConditionBuilder } from '../shared/condition-builder';
import { EdGroup, describe, fromApi, toApi, validateTree } from '../shared/conditions';
import { UI, toLocalInput } from '../shared/ui';
import { confirmDialog } from '../shared/widgets';

const TABS: { label: string; statuses: string[] }[] = [
  { label: 'All', statuses: [] }, { label: 'Draft', statuses: ['Draft'] }, { label: 'Pending approval', statuses: ['PendingApproval'] },
  { label: 'Scheduled', statuses: ['Approved', 'Scheduled'] }, { label: 'Running', statuses: ['Running'] }, { label: 'Paused', statuses: ['Paused'] },
  { label: 'Completed', statuses: ['Completed'] }, { label: 'Cancelled', statuses: ['Cancelled'] },
];

/** Create / edit a draft campaign. */
@Component({
  selector: 'app-campaign-form',
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, ConditionBuilder, ...UI],
  template: `
    <h2 mat-dialog-title>{{ data.draft ? 'Edit campaign' : 'New campaign' }}</h2>
    <mat-dialog-content>
      <div class="form-grid">
        <mat-form-field><mat-label>Company</mat-label>
          <mat-select [(ngModel)]="m.companyId" (selectionChange)="companyChanged()" [disabled]="!!data.draft">@for (c of lookups.companies(); track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Application</mat-label>
          <mat-select [(ngModel)]="m.applicationId" [disabled]="!!data.draft">@for (a of lookups.appsForCompany(m.companyId); track a.id) { <mat-option [value]="a.id">{{ a.name }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Name</mat-label><input matInput [(ngModel)]="m.name" required></mat-form-field>
        <mat-form-field><mat-label>Channels</mat-label>
          <mat-select [(ngModel)]="m.channels" multiple>@for (c of channels; track c) { <mat-option [value]="c">{{ c | channel }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Template code</mat-label>
          <mat-select [(ngModel)]="m.templateCode">@for (t of templateCodes(); track t) { <mat-option [value]="t">{{ t }}</mat-option> }</mat-select>
          <mat-hint>Needs an active template for every selected channel</mat-hint></mat-form-field>
        <mat-form-field><mat-label>Notification type</mat-label>
          <mat-select [(ngModel)]="m.notificationTypeCode"><mat-option value="">None</mat-option>@for (t of types(); track t.id) { <mat-option [value]="t.code">{{ t.code }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Message class</mat-label>
          <mat-select [(ngModel)]="m.messageClass">@for (c of classes; track c) { <mat-option [value]="c">{{ c }}</mat-option> }</mat-select>
          @if (m.messageClass === 'Promotional') { <mat-hint>Requires marketing consent; unsubscribes are honoured</mat-hint> }</mat-form-field>
        <mat-form-field><mat-label>Schedule (local time, optional)</mat-label><input matInput type="datetime-local" [(ngModel)]="m.scheduledAt"></mat-form-field>
        <mat-form-field><mat-label>Throttle (messages/minute)</mat-label><input matInput type="number" min="1" [(ngModel)]="m.throttlePerMinute"></mat-form-field>
        <mat-form-field><mat-label>Recipient groups</mat-label>
          <mat-select [(ngModel)]="m.groupIds" multiple>@for (g of groups(); track g.id) { <mat-option [value]="g.id">{{ g.name }} ({{ g.members }})</mat-option> }</mat-select></mat-form-field>
        <mat-form-field class="span-2"><mat-label>Description</mat-label><input matInput [(ngModel)]="m.description"></mat-form-field>
        <mat-form-field class="span-2"><mat-label>Template variables (one per line: Name=value)</mat-label><textarea matInput rows="3" class="code" [(ngModel)]="m.variables" placeholder="Discount=20%"></textarea>
          <mat-hint>Imported columns override these per recipient</mat-hint></mat-form-field>
      </div>
      <h3 style="margin:10px 0 4px">Segment (contact filter, optional)</h3>
      <app-condition-builder [root]="segment" [fields]="segmentFields" hint="Contact fields: kind, externalId, name, email, mobile, country, locale, roles, attributes.<name>." (changed)="segVersion.set(segVersion() + 1)" />
      <div class="small muted">{{ segSummary() }}</div>
      <app-error [error]="error()" />
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button (click)="save()" [disabled]="busy() || !m.companyId || !m.applicationId || !m.name || !m.channels.length || !m.templateCode">Save draft</button>
    </mat-dialog-actions>`,
})
export class CampaignForm implements OnInit {
  private api = inject(ApiService);
  private ref = inject(MatDialogRef<CampaignForm>);
  lookups = inject(LookupsService);
  data = inject<{ draft?: CampaignDraft; templates: TemplateSummary[]; types: NotificationType[] }>(MAT_DIALOG_DATA);
  channels = CHANNELS; classes = MESSAGE_CLASSES;
  types = signal(this.data.types);
  groups = signal<Group[]>([]);
  busy = signal(false); error = signal<unknown>(null);
  segment: EdGroup = fromApi(null);
  segVersion = signal(0);
  segSummary = computed(() => { this.segVersion(); return 'Segment: ' + describe(toApi(this.segment)); });
  segmentFields = ['kind', 'externalId', 'name', 'email', 'mobile', 'country', 'locale', 'timeZone', 'roles', 'attributes.tier', 'attributes.city'];
  m = {
    companyId: '', applicationId: '', name: '', description: '', channels: ['email'] as Channel[], templateCode: '', notificationTypeCode: '', messageClass: 'Promotional' as MessageClass,
    groupIds: [] as string[], variables: '', scheduledAt: '', throttlePerMinute: 600,
  };
  templateCodes = () => [...new Set(this.data.templates.filter(t => this.m.channels.includes(t.channel)).map(t => t.code))].sort();

  ngOnInit() {
    const c = this.data.draft;
    if (c) {
      this.m = { companyId: c.companyId, applicationId: c.applicationId, name: c.name, description: c.description ?? '', channels: [...c.channels], templateCode: c.templateCode,
        notificationTypeCode: c.notificationTypeCode ?? '', messageClass: c.messageClass, groupIds: [...(c.groupIds ?? [])], throttlePerMinute: c.throttlePerMinute,
        variables: Object.entries(c.variables ?? {}).map(([k, v]) => `${k}=${v}`).join('\n'), scheduledAt: toLocalInput(c.scheduledAt) };
      this.segment = fromApi(c.segment);
    } else {
      this.m.companyId = this.lookups.companies().find(x => this.lookups.appsForCompany(x.id).length)?.id ?? '';
      this.m.applicationId = this.lookups.appsForCompany(this.m.companyId)[0]?.id ?? '';
    }
    this.loadGroups();
  }

  companyChanged() { this.m.applicationId = this.lookups.appsForCompany(this.m.companyId)[0]?.id ?? ''; this.m.groupIds = []; this.loadGroups(); }
  loadGroups() { if (this.m.companyId) this.api.groups(this.m.companyId).subscribe({ next: g => this.groups.set(g), error: () => this.groups.set([]) }); }

  save() {
    const errs = validateTree(this.segment);
    if (errs.length) { this.error.set(new Error(errs.join(' '))); return; }
    const variables: Record<string, string> = {};
    for (const line of this.m.variables.split('\n')) { const i = line.indexOf('='); if (i > 0) variables[line.slice(0, i).trim()] = line.slice(i + 1).trim(); }
    const body = {
      companyId: this.m.companyId, applicationId: this.m.applicationId, name: this.m.name, description: this.m.description || null, channels: this.m.channels, templateCode: this.m.templateCode,
      notificationTypeCode: this.m.notificationTypeCode || null, messageClass: this.m.messageClass, groupIds: this.m.groupIds, segment: toApi(this.segment),
      variables: Object.keys(variables).length ? variables : null, scheduledAt: this.m.scheduledAt ? new Date(this.m.scheduledAt).toISOString() : null, throttlePerMinute: Number(this.m.throttlePerMinute) || 600,
    };
    this.busy.set(true); this.error.set(null);
    const c = this.data.draft;
    (c ? this.api.updateCampaign(c.id, body) : this.api.createCampaign(body)).subscribe({ next: r => this.ref.close(r.id), error: e => { this.busy.set(false); this.error.set(e); } });
  }
}

@Component({
  selector: 'app-campaigns',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, MatTabsModule, MatTooltipModule, RouterLink, ...UI],
  template: `
    <div class="page">
      <div class="page-head">
        <h1>Campaign manager</h1><span class="spacer"></span>
        <button mat-stroked-button (click)="load()"><mat-icon>refresh</mat-icon>Refresh</button>
        @if (canManage()) { <button mat-flat-button (click)="edit()"><mat-icon>add</mat-icon>New campaign</button> }
      </div>
      <app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />
      <nav class="tabs" role="tablist">
        @for (t of tabs; track t.label; let i = $index) {
          <button role="tab" [class.on]="tab() === i" [attr.aria-selected]="tab() === i" (click)="tab.set(i)">{{ t.label }} <span class="n">{{ count(t.statuses) }}</span></button>
        }
      </nav>
      <app-loading [on]="loading()" />
      <div class="table-wrap">
        <table class="data">
          <thead><tr><th>Campaign</th><th>Company / application</th><th>Channels</th><th>Status</th><th>Recipients</th><th class="num">Delivered</th><th class="num">Failed</th><th class="num">Cost</th><th>Schedule</th></tr></thead>
          <tbody>
            @for (c of visible(); track c.id) {
              <tr class="clickable" [class.selected]="c.id === selected()?.id" (click)="select(c)">
                <td><b>{{ c.name }}</b><div class="small muted">{{ c.templateCode }} · {{ c.messageClass }}</div></td>
                <td class="small">{{ lookups.companyName(c.companyId) }}<br><span class="muted">{{ lookups.appName(c.applicationId) }}</span></td>
                <td>@for (ch of c.channels; track ch) { <span class="tag">{{ ch | channel }}</span> }</td>
                <td><app-chip [value]="c.status" />@if (c.suspended) { <app-chip value="Suspended" /> }</td>
                <td style="min-width:140px"><div class="small">{{ processed(c) | num }} / {{ c.totalRecipients | num }}</div><app-meter [value]="processed(c)" [max]="c.totalRecipients || 1" [warnAt]="101" /></td>
                <td class="num">{{ (c.deliveries['Delivered'] ?? 0) + (c.deliveries['Read'] ?? 0) | num }}</td>
                <td class="num">{{ c.deliveries['Failed'] ?? 0 | num }}</td>
                <td class="num">{{ c.cost | money }}</td>
                <td class="small nowrap">{{ c.scheduledAt ? (c.scheduledAt | dt) : 'on approval' }}</td>
              </tr>
            } @empty { <tr><td colspan="9"><app-empty icon="campaign" text="No campaigns in this state" /></td></tr> }
          </tbody>
        </table>
      </div>

      @if (selected(); as c) {
        <section class="panel" style="margin-top:12px">
          <div class="panel-head">
            <h2>{{ c.name }}</h2><app-chip [value]="c.status" /><span class="spacer"></span>
            @if (canManage() && c.status === 'Draft') {
              <button mat-stroked-button (click)="edit(c)"><mat-icon>edit</mat-icon>Edit</button>
              <button mat-stroked-button (click)="fileInput.click()"><mat-icon>upload_file</mat-icon>Import recipients</button>
              <input #fileInput type="file" accept=".csv,.xlsx" hidden (change)="import(c, fileInput)">
              <button mat-stroked-button (click)="testSend(c)"><mat-icon>outgoing_mail</mat-icon>Test send</button>
              <button mat-flat-button (click)="act(c, 'submit', 'Submit for approval?', 'Another user with campaign approval rights must approve it.')"><mat-icon>send</mat-icon>Submit</button>
            }
            @if (c.status === 'PendingApproval' && auth.can('ApproveCampaigns')) {
              <button mat-flat-button (click)="act(c, 'approve', 'Approve campaign?', c.scheduledAt ? 'It starts at the scheduled time.' : 'It starts immediately.')" [disabled]="isOwn(c)"
                [matTooltip]="isOwn(c) ? 'Maker-checker: you created this campaign' : ''"><mat-icon>check</mat-icon>Approve</button>
            }
            @if (canManage() && c.status === 'Running') { <button mat-stroked-button (click)="act(c, 'pause')"><mat-icon>pause</mat-icon>Pause</button> }
            @if (canManage() && c.status === 'Paused') { <button mat-stroked-button (click)="act(c, 'resume')"><mat-icon>play_arrow</mat-icon>Resume</button> }
            @if (canManage() && c.status !== 'Completed' && c.status !== 'Cancelled') {
              <button mat-stroked-button class="danger" (click)="act(c, 'cancel', 'Cancel campaign?', 'Pending deliveries are cancelled. This cannot be undone.', true)"><mat-icon>cancel</mat-icon>Cancel</button>
            }
            @if (auth.can('EmergencySuspend', 'ApproveCampaigns') && (c.suspended || (c.status !== 'Completed' && c.status !== 'Cancelled'))) {
              <button mat-stroked-button [class.danger]="!c.suspended" (click)="suspend(c)"><mat-icon>{{ c.suspended ? 'play_circle' : 'front_hand' }}</mat-icon>{{ c.suspended ? 'Lift hold' : 'Emergency hold' }}</button>
            }
          </div>
          @if (importResult(); as r) {
            <div class="callout" [class.good]="!r.rejected" [class.warn]="r.rejected"><b>Import:</b> {{ r.imported }} imported, {{ r.rejected }} rejected.
              @if (r.errors.length) { <ul>@for (e of r.errors.slice(0, 50); track $index) { <li class="small">{{ e }}</li> }</ul>@if (r.errors.length > 50) { <div class="small">… {{ r.errors.length - 50 }} more</div> } }
            </div>
          }
          @if (testResult(); as t) {
            <div class="callout good small">Test accepted as <code>{{ t.notificationId }}</code>: @for (d of t.deliveries; track d.deliveryId) { <app-chip [value]="d.status" /> {{ d.channel | channel }} {{ d.reason }} }
              @for (w of t.warnings; track $index) { <div>{{ w }}</div> }</div>
          }
          <div class="detail-grid">
            <dl class="kv small">
              <dt>Company</dt><dd>{{ lookups.companyName(c.companyId) }}</dd><dt>Application</dt><dd>{{ lookups.appName(c.applicationId) }}</dd>
              <dt>Template</dt><dd>{{ c.templateCode }}</dd><dt>Throttle</dt><dd>{{ c.throttlePerMinute | num }}/min</dd>
            </dl>
            <dl class="kv small">
              <dt>Created</dt><dd>{{ c.createdAt | dt }} · {{ c.createdByName ?? c.createdBy }}</dd><dt>Approved</dt><dd>{{ c.approvedAt ? (c.approvedAt | dt) : '—' }}</dd>
              <dt>Started</dt><dd>{{ c.startedAt ? (c.startedAt | dt) : '—' }}</dd><dt>Completed</dt><dd>{{ c.completedAt ? (c.completedAt | dt) : '—' }}</dd>
            </dl>
            <div>
              <h3>Recipients</h3>
              <div class="wrap-row">@for (s of entries(c.recipients); track s[0]) { <app-chip [value]="s[0]" [label]="s[0] + ': ' + s[1]" /> } @empty { <span class="small muted">None imported yet</span> }</div>
              <h3 style="margin-top:8px">Deliveries</h3>
              <div class="wrap-row">@for (s of entries(c.deliveries); track s[0]) { <app-chip [value]="s[0]" [label]="s[0] + ': ' + s[1]" /> } @empty { <span class="small muted">No deliveries yet</span> }</div>
              <div style="margin-top:6px"><b>Cost:</b> {{ c.cost | money }} · <a routerLink="/explorer" [queryParams]="{ campaignId: c.id }">Open deliveries in explorer</a></div>
            </div>
          </div>
          <div class="panel-head" style="margin-top:10px"><h2>Recipients</h2><span class="spacer"></span>
            <mat-form-field style="width:160px"><mat-label>Status</mat-label><mat-select [(ngModel)]="recipientStatus" (selectionChange)="recipientPage = 1; loadRecipients(c)">
              <mat-option value="">All</mat-option>@for (s of recipientStatuses; track s) { <mat-option [value]="s">{{ s }}</mat-option> }</mat-select></mat-form-field>
            <button mat-icon-button (click)="recipientPage = recipientPage - 1; loadRecipients(c)" [disabled]="recipientPage <= 1" aria-label="Previous page"><mat-icon>chevron_left</mat-icon></button>
            <span class="small">page {{ recipientPage }}</span>
            <button mat-icon-button (click)="recipientPage = recipientPage + 1; loadRecipients(c)" [disabled]="recipients().length < 100" aria-label="Next page"><mat-icon>chevron_right</mat-icon></button>
          </div>
          <div class="table-wrap" style="max-height:360px">
            <table class="data">
              <thead><tr><th class="num">Row</th><th>External id</th><th>Name</th><th>Email</th><th>Mobile</th><th>Status</th><th>Error</th><th>Processed</th></tr></thead>
              <tbody>
                @for (r of recipients(); track r.id) {
                  <tr><td class="num">{{ r.rowNumber }}</td><td>{{ r.externalId ?? '—' }}</td><td>{{ r.name ?? '—' }}</td><td>{{ r.email ?? '—' }}</td><td>{{ r.mobile ?? '—' }}</td>
                    <td><app-chip [value]="r.status" /></td><td class="small error">{{ r.error ?? '' }}</td><td class="small nowrap">{{ r.processedAt ? (r.processedAt | dt) : '—' }}</td></tr>
                } @empty { <tr><td colspan="8" class="muted">No recipients{{ recipientStatus ? ' with status ' + recipientStatus : '' }}. Import a CSV/XLSX with columns email, mobile, name, externalId plus template variables, or target groups / a segment.</td></tr> }
              </tbody>
            </table>
          </div>
        </section>
      }
    </div>`,
  styles: [`
    .tabs { display: flex; flex-wrap: wrap; gap: 4px; margin-bottom: 8px; border-bottom: 1px solid var(--border); }
    .tabs button { all: unset; cursor: pointer; padding: 8px 12px; border-bottom: 2px solid transparent; color: var(--muted); font-weight: 500; }
    .tabs button.on { color: var(--accent); border-bottom-color: var(--accent); }
    .tabs button:focus-visible { outline: 2px solid var(--accent); }
    .detail-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(260px, 1fr)); gap: 8px 16px; align-items: start; }
    .detail-grid .kv { grid-template-columns: 90px 1fr; }
    .tabs .n { font-size: 11px; background: var(--surface-2); border: 1px solid var(--border); border-radius: 8px; padding: 0 6px; margin-left: 4px; color: var(--text); }`],
})
export class Campaigns implements OnInit {
  private api = inject(ApiService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  auth = inject(AuthService);
  lookups = inject(LookupsService);
  id = input<string>();
  tabs = TABS;
  recipientStatuses = ['Pending', 'Queued', 'Skipped', 'Failed'];
  tab = signal(0);
  campaigns = signal<Campaign[]>([]);
  selected = signal<Campaign | null>(null);
  recipients = signal<CampaignRecipient[]>([]);
  loading = signal(false);
  error = signal<unknown>(null);
  importResult = signal<{ imported: number; rejected: number; errors: string[] } | null>(null);
  testResult = signal<SendResponse | null>(null);
  templates = signal<TemplateSummary[]>([]);
  types = signal<NotificationType[]>([]);
  recipientStatus = ''; recipientPage = 1;

  canManage = computed(() => this.auth.can('ManageCampaigns'));
  visible = computed(() => { const s = TABS[this.tab()].statuses; return this.campaigns().filter(c => !s.length || s.includes(c.status)); });

  ngOnInit() {
    this.load();
    if (this.auth.can('ViewTemplates')) {
      this.api.templates({ status: 'Active' }).subscribe({ next: t => this.templates.set(t), error: () => undefined });
      this.api.notificationTypes().subscribe({ next: t => this.types.set(t), error: () => undefined });
    }
  }

  load(selectId?: string) {
    this.loading.set(true);
    this.api.campaigns().subscribe({
      next: c => {
        this.campaigns.set(c); this.loading.set(false);
        const id = selectId ?? this.selected()?.id ?? this.id();
        const found = id ? c.find(x => x.id === id) : null;
        if (found) this.select(found, true);
      },
      error: e => { this.error.set(e); this.loading.set(false); },
    });
  }

  count(statuses: string[]) { return this.campaigns().filter(c => !statuses.length || statuses.includes(c.status)).length; }
  processed(c: Campaign) { return Object.entries(c.recipients).filter(([k]) => k !== 'Pending').reduce((s, [, n]) => s + n, 0); }
  entries(o: Record<string, number>) { return Object.entries(o ?? {}); }
  isOwn(c: Campaign) { return c.createdBy === this.auth.me()?.actorId; }

  select(c: Campaign, keepMessages = false) {
    if (!keepMessages || this.selected()?.id !== c.id) { this.importResult.set(null); this.testResult.set(null); this.recipientPage = 1; }
    this.selected.set(c);
    this.loadRecipients(c);
  }

  loadRecipients(c: Campaign) {
    this.api.campaignRecipients(c.id, this.recipientStatus || undefined, this.recipientPage).subscribe({ next: r => this.recipients.set(r), error: e => this.error.set(e) });
  }

  edit(c?: Campaign) {
    if (!this.templates().length && this.auth.can('ViewTemplates')) this.api.templates({ status: 'Active' }).subscribe(t => this.templates.set(t));
    const open = (draft?: CampaignDraft) => this.dialog.open(CampaignForm, { data: { draft, templates: this.templates(), types: this.types() }, width: '900px', maxWidth: '96vw' }).afterClosed().subscribe((id?: string) => {
      if (id) { this.snack.open('Campaign saved as draft', 'OK', { duration: 3000 }); this.load(id); }
    });
    if (c) this.api.campaign(c.id).subscribe({ next: d => open(d), error: e => this.error.set(e) });
    else open();
  }

  import(c: Campaign, input: HTMLInputElement) {
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;
    this.importResult.set(null);
    this.api.importCampaign(c.id, file).subscribe({
      next: r => { this.importResult.set({ imported: Number(r['imported'] ?? 0), rejected: Number(r['rejected'] ?? 0), errors: (r['errors'] as string[]) ?? [] }); this.load(c.id); },
      error: e => this.error.set(e),
    });
  }

  testSend(c: Campaign) {
    confirmDialog(this.dialog, { title: 'Test send', message: `Send "${c.name}" to one test recipient on ${c.channels.join(', ')}.`, confirmText: 'Send',
      comment: { label: 'Recipient email or mobile (E.164)', required: true, placeholder: 'tester@example.com or +9198…' } }).subscribe(r => {
      if (!r) return;
      const v = r.comment;
      const recipient = v.includes('@') ? { email: v, name: 'Test recipient' } : { mobile: v, whatsApp: v, name: 'Test recipient' };
      this.api.campaignTestSend(c.id, recipient).subscribe({ next: res => this.testResult.set(res), error: e => this.error.set(e) });
    });
  }

  act(c: Campaign, action: 'submit' | 'approve' | 'pause' | 'resume' | 'cancel', title?: string, message?: string, danger = false) {
    const go = () => this.api.campaignAction(c.id, action).subscribe({
      next: r => { this.snack.open(`Campaign ${r.status}`, 'OK', { duration: 3000 }); this.load(c.id); },
      error: e => this.error.set(e),
    });
    if (!title) { go(); return; }
    (confirmDialog(this.dialog, { title, message: message ?? '', danger, confirmText: action === 'cancel' ? 'Cancel campaign' : 'Confirm' }) as Observable<unknown>).subscribe(ok => ok && go());
  }

  suspend(c: Campaign) {
    const on = !c.suspended;
    confirmDialog(this.dialog, { title: on ? 'Emergency hold' : 'Lift hold', danger: on, confirmText: on ? 'Hold campaign' : 'Lift hold',
      message: on ? 'Pending messages are held (not lost) until the hold is lifted.' : 'Held messages resume sending.', comment: { label: 'Reason', required: on } }).subscribe(r => {
      if (!r) return;
      this.api.suspend('campaign', c.id, on, r.comment || undefined).subscribe({ next: () => this.load(c.id), error: e => this.error.set(e) });
    });
  }
}
