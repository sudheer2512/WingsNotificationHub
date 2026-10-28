import { afterNextRender, ChangeDetectionStrategy, Component, inject, output } from '@angular/core';
import type { AppAction, InAppMessage } from '@wings/notification-hub-client/inbox';
import { WingsInboxStore, type InboxView } from './inbox.store';
import { WingsRelativeTimePipe } from './relative-time.pipe';
import { safeImageUrl } from './utils';

export interface InboxActionEvent {
  message: InAppMessage;
  action: AppAction;
}

/**
 * The inbox list: tabs (all / unread / archived), category filter, priority styling, deep links, action buttons,
 * per-message read/unread/archive/dismiss and "mark all as read". Used inside <wings-inbox-bell>'s dropdown and
 * usable on its own (e.g. a full-page /notifications route).
 */
@Component({
  selector: 'wings-inbox-panel',
  imports: [WingsRelativeTimePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'wings-inbox-panel',
    role: 'region',
    '[attr.aria-label]': 'store.title()',
    '[style.--wings-inbox-primary]': 'store.theme().primary',
    '[style.--wings-inbox-on-primary]': 'store.theme().onPrimary',
    '[style.--wings-inbox-accent]': 'store.theme().accent',
  },
  template: `
    <header class="head">
      @if (store.logoUrl(); as logo) {
        <img class="logo" [src]="logo" alt="" />
      }
      <h2 class="title">{{ store.title() }}</h2>
      <button type="button" class="link" (click)="store.markAllRead()" [disabled]="!store.unreadCount()">Mark all as read</button>
    </header>

    <div class="tabs" role="tablist" aria-label="Filter notifications">
      @for (v of views; track v.id) {
        <button type="button" role="tab" class="tab" [class.active]="store.view() === v.id" [attr.aria-selected]="store.view() === v.id"
                (click)="store.setView(v.id)">
          {{ v.label }}
          @if (v.id === 'unread' && store.unreadCount()) {
            <span class="count">{{ store.unreadCount() }}</span>
          }
        </button>
      }
    </div>

    @if (store.categories().length > 1) {
      <div class="chips" role="group" aria-label="Categories">
        <button type="button" class="chip" [class.active]="!store.category()" (click)="store.setCategory(null)">All</button>
        @for (c of store.categories(); track c) {
          <button type="button" class="chip" [class.active]="store.category() === c" (click)="store.setCategory(c)">{{ c }}</button>
        }
      </div>
    }

    @if (store.error(); as err) {
      <div class="error" role="alert">
        <span>{{ err }}</span>
        <button type="button" class="link" (click)="store.reload()">Retry</button>
      </div>
    }

    <ul class="list" aria-live="polite">
      @for (m of store.messages(); track m.id) {
        <li class="item" [class.unread]="!m.readAt" [class.critical]="m.priority === 'Critical'" [class.high]="m.priority === 'High'"
            [class.low]="m.priority === 'Low'">
          <button type="button" class="main" (click)="open(m)" [attr.aria-label]="(m.readAt ? '' : 'Unread: ') + m.title">
            @if (image(m); as img) {
              <img class="thumb" [src]="img" alt="" loading="lazy" />
            }
            <span class="text">
              <span class="meta">
                @if (m.priority === 'Critical' || m.priority === 'High') {
                  <span class="prio">{{ m.priority }}</span>
                }
                @if (m.category) {
                  <span class="cat">{{ m.category }}</span>
                }
                <time [attr.datetime]="m.createdAt">{{ m.createdAt | wingsRelativeTime: store.locale }}</time>
              </span>
              <span class="subject">{{ m.title }}</span>
              <span class="body">{{ m.body }}</span>
            </span>
          </button>

          @if (m.actions.length) {
            <div class="actions">
              @for (a of m.actions; track a.id) {
                <button type="button" class="action" [class.taken]="m.actionTaken === a.id" [disabled]="!!m.actionTaken" (click)="act(m, a)">
                  {{ a.label }}
                </button>
              }
            </div>
          }

          <div class="tools">
            <button type="button" class="icon" (click)="store.toggleRead(m)" [attr.aria-label]="m.readAt ? 'Mark as unread' : 'Mark as read'"
                    [title]="m.readAt ? 'Mark as unread' : 'Mark as read'">
              <svg viewBox="0 0 24 24" aria-hidden="true">
                @if (m.readAt) {
                  <circle cx="12" cy="12" r="5" fill="none" stroke="currentColor" stroke-width="2" />
                } @else {
                  <circle cx="12" cy="12" r="5" fill="currentColor" />
                }
              </svg>
            </button>
            @if (!m.archivedAt) {
              <button type="button" class="icon" (click)="store.archive(m)" aria-label="Archive" title="Archive">
                <svg viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="M20.5 5.2 19.1 3.5A1.5 1.5 0 0 0 18 3H6c-.5 0-.9.2-1.2.5L3.5 5.2C3.2 5.6 3 6 3 6.5V19a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V6.5c0-.5-.2-.9-.5-1.3ZM12 17.5 6.5 12H10v-2h4v2h3.5L12 17.5ZM5.1 5l.8-1h12l.9 1H5.1Z"/></svg>
              </button>
            }
            <button type="button" class="icon" (click)="store.dismiss(m)" aria-label="Dismiss" title="Dismiss">
              <svg viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" d="M19 6.4 17.6 5 12 10.6 6.4 5 5 6.4 10.6 12 5 17.6 6.4 19l5.6-5.6 5.6 5.6 1.4-1.4-5.6-5.6L19 6.4Z"/></svg>
            </button>
          </div>
        </li>
      } @empty {
        @if (!store.loading()) {
          <li class="empty">{{ store.view() === 'archived' ? 'No archived notifications.' : "You're all caught up." }}</li>
        }
      }
    </ul>

    @if (store.loading()) {
      <div class="loading" role="status">Loading…</div>
    } @else if (store.hasMore()) {
      <button type="button" class="more" (click)="store.loadMore()">Load more</button>
    }
  `,
  styles: `
    :host {
      --_primary: var(--wings-inbox-primary, #1565c0);
      --_on-primary: var(--wings-inbox-on-primary, #fff);
      --_surface: var(--wings-inbox-surface, #fff);
      --_text: var(--wings-inbox-text, #1f1f1f);
      --_muted: var(--wings-inbox-muted, #5f6368);
      --_border: var(--wings-inbox-border, #e3e3e3);
      --_critical: var(--wings-inbox-critical, #c62828);
      --_high: var(--wings-inbox-high, #ef6c00);
      display: flex; flex-direction: column; box-sizing: border-box; max-height: min(560px, 80vh); width: 100%;
      background: var(--_surface); color: var(--_text); font: 14px/1.4 var(--wings-inbox-font, system-ui, -apple-system, 'Segoe UI', Roboto, sans-serif);
      border-radius: 12px; box-shadow: 0 8px 28px rgba(0, 0, 0, .18); overflow: hidden;
    }
    button { font: inherit; color: inherit; background: none; border: 0; cursor: pointer; }
    button:disabled { cursor: default; opacity: .5; }
    button:focus-visible { outline: 2px solid var(--_primary); outline-offset: 2px; }
    .head { display: flex; align-items: center; gap: 8px; padding: 12px 16px; background: var(--_primary); color: var(--_on-primary); }
    .head .logo { height: 24px; width: auto; }
    .head .title { flex: 1; margin: 0; font-size: 16px; font-weight: 600; }
    .head .link { color: var(--_on-primary); }
    .link { font-size: 13px; text-decoration: underline; padding: 2px 4px; }
    .tabs { display: flex; border-bottom: 1px solid var(--_border); }
    .tab { flex: 1; padding: 10px 8px; border-bottom: 2px solid transparent; color: var(--_muted); }
    .tab.active { color: var(--_primary); border-bottom-color: var(--_primary); font-weight: 600; }
    .count { display: inline-block; min-width: 18px; padding: 0 5px; margin-left: 4px; border-radius: 9px; background: var(--_primary); color: var(--_on-primary); font-size: 11px; line-height: 18px; }
    .chips { display: flex; gap: 6px; padding: 8px 12px; overflow-x: auto; border-bottom: 1px solid var(--_border); }
    .chip { padding: 3px 10px; border: 1px solid var(--_border); border-radius: 14px; font-size: 12px; white-space: nowrap; }
    .chip.active { background: var(--_primary); border-color: var(--_primary); color: var(--_on-primary); }
    .error { display: flex; justify-content: space-between; gap: 8px; padding: 8px 16px; background: #fdecea; color: #8a1c1c; font-size: 13px; }
    .list { list-style: none; margin: 0; padding: 0; overflow-y: auto; flex: 1; }
    .item { position: relative; padding: 10px 88px 10px 16px; border-bottom: 1px solid var(--_border); border-left: 4px solid transparent; }
    .item.unread { background: color-mix(in srgb, var(--_primary) 6%, var(--_surface)); }
    .item.unread .subject { font-weight: 600; }
    .item.high { border-left-color: var(--_high); }
    .item.critical { border-left-color: var(--_critical); background: color-mix(in srgb, var(--_critical) 7%, var(--_surface)); }
    .item.low { opacity: .85; }
    .main { display: flex; gap: 10px; width: 100%; text-align: left; padding: 0; }
    .thumb { width: 40px; height: 40px; border-radius: 6px; object-fit: cover; flex: none; }
    .text { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
    .meta { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; font-size: 11px; color: var(--_muted); }
    .prio { padding: 0 6px; border-radius: 4px; color: #fff; background: var(--_high); font-weight: 600; text-transform: uppercase; letter-spacing: .02em; }
    .critical .prio { background: var(--_critical); }
    .cat { padding: 0 6px; border-radius: 4px; background: var(--_border); color: var(--_text); }
    .subject { overflow: hidden; text-overflow: ellipsis; }
    .body { color: var(--_muted); display: -webkit-box; -webkit-line-clamp: 3; -webkit-box-orient: vertical; overflow: hidden; white-space: pre-line; }
    .actions { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 8px; }
    .action { padding: 4px 12px; border-radius: 16px; border: 1px solid var(--_primary); color: var(--_primary); font-size: 12px; font-weight: 600; }
    .action:hover:not(:disabled) { background: var(--_primary); color: var(--_on-primary); }
    .action.taken { background: var(--_primary); color: var(--_on-primary); opacity: 1; }
    .tools { position: absolute; top: 6px; right: 8px; display: flex; }
    .icon { width: 26px; height: 26px; border-radius: 50%; color: var(--_muted); display: grid; place-items: center; }
    .icon:hover { background: var(--_border); color: var(--_text); }
    .icon svg { width: 16px; height: 16px; }
    .item.unread .tools .icon:first-child { color: var(--_primary); }
    .empty { padding: 32px 16px; text-align: center; color: var(--_muted); }
    .loading { padding: 12px; text-align: center; color: var(--_muted); }
    .more { padding: 10px; color: var(--_primary); font-weight: 600; border-top: 1px solid var(--_border); }
  `,
})
export class WingsInboxPanelComponent {
  protected readonly store = inject(WingsInboxStore);

  /** A message was opened (clicked). Navigation to its deep link happens automatically. */
  readonly messageOpened = output<InAppMessage>();
  /** An action button was pressed (the hub also raises an inapp.action webhook to your backend). */
  readonly actionInvoked = output<InboxActionEvent>();

  protected readonly views: readonly { id: InboxView; label: string }[] = [
    { id: 'all', label: 'All' },
    { id: 'unread', label: 'Unread' },
    { id: 'archived', label: 'Archived' },
  ];

  constructor() {
    afterNextRender(() => void this.store.start());
  }

  protected open(m: InAppMessage): void {
    this.messageOpened.emit(m);
    void this.store.open(m);
  }

  protected act(m: InAppMessage, action: AppAction): void {
    this.actionInvoked.emit({ message: m, action });
    void this.store.act(m, action);
  }

  protected image(m: InAppMessage): string | null {
    return safeImageUrl(m.imageUrl);
  }
}
