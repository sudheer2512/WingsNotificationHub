import { Component, DestroyRef, OnInit, ViewChild, computed, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatMenuModule } from '@angular/material/menu';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatSelectModule } from '@angular/material/select';
import { MatSidenav, MatSidenavModule } from '@angular/material/sidenav';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTabsModule } from '@angular/material/tabs';
import { MatTooltipModule } from '@angular/material/tooltip';
import { ActivatedRoute, Router } from '@angular/router';
import { Subscription, filter } from 'rxjs';
import { ApiService, DeliveryQuery } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { problemText } from '../core/errors';
import { DELIVERY_STATUSES, DeliveryDetail, ExplorerRow, Timeline } from '../core/models';
import { RealtimeService } from '../core/realtime.service';
import { FilterModel, ScopeFilters, emptyFilters, filterQuery } from '../shared/filters';
import { Tone, UI, humanize, toneOf } from '../shared/ui';
import { confirmDialog } from '../shared/widgets';

type SortKey = '' | 'createdAt' | 'attempts' | 'cost';

@Component({
  selector: 'app-explorer',
  imports: [FormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule, MatMenuModule, MatPaginatorModule, MatSelectModule,
    MatSidenavModule, MatSlideToggleModule, MatTabsModule, MatTooltipModule, ScopeFilters, ...UI],
  template: `
    <mat-sidenav-container class="drawer-host" (backdropClick)="close()">
      <mat-sidenav #drawer position="end" mode="over" class="drawer" (closedStart)="onDrawerClosed()">
        @if (detail(); as x) {
          <div class="drawer-head">
            <button mat-icon-button (click)="close()" aria-label="Close"><mat-icon>close</mat-icon></button>
            <div style="min-width:0">
              <div class="row"><app-channel [value]="x.delivery.channel" /><app-chip [value]="x.delivery.status" />
                @if (x.awaitingOperator) { <app-chip tone="bad" icon="support_agent" label="Awaiting operator" /> }
                @if (x.deadLettered) { <app-chip tone="bad" icon="report" label="Dead-lettered" /> }</div>
              <div class="small muted ellipsis">Delivery <code>{{ x.delivery.deliveryId }}</code></div>
            </div>
            <span class="spacer"></span>
            @if (canRetry()) { <button mat-flat-button (click)="retry()" [disabled]="acting()"><mat-icon>replay</mat-icon>Retry</button> }
            @if (canResolve()) {
              <button mat-flat-button [matMenuTriggerFor]="resolveMenu" [disabled]="acting()"><mat-icon>gavel</mat-icon>Resolve</button>
              <mat-menu #resolveMenu="matMenu">
                <button mat-menu-item (click)="resolve('resend')"><mat-icon>send</mat-icon>Resend (accept duplicate risk)</button>
                <button mat-menu-item (click)="resolve('mark_sent')"><mat-icon>done</mat-icon>Mark as sent</button>
                <button mat-menu-item (click)="resolve('mark_failed')"><mat-icon>close</mat-icon>Mark as failed</button>
              </mat-menu>
            }
          </div>
          <div class="drawer-body">
            <app-error [error]="detailError()" />
            @if (x.delivery.status === 'Unknown') {
              <div class="callout bad">The provider outcome is unknown (for example a timeout after submit). Resending may produce a duplicate; decide with <b>Resolve</b>.</div>
            }
            <dl class="kv">
              <dt>Created</dt><dd>{{ x.delivery.createdAt | dt }}</dd>
              <dt>Recipient</dt><dd>{{ x.delivery.recipient }}</dd>
              <dt>Notification</dt><dd><code>{{ x.request.id }}</code> <button mat-button (click)="openHistory(x.request.id)"><mat-icon>timeline</mat-icon>Full history</button></dd>
              <dt>Type / template</dt><dd>{{ x.request.notificationTypeCode ?? '—' }} · {{ x.request.templateCode ?? '—' }}@if (x.delivery.templateVersion) { v{{ x.delivery.templateVersion }} }</dd>
              <dt>Source</dt><dd>{{ x.request.source | human }}@if (x.request.actorName || x.request.actorId) { · {{ x.request.actorName ?? x.request.actorId }} }</dd>
              <dt>Provider</dt><dd>{{ x.delivery.provider ?? '—' }}@if (x.delivery.providerMessageId) { · <code>{{ x.delivery.providerMessageId }}</code> }</dd>
              <dt>Attempts</dt><dd>{{ x.delivery.attempts }}@if (x.delivery.nextAttemptAt) { · next {{ x.delivery.nextAttemptAt | ago }} }</dd>
              <dt>Cost</dt><dd>{{ x.delivery.cost != null ? (x.delivery.cost | money:(x.delivery.currency ?? 'INR')) : '—' }}</dd>
              @if (x.delivery.failureCode || x.delivery.failureReason) { <dt>Failure</dt><dd class="error">{{ x.delivery.failureCode }} {{ x.delivery.failureReason }}</dd> }
              @if (x.delivery.fallbackOfDeliveryId) { <dt>Fallback of</dt><dd><a href="" (click)="$event.preventDefault(); open(x.delivery.fallbackOfDeliveryId!)"><code>{{ x.delivery.fallbackOfDeliveryId | short }}</code></a></dd> }
              @if (x.delivery.escalationOfDeliveryId) { <dt>Escalation of</dt><dd><a href="" (click)="$event.preventDefault(); open(x.delivery.escalationOfDeliveryId!)"><code>{{ x.delivery.escalationOfDeliveryId | short }}</code></a></dd> }
              @if (x.request.correlationId) { <dt>Correlation</dt><dd><code>{{ x.request.correlationId }}</code></dd> }
              @if (x.request.campaignId) { <dt>Campaign</dt><dd><code>{{ x.request.campaignId }}</code></dd> }
              @if (x.request.ruleId) { <dt>Rule</dt><dd><code>{{ x.request.ruleId }}</code></dd> }
            </dl>

            <mat-tab-group style="margin-top:8px" mat-stretch-tabs="false">
              <mat-tab label="Timeline">
                <ul class="timeline">
                  @for (t of timeline(); track $index) {
                    <li [attr.data-tone]="t.tone">
                      <div class="row"><b>{{ t.title }}</b><span class="spacer"></span><span class="small muted" [title]="t.at | dt">{{ t.at | dt }}</span></div>
                      @if (t.detail) { <div class="small pre">{{ t.detail }}</div> }
                      @if (t.meta) { <div class="small muted">{{ t.meta }}</div> }
                    </li>
                  } @empty { <li class="muted">No events recorded</li> }
                </ul>
              </mat-tab>
              <mat-tab [label]="'Attempts (' + x.attempts.length + ')'">
                @for (a of x.attempts; track a.id) {
                  <div class="panel">
                    <div class="row wrap-row"><b>#{{ a.attemptNumber }}</b> <app-chip [value]="a.outcome ?? 'In progress'" [tone]="a.outcome === 'Accepted' ? 'good' : null" /> <span>{{ a.provider }}</span>
                      <span class="spacer"></span><span class="small muted">{{ a.startedAt | dt }}@if (a.durationMs != null) { · {{ a.durationMs }} ms }</span></div>
                    <dl class="kv small" style="margin-top:6px">
                      @if (a.responseCode) { <dt>Response code</dt><dd><code>{{ a.responseCode }}</code></dd> }
                      @if (a.providerMessageId) { <dt>Provider message id</dt><dd><code>{{ a.providerMessageId }}</code></dd> }
                      @if (a.errorCode || a.errorMessage) { <dt>Error</dt><dd class="error">{{ a.errorCode }} {{ a.errorMessage }}</dd> }
                      @if (a.cost != null) { <dt>Cost</dt><dd>{{ a.cost | money }}</dd> }
                    </dl>
                    @if (a.responseSummary) { <div class="small muted" style="margin-top:4px">Provider response (sanitized)</div><pre class="json">{{ pretty(a.responseSummary) }}</pre> }
                  </div>
                } @empty { <app-empty icon="history" text="No provider attempts" hint="Suppressed, cancelled and scheduled deliveries have no attempts." /> }
              </mat-tab>
              <mat-tab label="Content">
                @if (x.content; as c) {
                  @if (c.subject) { <div><span class="muted small">Subject</span><div><b>{{ c.subject }}</b></div></div> }
                  @if (c.title) { <div><span class="muted small">Title</span><div><b>{{ c.title }}</b></div></div> }
                  @if (c.whatsAppTemplate) { <div class="small muted">WhatsApp template <code>{{ c.whatsAppTemplate }}</code></div> }
                  @if (c.format === 'Html' && c.body) {
                    <iframe class="preview-frame" sandbox="" [attr.srcdoc]="c.body" title="Rendered HTML"></iframe>
                    @if (c.textBody) { <div class="small muted" style="margin-top:6px">Plain-text part</div><pre class="json">{{ c.textBody }}</pre> }
                  } @else { <pre class="json">{{ c.body ?? c.textBody ?? '' }}</pre> }
                } @else if (x.contentPurgedAt) {
                  <app-empty icon="auto_delete" text="Content purged" [hint]="'Message bodies are deleted after the retention period (' + (x.contentPurgedAt | dt) + ').'" />
                } @else if (x.contentAvailable) {
                  <app-empty icon="visibility_off" text="Content hidden" hint="You do not have permission to view message content." />
                } @else { <app-empty icon="description" text="No rendered content stored" /> }
              </mat-tab>
            </mat-tab-group>

            @if (history(); as h) {
              <section class="panel" style="margin-top:12px">
                <div class="panel-head"><h2>Notification history</h2><span class="spacer"></span><button mat-icon-button (click)="history.set(null)" aria-label="Close history"><mat-icon>close</mat-icon></button></div>
                <div class="table-wrap">
                  <table class="data">
                    <thead><tr><th>Channel</th><th>Recipient</th><th>Status</th><th class="num">Attempts</th><th>Created</th></tr></thead>
                    <tbody>
                      @for (d of h.deliveries; track d.deliveryId) {
                        <tr class="clickable" [class.selected]="d.deliveryId === x.delivery.deliveryId" (click)="open(d.deliveryId)">
                          <td><app-channel [value]="d.channel" /></td><td class="ellipsis" style="max-width:200px">{{ d.recipient }}</td><td><app-chip [value]="d.status" /></td>
                          <td class="num">{{ d.attempts }}</td><td class="nowrap">{{ d.createdAt | dt }}</td>
                        </tr>
                      }
                    </tbody>
                  </table>
                </div>
                <div class="small muted" style="margin-top:6px">{{ h.attempts.length }} attempts and {{ h.events.length }} events across {{ h.deliveries.length }} deliveries.</div>
              </section>
            }
          </div>
        } @else if (detailLoading()) {
          <div class="drawer-body"><app-loading [on]="true" /></div>
        } @else if (detailError()) {
          <div class="drawer-body"><app-error [error]="detailError()" /></div>
        }
      </mat-sidenav>

      <mat-sidenav-content>
        <div class="page">
          <div class="page-head">
            <h1>Notification explorer</h1>
            <span class="sub">{{ total() | num }} deliveries</span>
            <span class="spacer"></span>
            <mat-slide-toggle [(ngModel)]="live" matTooltip="Reload the first page when delivery statuses change">Live</mat-slide-toggle>
            <button mat-stroked-button [matMenuTriggerFor]="exportMenu" [disabled]="exporting()"><mat-icon>download</mat-icon>Export</button>
            <mat-menu #exportMenu="matMenu">
              <button mat-menu-item (click)="export('csv')">CSV</button>
              <button mat-menu-item (click)="export('xlsx')">Excel (.xlsx)</button>
            </mat-menu>
            <button mat-stroked-button (click)="load()"><mat-icon>refresh</mat-icon>Refresh</button>
          </div>
          <app-scope-filters [(value)]="scope" (changed)="search()" />
          <div class="filters">
            <mat-form-field>
              <mat-label>Status</mat-label>
              <mat-select [(ngModel)]="status" (selectionChange)="search()">
                <mat-option value="">Any status</mat-option>
                @for (s of statuses; track s) { <mat-option [value]="s">{{ s | human }}</mat-option> }
              </mat-select>
            </mat-form-field>
            <mat-form-field class="wide">
              <mat-label>Recipient (exact email or phone)</mat-label>
              <input matInput [(ngModel)]="recipient" (keyup.enter)="search()" (blur)="recipient !== lastRecipient && search()">
            </mat-form-field>
            <mat-form-field>
              <mat-label>Notification type</mat-label>
              <input matInput [(ngModel)]="notificationType" (keyup.enter)="search()" (blur)="notificationType !== lastType && search()" placeholder="INVOICE_CREATED">
            </mat-form-field>
            <mat-form-field class="wide">
              <mat-label>Notification id</mat-label>
              <input matInput [(ngModel)]="notificationId" (keyup.enter)="search()" (blur)="notificationId !== lastNotification && search()">
            </mat-form-field>
            <mat-checkbox [(ngModel)]="awaitingOperator" (change)="search()">Awaiting operator</mat-checkbox>
            @if (campaignId()) { <button mat-stroked-button (click)="clearCampaign()" matTooltip="Showing one campaign's deliveries"><mat-icon>campaign</mat-icon>Campaign {{ campaignId() | short }}<mat-icon iconPositionEnd>close</mat-icon></button> }
          </div>
          <app-loading [on]="loading()" />
          <app-error [error]="error()" />
          <div class="table-wrap">
            <table class="data">
              <thead>
                <tr>
                  <th><button class="sort" (click)="toggleSort('createdAt')">Date / time <mat-icon>{{ sort === 'createdAt' ? 'arrow_upward' : sort === '' ? 'arrow_downward' : 'unfold_more' }}</mat-icon></button></th>
                  <th>Notification</th><th>Application</th><th>Company</th><th>Recipient</th><th>Type</th><th>Channel</th><th>Provider</th><th>Status</th>
                  <th class="num"><button class="sort" (click)="toggleSort('attempts')">Attempts <mat-icon>{{ sort === 'attempts' ? 'arrow_downward' : 'unfold_more' }}</mat-icon></button></th>
                  <th class="num"><button class="sort" (click)="toggleSort('cost')">Cost <mat-icon>{{ sort === 'cost' ? 'arrow_downward' : 'unfold_more' }}</mat-icon></button></th>
                </tr>
              </thead>
              <tbody>
                @for (r of rows(); track r.deliveryId) {
                  <tr class="clickable" [class.selected]="r.deliveryId === selectedId()" (click)="open(r.deliveryId)" tabindex="0" (keyup.enter)="open(r.deliveryId)">
                    <td class="nowrap" [title]="r.createdAt | dt">{{ r.createdAt | dt }}</td>
                    <td><code class="small" [title]="r.notificationId">{{ r.notificationId | short }}</code></td>
                    <td class="ellipsis" style="max-width:140px">{{ r.application }}</td>
                    <td class="ellipsis" style="max-width:140px">{{ r.company }}</td>
                    <td class="ellipsis" style="max-width:200px" [title]="r.recipient">{{ r.recipient }}</td>
                    <td class="small">{{ r.notificationType ?? '—' }}</td>
                    <td><app-channel [value]="r.channel" /></td>
                    <td class="ellipsis" style="max-width:150px">{{ r.provider ?? '—' }}</td>
                    <td><app-chip [value]="r.status" [tip]="r.statusReason ?? r.failureCode ?? null" />@if (r.awaitingOperator) { <mat-icon class="small-ic" matTooltip="Awaiting operator decision">support_agent</mat-icon> }</td>
                    <td class="num">{{ r.attempts }}</td>
                    <td class="num">{{ r.cost != null ? (r.cost | money:(r.currency ?? 'INR')) : '—' }}</td>
                  </tr>
                } @empty {
                  @if (!loading()) { <tr><td colspan="11"><app-empty icon="manage_search" text="No deliveries match" hint="Widen the filters or send a notification." /></td></tr> }
                }
              </tbody>
            </table>
          </div>
          <mat-paginator [length]="total()" [pageIndex]="page - 1" [pageSize]="pageSize" [pageSizeOptions]="[25, 50, 100, 200]" (page)="onPage($event)" showFirstLastButtons />
        </div>
      </mat-sidenav-content>
    </mat-sidenav-container>`,
  styles: [`
    button.sort { all: unset; cursor: pointer; display: inline-flex; align-items: center; gap: 2px; }
    button.sort:focus-visible { outline: 2px solid var(--accent); }
    button.sort mat-icon { font-size: 14px; width: 14px; height: 14px; }
    .small-ic { font-size: 16px; width: 16px; height: 16px; vertical-align: middle; color: var(--bad); }
    mat-paginator { background: transparent; }`],
})
export class Explorer implements OnInit {
  private api = inject(ApiService);
  private auth = inject(AuthService);
  private rt = inject(RealtimeService);
  private snack = inject(MatSnackBar);
  private dialog = inject(MatDialog);
  private router = inject(Router);
  private route = inject(ActivatedRoute);
  private destroyRef = inject(DestroyRef);
  @ViewChild('drawer', { static: true }) drawer!: MatSidenav;

