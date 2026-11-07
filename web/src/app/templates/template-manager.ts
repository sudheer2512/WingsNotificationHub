import { Component, OnInit, ViewChild, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router } from '@angular/router';
import { Observable } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { problemText } from '../core/errors';
import { LookupsService } from '../core/lookups.service';
import { TemplateDetail, TemplateVersion, TreeNode } from '../core/models';
import { UI } from '../shared/ui';
import { confirmDialog } from '../shared/widgets';
import { CloneTemplateDialog, CreateTemplateDialog, NotificationTypesDialog, TestSendDialog, WhatsAppSubmitDialog } from './template-dialogs';
import { TemplateEditor } from './template-editor';
import { TemplateTree, TreeCtx } from './template-tree';

@Component({
  selector: 'app-template-manager',
  imports: [FormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule, MatMenuModule, MatTabsModule, MatTooltipModule,
    TemplateTree, TemplateEditor, ...UI],
  template: `
    <div class="page">
      <div class="page-head">
        <h1>Template manager</h1><span class="spacer"></span>
        <button mat-stroked-button (click)="openTypes()"><mat-icon>category</mat-icon>Notification types</button>
        @if (auth.can('ManageTemplates')) { <button mat-flat-button (click)="create()"><mat-icon>add</mat-icon>New template</button> }
      </div>
      <div class="split tm">
        <section class="panel tree-panel">
          <mat-form-field class="full"><mat-label>Search templates</mat-label><input matInput [(ngModel)]="search" placeholder="code, name or channel"><mat-icon matSuffix>search</mat-icon></mat-form-field>
          <mat-checkbox [(ngModel)]="showEmpty">Show empty scopes</mat-checkbox>
          <app-loading [on]="treeLoading()" />
          <app-error [error]="treeError()" />
          <div class="list-scroll">
            <app-template-tree [nodes]="tree()" [search]="search" [hideEmpty]="!showEmpty" [selectedId]="selectedId()" (selectTemplate)="select($event)" (selectContainer)="ctx = $event" />
          </div>
        </section>

        <section style="min-width:0">
          <app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />
          @if (detail(); as d) {
            <div class="panel">
              <div class="row wrap-row">
                <h2><code>{{ d.template.code }}</code></h2>
                <app-channel [value]="d.template.channel" /><span class="tag">{{ d.template.locale }}</span><app-chip [value]="d.template.status" />
                @if (d.template.activeVersion) { <span class="small muted">active v{{ d.template.activeVersion }}</span> } <span class="small muted">· latest v{{ d.template.latestVersion }}</span>
                @if (dirty()) { <app-chip tone="warn" icon="edit" label="Unsaved changes" /> }
                <span class="spacer"></span>
                @if (canManage()) {
                  <button mat-flat-button (click)="saveDraft()" [disabled]="busy() || !dirty()"><mat-icon>save</mat-icon>Save draft</button>
                  @if (latest()?.status === 'Draft') { <button mat-stroked-button (click)="submit(latest()!)" [disabled]="busy() || dirty()" [matTooltip]="dirty() ? 'Save the draft first' : 'Send v' + latest()!.versionNumber + ' for review'"><mat-icon>send</mat-icon>Submit</button> }
                }
                <button mat-icon-button [matMenuTriggerFor]="more" aria-label="More actions"><mat-icon>more_vert</mat-icon></button>
                <mat-menu #more="matMenu">
                  @if (canManage()) { <button mat-menu-item (click)="testSend()"><mat-icon>outgoing_mail</mat-icon>Test send</button> }
                  @if (auth.can('ManageTemplates')) { <button mat-menu-item (click)="clone()"><mat-icon>content_copy</mat-icon>Clone / translate</button> }
                  @if (d.template.channel === 'whatsapp' && canManage()) { <button mat-menu-item (click)="waSubmit()"><mat-icon>cloud_upload</mat-icon>Submit to WhatsApp</button> }
                  @if (canApprove() && d.template.status === 'Active') { <button mat-menu-item (click)="retire()"><mat-icon>archive</mat-icon>Retire template</button> }
                  @if (dirty()) { <button mat-menu-item (click)="editor?.reset()"><mat-icon>undo</mat-icon>Discard changes</button> }
                  <button mat-menu-item (click)="reload()"><mat-icon>refresh</mat-icon>Reload</button>
                </mat-menu>
              </div>
              <dl class="kv small" style="margin-top:6px">
                <dt>Name</dt><dd>{{ d.template.name }}</dd>
                @if (d.template.description) { <dt>Description</dt><dd>{{ d.template.description }}</dd> }
                <dt>Scope</dt><dd>{{ lookups.tenantName(d.template.tenantId) }} / {{ d.template.companyId ? lookups.companyName(d.template.companyId) : 'all companies' }} / {{ d.template.applicationId ? lookups.appName(d.template.applicationId) : 'all applications' }}</dd>
                <dt>Category</dt><dd>{{ d.template.category ?? '—' }}</dd>
                <dt>Variables (active)</dt><dd>@for (v of d.template.variables; track v.name) { <span class="tag">{{ v.name }}@if (!v.required) {?}</span> } @empty { — }</dd>
                <dt>Updated</dt><dd>{{ d.template.updatedAt | dt }}</dd>
              </dl>
            </div>

            <mat-tab-group mat-stretch-tabs="false" style="margin-top:8px" [(selectedIndex)]="tab">
              <mat-tab label="Editor">
                <app-template-editor [detail]="d" [canEdit]="canManage()" (dirtyChange)="dirty.set($event)" />
              </mat-tab>
              <mat-tab [label]="'Versions (' + d.versions.length + ')'">
                <div class="table-wrap">
                  <table class="data">
                    <thead><tr><th>Version</th><th>Status</th><th>Created</th><th>Submitted</th><th>Approved</th><th>Note</th><th>Actions</th></tr></thead>
                    <tbody>
                      @for (v of d.versions; track v.id) {
                        <tr [class.selected]="viewing()?.id === v.id">
                          <td><b>v{{ v.versionNumber }}</b>@if (v.versionNumber === d.template.activeVersion) { <span class="tag">active</span> }</td>
                          <td><app-chip [value]="v.status" [tip]="v.rejectionReason ?? null" />
                            @if (v.providerApprovalStatus) { <span class="tag" matTooltip="WhatsApp approval status">WA: {{ v.providerApprovalStatus }}</span> }</td>
                          <td class="nowrap small">{{ v.createdAt | dt }}<br><span class="muted">{{ who(v.createdBy) }}</span></td>
                          <td class="nowrap small">@if (v.submittedAt) { {{ v.submittedAt | dt }}<br><span class="muted">{{ who(v.submittedBy) }}</span> } @else { — }</td>
                          <td class="nowrap small">@if (v.approvedAt) { {{ v.approvedAt | dt }}<br><span class="muted">{{ who(v.approvedBy) }}</span> } @else { — }</td>
                          <td class="small" style="max-width:220px">{{ v.changeNote ?? '' }}@if (v.rejectionReason) { <div class="error">Rejected: {{ v.rejectionReason }}</div> }</td>
                          <td class="nowrap">
                            <button mat-button (click)="viewing.set(viewing()?.id === v.id ? null : v)"><mat-icon>visibility</mat-icon>View</button>
                            @if (v.status === 'Draft' && canManage()) { <button mat-button (click)="submit(v)" [disabled]="busy()"><mat-icon>send</mat-icon>Submit</button> }
                            @if (v.status === 'InReview' && canApprove()) {
                              <button mat-button (click)="review(v, true)" [disabled]="busy() || isOwn(v)" [matTooltip]="isOwn(v) ? 'Maker-checker: you created or submitted this version' : ''"><mat-icon>check</mat-icon>Approve</button>
                              <button mat-button class="danger" (click)="review(v, false)" [disabled]="busy() || isOwn(v)"><mat-icon>close</mat-icon>Reject</button>
                            }
                            @if ((v.status === 'Approved' || v.status === 'Retired') && canApprove()) { <button mat-button (click)="activate(v)" [disabled]="busy()"><mat-icon>bolt</mat-icon>{{ v.status === 'Retired' ? 'Roll back' : 'Activate' }}</button> }
                          </td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
                @if (viewing(); as v) {
                  <section class="panel" style="margin-top:10px">
                    <div class="panel-head"><h2>v{{ v.versionNumber }} content</h2><span class="spacer"></span><button mat-icon-button (click)="viewing.set(null)" aria-label="Close"><mat-icon>close</mat-icon></button></div>
                    @if (v.subject) { <div><span class="small muted">Subject</span> <b>{{ v.subject }}</b></div> }
                    @if (v.components?.title) { <div><span class="small muted">Title</span> <b>{{ v.components.title }}</b></div> }
                    <div class="small muted">Body ({{ v.bodyFormat }})</div>
                    <pre class="json">{{ v.body }}</pre>
                    @if (v.textBody) { <div class="small muted">Text part</div><pre class="json">{{ v.textBody }}</pre> }
                    <dl class="kv small">
                      @if (v.dltTemplateId) { <dt>DLT template id</dt><dd>{{ v.dltTemplateId }}</dd> }
                      @if (v.providerTemplateName) { <dt>Provider template</dt><dd>{{ v.providerTemplateName }} · {{ v.providerTemplateLanguage }} · {{ v.providerCategory }}</dd> }
                      <dt>Variables</dt><dd>@for (x of v.variables; track x.name) { <span class="tag">{{ x.name }}{{ x.required ? '' : '?' }}@if (x.sampleValue) { = {{ x.sampleValue }} }</span> } @empty { — }</dd>
                    </dl>
                  </section>
                }
              </mat-tab>
            </mat-tab-group>
          } @else if (loading()) {
            <app-loading [on]="true" />
          } @else {
            <div class="panel"><app-empty icon="description" text="Select a template" hint="Pick a template in the tree, or create a new one. Templates are scoped tenant → company → application and grouped by category." /></div>
          }
        </section>
      </div>
    </div>`,
  styles: [`
    .tm { grid-template-columns: minmax(260px, 340px) 1fr; }
    .tree-panel .list-scroll { max-height: calc(100vh - 260px); }
    @media (max-width: 1280px) { .tm { grid-template-columns: 1fr; } .tree-panel .list-scroll { max-height: 280px; } }`],
})
export class TemplateManager implements OnInit {
  private api = inject(ApiService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  private router = inject(Router);
  auth = inject(AuthService);
  lookups = inject(LookupsService);
  @ViewChild(TemplateEditor) editor?: TemplateEditor;

  id = input<string>();
  search = '';
  showEmpty = false;
  tab = 0;
  ctx: TreeCtx = {};
  tree = signal<TreeNode[]>([]);
  treeLoading = signal(false);
  treeError = signal<unknown>(null);
  detail = signal<TemplateDetail | null>(null);
  loading = signal(false);
  busy = signal(false);
  error = signal<unknown>(null);
  dirty = signal(false);
  viewing = signal<TemplateVersion | null>(null);
  selectedId = signal<string | null>(null);

  latest = computed(() => this.detail()?.versions[0] ?? null);
  canManage = computed(() => this.auth.can('ManageTemplates'));
  canApprove = computed(() => this.auth.can('ApproveTemplates'));

  ngOnInit() {
    this.loadTree();
    const id = this.id();
    if (id) this.select(id);
  }

  loadTree() {
    this.treeLoading.set(true);
    this.api.templateTree().subscribe({
      next: t => { this.tree.set(t); this.treeLoading.set(false); this.treeError.set(null); },
      error: e => { this.treeError.set(e); this.treeLoading.set(false); },
    });
  }

  select(id: string) {
    if (this.dirty() && id !== this.selectedId() && !window.confirm('Discard unsaved changes to this template?')) return;
    this.selectedId.set(id);
    this.viewing.set(null);
    this.router.navigate([], { queryParams: { id }, replaceUrl: true });
    this.load(id);
  }

  load(id = this.selectedId()) {
    if (!id) return;
    this.loading.set(true);
    this.api.template(id).subscribe({
      next: d => { this.detail.set(d); this.loading.set(false); this.error.set(null); this.dirty.set(false); },
      error: e => { this.error.set(e); this.loading.set(false); this.detail.set(null); },
    });
  }

  reload() { this.load(); this.loadTree(); }

  who(actor?: string | null) {
    if (!actor) return '';
    if (actor === this.auth.me()?.actorId) return 'you';
    return this.detail()?.actors?.[actor] ?? (actor.startsWith('credential:') ? 'API client' : 'Unknown user');
  }

  isOwn(v: TemplateVersion) { const me = this.auth.me()?.actorId; return !!me && (v.createdBy === me || v.submittedBy === me); }

  private run<T>(obs: Observable<T>, ok: string, after?: (r: T) => void) {
    this.busy.set(true); this.error.set(null);
    obs.subscribe({
      next: r => { this.busy.set(false); this.snack.open(ok, 'OK', { duration: 3500 }); after?.(r); this.reload(); },
      error: e => { this.busy.set(false); this.error.set(e); },
    });
  }

  saveDraft() {
    const d = this.detail(), ed = this.editor;
    if (!d || !ed) return;
    this.run(this.api.saveDraft(d.template.id, ed.request()), 'Draft saved', r => { ed.markSaved(); this.snack.open(`Saved v${r.versionNumber} (${r.status})`, 'OK', { duration: 3500 }); });
  }

  submit(v: TemplateVersion) {
    const d = this.detail()!;
    confirmDialog(this.dialog, { title: `Submit v${v.versionNumber} for review`, message: 'The version becomes read-only and an approver (not you) must approve it before it can be activated.', confirmText: 'Submit' })
      .subscribe(r => { if (r) this.run(this.api.submitVersion(d.template.id, v.versionNumber), `v${v.versionNumber} submitted for review`); });
  }

  review(v: TemplateVersion, approve: boolean) {
    const d = this.detail()!;
    confirmDialog(this.dialog, {
      title: `${approve ? 'Approve' : 'Reject'} v${v.versionNumber}`, message: approve ? 'Approving also activates this version: new notifications will use it immediately.' : 'The author can revise the draft and resubmit.',
      confirmText: approve ? 'Approve & activate' : 'Reject', danger: !approve, comment: { label: approve ? 'Comment (optional)' : 'Reason for rejection', required: !approve },
    }).subscribe(r => { if (r) this.run(this.api.reviewVersion(d.template.id, v.versionNumber, approve, r.comment || undefined, true), approve ? 'Approved and activated' : 'Rejected'); });
  }

  activate(v: TemplateVersion) {
    const d = this.detail()!;
    confirmDialog(this.dialog, { title: `Activate v${v.versionNumber}`, message: 'The currently active version is retired and new notifications use this one.', confirmText: 'Activate' })
      .subscribe(r => { if (r) this.run(this.api.activateVersion(d.template.id, v.versionNumber), `v${v.versionNumber} is now active`); });
  }

  retire() {
    const d = this.detail()!;
    confirmDialog(this.dialog, { title: `Retire ${d.template.code}`, message: 'Notifications that use this template will fail until a version is activated again.', confirmText: 'Retire', danger: true })
      .subscribe(r => { if (r) this.run(this.api.retireTemplate(d.template.id), 'Template retired'); });
  }

  create() {
    this.dialog.open(CreateTemplateDialog, { data: { ...this.ctx }, width: '760px', maxWidth: '96vw' }).afterClosed().subscribe((id?: string) => {
      if (id) { this.loadTree(); this.select(id); this.snack.open('Template created as a draft', 'OK', { duration: 3000 }); }
    });
  }

  clone() {
    const d = this.detail();
    if (!d) return;
    this.dialog.open(CloneTemplateDialog, { data: d.template, width: '760px', maxWidth: '96vw' }).afterClosed().subscribe((id?: string) => {
      if (id) { this.loadTree(); this.select(id); this.snack.open('Clone created as a draft', 'OK', { duration: 3000 }); }
    });
  }

  testSend() {
    const d = this.detail();
    if (d) this.dialog.open(TestSendDialog, { data: { template: d.template, versions: d.versions }, width: '720px', maxWidth: '96vw' });
  }

  waSubmit() {
    const d = this.detail();
    if (d) this.dialog.open(WhatsAppSubmitDialog, { data: { template: d.template, versions: d.versions }, width: '520px', maxWidth: '96vw' }).afterClosed().subscribe(ok => ok && this.load());
  }

  openTypes() { this.dialog.open(NotificationTypesDialog, { width: '980px', maxWidth: '96vw' }); }

  problem(e: unknown) { return problemText(e); }
}
