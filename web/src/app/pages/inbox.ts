import { Component, DestroyRef, ElementRef, OnInit, ViewChild, computed, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router, RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { LookupsService } from '../core/lookups.service';
import { ConversationMessage, ConversationSummary } from '../core/models';
import { RealtimeService } from '../core/realtime.service';
import { UI, parseDate } from '../shared/ui';

@Component({
  selector: 'app-inbox',
  imports: [FormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, MatTooltipModule, RouterLink, ...UI],
  template: `
    <div class="page">
      <div class="page-head">
        <h1>Communication inbox</h1><span class="sub">Customer replies on WhatsApp, SMS and email</span><span class="spacer"></span>
        <button mat-stroked-button (click)="load()"><mat-icon>refresh</mat-icon>Refresh</button>
        @if (auth.isPlatformAdmin()) { <button mat-stroked-button (click)="toggleSimulate()" matTooltip="Development helper: POST /api/v1/dev/simulate-inbound"><mat-icon>science</mat-icon>Simulate reply</button> }
      </div>
      @if (simulateOpen()) {
        <section class="panel" style="margin-bottom:12px">
          <div class="form-grid">
            <mat-form-field><mat-label>Mock provider account</mat-label>
              <mat-select [(ngModel)]="sim.providerAccountId">@for (p of mockProviders(); track p.id) { <mat-option [value]="p.id">{{ p.name }} ({{ p.channel }})</mat-option> }</mat-select></mat-form-field>
            <mat-form-field><mat-label>From (customer address)</mat-label><input matInput [(ngModel)]="sim.from" placeholder="+919812345678"></mat-form-field>
            <mat-form-field class="span-2"><mat-label>Message</mat-label><input matInput [(ngModel)]="sim.body"></mat-form-field>
          </div>
          <button mat-flat-button (click)="simulate()" [disabled]="!sim.providerAccountId || !sim.from || !sim.body">Send simulated reply</button>
          <span class="small muted"> Development endpoint; only available when the API runs with dev endpoints enabled.</span>
        </section>
      }
      <app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />
      <div class="split inbox">
        <section class="panel threads">
          <div class="filters">
            <mat-form-field style="width:100%"><mat-label>Search address or name</mat-label><input matInput [(ngModel)]="q" (keyup.enter)="load()"><mat-icon matSuffix>search</mat-icon></mat-form-field>
            <mat-form-field style="width:140px"><mat-label>Channel</mat-label>
              <mat-select [(ngModel)]="channel" (selectionChange)="load()"><mat-option value="">All</mat-option>
                <mat-option value="whatsapp">WhatsApp</mat-option><mat-option value="sms">SMS</mat-option><mat-option value="email">Email</mat-option></mat-select></mat-form-field>
            <mat-checkbox [(ngModel)]="unread" (change)="load()">Unread only</mat-checkbox>
          </div>
          <app-loading [on]="loading()" />
          <div class="list-scroll">
            @for (t of threads(); track t.id) {
              <div class="thread" [class.selected]="t.id === selectedId()" [class.unread]="t.unreadCount > 0" (click)="open(t.id)" tabindex="0" (keyup.enter)="open(t.id)">
                <div class="row"><app-channel [value]="t.channel" /><b class="ellipsis">{{ t.contactName || t.externalAddress }}</b><span class="spacer"></span><span class="small muted nowrap">{{ t.lastMessageAt | ago }}</span></div>
                <div class="row"><span class="small muted ellipsis" style="flex:1">{{ t.lastMessage ?? '' }}</span>
                  @if (t.unreadCount) { <span class="badge" [attr.aria-label]="t.unreadCount + ' unread'">{{ t.unreadCount }}</span> }</div>
                <div class="row small">
                  @if (t.channel === 'whatsapp') { <app-chip [tone]="t.windowOpen ? 'good' : 'off'" [icon]="t.windowOpen ? 'lock_open' : 'lock'" [label]="t.windowOpen ? '24 h window open' : 'Window closed'" /> }
                  @if (t.routing) { <app-chip [value]="t.routing" [tip]="'Routing to the application: ' + t.routing" /> }
                </div>
              </div>
            } @empty { <app-empty icon="forum" text="No conversations" hint="Customer replies to notifications appear here." /> }
          </div>
        </section>

        <section class="panel convo">
          @if (current(); as c) {
            <div class="panel-head">
              <app-channel [value]="c.summary.channel" /><h2>{{ c.summary.contactName || c.summary.externalAddress }}</h2>
              @if (c.summary.contactName) { <span class="small muted">{{ c.summary.externalAddress }}</span> }
              <span class="spacer"></span>
              @if (c.summary.channel === 'whatsapp') {
                <app-chip [tone]="windowOpen() ? 'good' : 'warn'" [icon]="windowOpen() ? 'lock_open' : 'lock'"
                  [label]="windowOpen() ? 'Window closes ' + windowCloses() : 'Window closed'"
                  [tip]="'WhatsApp allows free-form replies only within 24 hours of the customer\\'s last message; otherwise send an approved template.'" />
              }
            </div>
            <div class="small muted" style="margin-bottom:6px">{{ lookups.companyName(c.summary.companyId) }} · {{ lookups.appName(c.summary.applicationId) }}</div>
            <div class="bubble-list" #scroller>
              @for (m of c.messages; track m.id) {
                <div class="bubble" [class.out]="m.direction === 'outbound'">
                  <div class="pre">{{ m.body ?? '' }}</div>
                  @for (a of m.attachments; track a.id) { <div class="small"><mat-icon class="inline-ic">attach_file</mat-icon>{{ a.fileName }} ({{ a.size | num }} B)</div> }
                  <div class="meta">
                    <span [title]="m.at | dt">{{ m.at | dt }}</span>
                    @if (m.direction === 'outbound') {
                      <app-chip [value]="m.status" [tip]="m.detail ?? null" />
                      @if (m.type !== 'Conversation') { <span>{{ m.type }}</span> }
                      @if (auth.can('ViewNotifications')) { <a routerLink="/explorer" [queryParams]="{ delivery: m.id }">details</a> }
                    } @else {
                      <app-chip [value]="m.status" [label]="'Routing: ' + m.status" [tip]="m.detail ?? null" />
                      @if (m.type && m.type !== 'text') { <span>{{ m.type }}</span> }
                    }
                  </div>
                </div>
              } @empty { <app-empty icon="chat" text="No messages" /> }
            </div>
            @if (auth.can('ManageInbox')) {
              <div class="reply">
                @if (c.summary.channel === 'whatsapp' && !windowOpen()) {
                  <div class="callout warn small">The 24-hour customer-service window is closed. Free-form replies are rejected by WhatsApp; send an approved template notification instead.</div>
                }
                <mat-form-field class="full"><mat-label>Reply</mat-label>
                  <textarea matInput rows="3" [(ngModel)]="replyText" maxlength="4096" (keydown.control.enter)="send()" (keydown.meta.enter)="send()"
                    [disabled]="c.summary.channel === 'whatsapp' && !windowOpen()"></textarea>
                  <mat-hint align="end">{{ replyText.length }}/4096 · Ctrl+Enter to send</mat-hint></mat-form-field>
                <div class="row"><span class="spacer"></span>
                  <button mat-flat-button (click)="send()" [disabled]="sending() || !replyText.trim() || (c.summary.channel === 'whatsapp' && !windowOpen())"><mat-icon>send</mat-icon>Send reply</button></div>
              </div>
            }
          } @else { <app-empty icon="forum" text="Select a conversation" /> }
        </section>
      </div>
    </div>`,
  styles: [`
    .inbox { grid-template-columns: minmax(260px, 360px) 1fr; }
    @media (max-width: 900px) { .inbox { grid-template-columns: 1fr; } }
    .thread { padding: 8px; border-radius: 6px; cursor: pointer; border: 1px solid transparent; display: flex; flex-direction: column; gap: 2px; }
    .thread:hover { background: var(--hover); } .thread.selected { background: var(--selected); border-color: var(--border); }
    .thread.unread b { font-weight: 700; }
    .badge { min-width: 20px; height: 20px; border-radius: 10px; background: var(--accent); color: var(--surface); font-size: 11px; font-weight: 700; display: inline-grid; place-items: center; padding: 0 6px; }
    .convo .bubble-list { max-height: calc(100vh - 420px); min-height: 200px; overflow: auto; background: var(--canvas); border-radius: 6px; border: 1px solid var(--border); }
    .reply { margin-top: 8px; }
    .inline-ic { font-size: 14px; width: 14px; height: 14px; vertical-align: -2px; }`],
})
export class Inbox implements OnInit {
  private api = inject(ApiService);
  private rt = inject(RealtimeService);
  private snack = inject(MatSnackBar);
  private router = inject(Router);
  private destroyRef = inject(DestroyRef);
  auth = inject(AuthService);
  lookups = inject(LookupsService);
  @ViewChild('scroller') scroller?: ElementRef<HTMLDivElement>;

  thread = input<string>();
  q = ''; channel = ''; unread = false;
  threads = signal<ConversationSummary[]>([]);
  selectedId = signal<string | null>(null);
  current = signal<{ summary: ConversationSummary; messages: ConversationMessage[] } | null>(null);
  loading = signal(false);
  error = signal<unknown>(null);
  sending = signal(false);
  replyText = '';
  simulateOpen = signal(false);
  sim = { providerAccountId: '', from: '', body: 'Thanks, I will pay by Friday.' };
  mockProviders = computed(() => this.lookups.providers().filter(p => p.providerType === 'mock' && ['whatsapp', 'sms', 'email'].includes(p.channel)));
  private now = signal(Date.now());

  windowOpen = computed(() => {
    this.now();
    const last = parseDate(this.current()?.summary.lastInboundAt);
    return !!last && Date.now() - last.getTime() < 24 * 3600 * 1000;
  });
  windowCloses = computed(() => {
    const last = parseDate(this.current()?.summary.lastInboundAt);
    if (!last) return '';
    const ms = last.getTime() + 24 * 3600 * 1000 - Date.now();
    const h = Math.floor(ms / 3600000), m = Math.floor((ms % 3600000) / 60000);
    return h > 0 ? `in ${h} h ${m} min` : `in ${m} min`;
  });

  ngOnInit() {
    this.load();
    const t = this.thread();
    if (t) this.open(t);
    this.sim.providerAccountId = this.mockProviders().find(p => p.channel === 'whatsapp')?.id ?? '';
    this.rt.changes(800, ['inbound']).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => { this.load(true); const id = this.selectedId(); if (id) this.open(id, true); });
    this.rt.changes(2500, ['delivery']).pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => { const id = this.selectedId(); if (id) this.open(id, true); });
    const timer = setInterval(() => this.now.set(Date.now()), 60000);
    this.destroyRef.onDestroy(() => clearInterval(timer));
  }

  load(silent = false) {
    if (!silent) this.loading.set(true);
    this.api.conversations({ channel: this.channel || undefined, unread: this.unread || undefined, q: this.q.trim() || undefined }).subscribe({
      next: t => {
        this.threads.set(t); this.loading.set(false);
        const cur = this.current();
        if (cur) { const s = t.find(x => x.id === cur.summary.id); if (s) this.current.set({ ...cur, summary: s }); }
      },
      error: e => { this.error.set(e); this.loading.set(false); },
    });
  }

  open(id: string, silent = false) {
    this.selectedId.set(id);
    if (!silent) this.router.navigate([], { queryParams: { thread: id }, replaceUrl: true });
    this.api.conversation(id).subscribe({
      next: r => {
        const t = r.thread as Record<string, unknown>;
        const fromList = this.threads().find(x => x.id === id);
        const summary: ConversationSummary = fromList ?? {
          id, companyId: t['companyId'] as string, applicationId: t['applicationId'] as string, channel: t['channel'] as ConversationSummary['channel'], externalAddress: t['externalAddress'] as string,
          contactName: t['contactName'] as string, lastMessageAt: t['lastMessageAt'] as string, lastInboundAt: t['lastInboundAt'] as string, unreadCount: Number(t['unreadCount'] ?? 0),
          closed: !!t['closed'], windowOpen: false,
        };
        summary.lastInboundAt = (t['lastInboundAt'] as string) ?? summary.lastInboundAt;
        this.current.set({ summary, messages: r.messages });
        setTimeout(() => { const el = this.scroller?.nativeElement; if (el) el.scrollTop = el.scrollHeight; });
        if (summary.unreadCount > 0 && this.auth.can('ManageInbox')) this.api.markRead(id).subscribe({ next: () => this.load(true), error: () => undefined });
      },
      error: e => this.error.set(e),
    });
  }

  send() {
    const c = this.current();
    if (!c || !this.replyText.trim()) return;
    this.sending.set(true);
    this.api.reply(c.summary.id, this.replyText.trim()).subscribe({
      next: () => { this.sending.set(false); this.replyText = ''; this.snack.open('Reply queued', 'OK', { duration: 2500 }); this.open(c.summary.id, true); },
      error: e => { this.sending.set(false); this.error.set(e); },
    });
  }

  toggleSimulate() {
    this.simulateOpen.set(!this.simulateOpen());
    const cur = this.current()?.summary;
    const mocks = this.mockProviders();
    if (!this.sim.providerAccountId) this.sim.providerAccountId = mocks.find(p => p.channel === (cur?.channel ?? 'whatsapp'))?.id ?? mocks[0]?.id ?? '';
    if (!this.sim.from && cur) this.sim.from = cur.externalAddress;
  }

  simulate() {
    this.api.simulateInbound({ ...this.sim }).subscribe({
      next: r => { this.snack.open(`Simulated reply accepted (${r.accepted})`, 'OK', { duration: 3000 }); setTimeout(() => this.load(), 800); },
      error: e => this.error.set(e),
    });
  }
}