  /** Query params (deep links from the dashboard, campaigns and live events). */
  delivery = input<string>();
  campaignId = signal<string | null>(null);

  statuses = DELIVERY_STATUSES;
  scope: FilterModel = emptyFilters();
  status = ''; recipient = ''; notificationType = ''; notificationId = ''; awaitingOperator = false;
  lastRecipient = ''; lastType = ''; lastNotification = '';
  sort: SortKey = '';
  page = 1; pageSize = 50;
  live = true;

  rows = signal<ExplorerRow[]>([]);
  total = signal(0);
  loading = signal(false);
  error = signal<unknown>(null);
  exporting = signal(false);
  selectedId = signal<string | null>(null);
  detail = signal<DeliveryDetail | null>(null);
  detailLoading = signal(false);
  detailError = signal<unknown>(null);
  history = signal<Timeline | null>(null);
  acting = signal(false);
  private detailSub?: Subscription;

  canRetry = computed(() => { const s = this.detail()?.delivery.status; return this.auth.can('RetryDeliveries') && (s === 'Failed' || s === 'Expired' || s === 'RetryPending'); });
  canResolve = computed(() => this.auth.can('RetryDeliveries') && this.detail()?.delivery.status === 'Unknown');

  timeline = computed(() => {
    const x = this.detail();
    if (!x) return [];
    const items: { at: string; title: string; detail?: string | null; meta?: string; tone: Tone }[] = [];
    for (const e of x.events) items.push({ at: e.recordedAt, title: e.status ? `${humanType(e.type)} → ${humanize(e.status)}` : humanType(e.type), detail: e.detail, meta: `source: ${e.source}`, tone: e.status ? toneOf(e.status) : 'info' });
    for (const a of x.attempts) items.push({ at: a.startedAt, title: `Attempt ${a.attemptNumber} via ${a.provider ?? 'provider'}: ${a.outcome ? humanize(a.outcome) : 'in progress'}`,
      detail: a.errorMessage ?? null, meta: [a.responseCode && `code ${a.responseCode}`, a.durationMs != null && `${a.durationMs} ms`].filter(Boolean).join(' · '),
      tone: a.outcome === 'Accepted' ? 'good' : a.outcome === 'TransientFailure' || a.outcome === 'Throttled' ? 'warn' : a.outcome ? 'bad' : 'info' });
    return items.sort((a, b) => a.at.localeCompare(b.at));
  });

  ngOnInit() {
    const qp = this.route.snapshot.queryParamMap;
    this.status = qp.get('status') ?? '';
    this.awaitingOperator = qp.get('awaitingOperator') === 'true';
    this.notificationId = qp.get('notificationId') ?? '';
    this.campaignId.set(qp.get('campaignId'));
    this.load();
    const d = this.delivery();
    if (d) this.open(d);
    this.rt.changes(2000, ['delivery']).pipe(takeUntilDestroyed(this.destroyRef), filter(() => this.live && this.page === 1)).subscribe(() => this.load(true));
    this.route.queryParamMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(m => { const id = m.get('delivery'); if (id && id !== this.selectedId()) this.open(id); });
  }

  private query(): DeliveryQuery {
    return {
      ...filterQuery(this.scope), status: this.status || undefined, recipient: this.recipient.trim() || undefined, notificationType: this.notificationType.trim().toUpperCase() || undefined,
      notificationId: this.notificationId.trim() || undefined, campaignId: this.campaignId() ?? undefined,
      awaitingOperator: this.awaitingOperator || undefined, sort: this.sort || undefined,
    };
  }

  search() { this.page = 1; this.lastRecipient = this.recipient; this.lastType = this.notificationType; this.lastNotification = this.notificationId; this.load(); }

  load(silent = false) {
    if (!silent) this.loading.set(true);
    this.api.deliveries({ ...this.query(), page: this.page, pageSize: this.pageSize }).subscribe({
      next: r => { this.rows.set(r.items); this.total.set(r.total); this.error.set(null); this.loading.set(false); },
      error: e => { this.error.set(e); this.loading.set(false); },
    });
  }

  clearCampaign() {
    this.campaignId.set(null);
    this.router.navigate([], { queryParams: { campaignId: null }, queryParamsHandling: 'merge', replaceUrl: true });
    this.search();
  }

  onPage(e: PageEvent) { this.page = e.pageIndex + 1; this.pageSize = e.pageSize; this.load(); }

  toggleSort(k: SortKey) {
    this.sort = k === 'createdAt' ? (this.sort === 'createdAt' ? '' : 'createdAt') : (this.sort === k ? '' : k);
    this.page = 1;
    this.load();
  }

  export(format: 'csv' | 'xlsx') {
    this.exporting.set(true);
    this.api.download('/deliveries/export', { ...this.query(), format }, `deliveries.${format}`).subscribe({
      next: name => { this.exporting.set(false); this.snack.open(`Downloaded ${name} (addresses masked)`, 'OK', { duration: 4000 }); },
      error: e => { this.exporting.set(false); this.snack.open(problemText(e), 'OK', { duration: 8000, panelClass: 'snack-error' }); },
    });
  }

  open(id: string) {
    this.selectedId.set(id);
    if (this.detail()?.delivery.deliveryId !== id) { this.detail.set(null); }
    this.detailError.set(null);
    this.detailLoading.set(true);
    this.drawer.open();
    this.router.navigate([], { queryParams: { delivery: id }, queryParamsHandling: 'merge', replaceUrl: true });
    this.fetchDetail(id);
    this.detailSub?.unsubscribe();
    this.detailSub = this.rt.deliveryUpdates(id).subscribe(() => this.fetchDetail(id));
  }

  private fetchDetail(id: string) {
    this.api.delivery(id).subscribe({
      next: d => { if (this.selectedId() === id) { this.detail.set(d); this.detailLoading.set(false); } },
      error: e => { this.detailError.set(e); this.detailLoading.set(false); },
    });
  }

  openHistory(notificationId: string) {
    this.api.history(notificationId).subscribe({ next: h => this.history.set(h), error: e => this.snack.open(problemText(e), 'OK', { duration: 6000 }) });
  }

  close() { this.drawer.close(); }

  onDrawerClosed() {
    this.detailSub?.unsubscribe();
    this.selectedId.set(null);
    this.detail.set(null);
    this.history.set(null);
    this.router.navigate([], { queryParams: { delivery: null }, queryParamsHandling: 'merge', replaceUrl: true });
  }

  retry() {
    const id = this.selectedId();
    if (!id) return;
    this.acting.set(true);
    this.api.retryDelivery(id).subscribe({
      next: r => { this.acting.set(false); this.snack.open(`Retry scheduled (${r.status}). Earlier attempts stay on the timeline.`, 'OK', { duration: 4000 }); this.fetchDetail(id); this.load(true); },
      error: e => { this.acting.set(false); this.detailError.set(e); },
    });
  }

  resolve(action: 'resend' | 'mark_sent' | 'mark_failed') {
    const id = this.selectedId();
    if (!id) return;
    const text = { resend: 'Resend this message? The provider may already have delivered it, so the recipient could get a duplicate.',
      mark_sent: 'Record that the provider accepted this message (no resend).', mark_failed: 'Record this delivery as failed (no resend).' }[action];
    confirmDialog(this.dialog, { title: 'Resolve unknown outcome', message: text, confirmText: action === 'resend' ? 'Resend' : 'Confirm', danger: action === 'resend',
      comment: { label: 'Comment for the audit trail' } }).subscribe(r => {
      if (!r) return;
      this.acting.set(true);
      this.api.resolveDelivery(id, action, r.comment || undefined).subscribe({
        next: res => { this.acting.set(false); this.snack.open(`Resolved: ${res.status}`, 'OK', { duration: 4000 }); this.fetchDetail(id); this.load(true); },
        error: e => { this.acting.set(false); this.detailError.set(e); },
      });
    });
  }

  pretty(s: string) { try { return JSON.stringify(JSON.parse(s), null, 2); } catch { return s; } }
}

function humanType(t: string) { return t.replace(/_/g, ' ').replace(/^./, c => c.toUpperCase()); }
