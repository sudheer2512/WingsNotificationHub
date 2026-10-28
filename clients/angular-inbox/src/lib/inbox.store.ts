import { computed, inject, Injectable, type OnDestroy, signal } from '@angular/core';
import { Router } from '@angular/router';
import * as signalR from '@microsoft/signalr';
import {
  InboxClient, NotificationHubError, type AppAction, type InAppMessage, type InboxBranding, type InboxConnectionState, type SignalRModule,
} from '@wings/notification-hub-client/inbox';
import { WINGS_INBOX_CONFIG } from './inbox.config';
import { inboxTheme, resolveDeepLink, safeImageUrl } from './utils';

export type InboxView = 'all' | 'unread' | 'archived';

/**
 * Signal-based state for the in-app inbox, shared by the bell, panel and toasts. Provided by provideWingsInbox().
 * Wraps the TypeScript InboxClient (REST + SignalR) and keeps messages, unread count and branding in signals,
 * so it works with zone.js and zoneless change detection alike.
 */
@Injectable()
export class WingsInboxStore implements OnDestroy {
  private readonly config = inject(WINGS_INBOX_CONFIG);
  private readonly router = inject(Router, { optional: true });
  readonly client: InboxClient;

  readonly messages = signal<InAppMessage[]>([]);
  readonly unreadCount = signal(0);
  readonly branding = signal<InboxBranding>({});
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly connection = signal<InboxConnectionState>('disconnected');
  readonly view = signal<InboxView>('all');
  readonly category = signal<string | null>(null);
  readonly hasMore = signal(false);
  readonly toasts = signal<InAppMessage[]>([]);

  private readonly knownCategories = signal<string[]>([]);
  readonly categories = computed(() => this.knownCategories());
  readonly title = computed(() => this.config.title ?? (typeof this.branding().inboxTitle === 'string' ? this.branding().inboxTitle! : 'Notifications'));
  readonly theme = computed(() => inboxTheme(this.branding()));
  readonly logoUrl = computed(() => safeImageUrl(this.branding().logoUrl as string | undefined));
  readonly toastsEnabled = this.config.toasts !== false;
  readonly locale = this.config.locale;

  private readonly pageSize = Math.min(Math.max(this.config.pageSize ?? 20, 1), 100);
  private page = 1;
  private started?: Promise<void>;
  private readonly timers = new Set<ReturnType<typeof setTimeout>>();
  private readonly unsubscribe: (() => void)[] = [];

  constructor() {
    this.client = new InboxClient({
      baseUrl: this.config.baseUrl,
      getToken: this.config.getToken,
      signalR: this.config.signalR ?? (signalR as unknown as SignalRModule),
      signalRLogLevel: this.config.signalRLogLevel ?? signalR.LogLevel.Warning,
    });
    this.unsubscribe.push(
      this.client.onUnreadCountChange((n) => this.unreadCount.set(n)),
      this.client.onConnectionStateChange((s) => this.connection.set(s)),
      this.client.onNotification((m) => this.onIncoming(m)),
    );
  }

  /** Loads branding and the first page and opens the realtime connection. Safe to call repeatedly. */
  start(): Promise<void> {
    this.started ??= this.doStart();
    return this.started;
  }

  async reload(): Promise<void> {
    this.page = 1;
    await this.load(false);
  }

  async loadMore(): Promise<void> {
    if (!this.hasMore() || this.loading()) return;
    this.page++;
    await this.load(true);
  }

  setView(view: InboxView): void {
    if (view === this.view()) return;
    this.view.set(view);
    void this.reload();
  }

  setCategory(category: string | null): void {
    if (category === this.category()) return;
    this.category.set(category);
    void this.reload();
  }

  async markRead(m: InAppMessage): Promise<void> {
    if (m.readAt) return;
    await this.run(async () => this.replace(await this.client.markRead(m.id)));
  }

  async markUnread(m: InAppMessage): Promise<void> {
    if (!m.readAt) return;
    await this.run(async () => this.replace(await this.client.markUnread(m.id)));
  }

  async toggleRead(m: InAppMessage): Promise<void> {
    await (m.readAt ? this.markUnread(m) : this.markRead(m));
  }

  async archive(m: InAppMessage): Promise<void> {
    await this.run(async () => {
      const updated = await this.client.archive(m.id);
      if (this.view() === 'archived') this.replace(updated);
      else this.remove(m.id);
    });
  }

  async dismiss(m: InAppMessage): Promise<void> {
    await this.run(async () => {
      await this.client.dismiss(m.id);
      this.remove(m.id);
    });
  }

  async markAllRead(): Promise<void> {
    await this.run(async () => {
      await this.client.markAllRead();
      const now = new Date().toISOString();
      this.messages.update((list) => list.map((x) => (x.readAt ? x : { ...x, readAt: now })));
    });
  }

  /** Opens a message: marks it read and follows its deep link. */
  async open(m: InAppMessage): Promise<void> {
    this.dismissToast(m.id);
    await this.markRead(m);
    await this.navigate(m.deepLink, m);
  }

  /** Records an action button press (raises an inapp.action webhook) and follows the action's deep link. */
  async act(m: InAppMessage, action: AppAction): Promise<void> {
    await this.run(async () => this.replace(await this.client.performAction(m.id, action.id)));
    await this.navigate(action.deepLink ?? m.deepLink, m);
  }

  dismissToast(id: string): void {
    this.toasts.update((t) => t.filter((x) => x.id !== id));
  }

  ngOnDestroy(): void {
    for (const t of this.timers) clearTimeout(t);
    for (const u of this.unsubscribe) u();
    void this.client.dispose();
  }

  private async doStart(): Promise<void> {
    await Promise.all([
      this.client.getBranding().then((b) => this.branding.set(b ?? {})).catch(() => undefined),
      this.reload(),
    ]);
    if (this.config.realtime !== false) {
      try {
        await this.client.connect();
      } catch (e) {
        // REST keeps working without realtime; surface the problem but do not block the inbox.
        console.warn('[wings-inbox] realtime connection failed', e);
      }
    }
  }

  private async load(append: boolean): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const view = this.view();
      const page = await this.client.list({
        unreadOnly: view === 'unread' ? true : undefined,
        archived: view === 'archived' ? true : undefined,
        category: this.category() ?? undefined,
        page: this.page,
        pageSize: this.pageSize,
      });
      this.messages.update((list) => (append ? [...list, ...page.items.filter((m) => !list.some((x) => x.id === m.id))] : page.items));
      this.hasMore.set(page.items.length === this.pageSize);
      this.learnCategories(page.items);
    } catch (e) {
      this.error.set(describe(e));
    } finally {
      this.loading.set(false);
    }
  }

  private onIncoming(m: InAppMessage): void {
    this.learnCategories([m]);
    const view = this.view();
    const matches = view !== 'archived' && (!this.category() || this.category() === m.category);
    if (matches) this.messages.update((list) => (list.some((x) => x.id === m.id) ? list : sortMessages([m, ...list])));
    if (this.toastsEnabled && !m.readAt) {
      this.toasts.update((t) => [m, ...t.filter((x) => x.id !== m.id)].slice(0, 3));
      if (m.priority !== 'Critical') {
        const timer = setTimeout(() => {
          this.timers.delete(timer);
          this.dismissToast(m.id);
        }, this.config.toastDurationMs ?? 6000);
        this.timers.add(timer);
      }
    }
  }

  private async navigate(url: string | null | undefined, m: InAppMessage): Promise<void> {
    if (!url || url === '#') return;
    if (this.config.openDeepLink) {
      const handled = await this.config.openDeepLink(url, m);
      if (handled !== false) return;
    }
    const origin = typeof location === 'undefined' ? undefined : location.origin;
    const target = resolveDeepLink(url, origin);
    if (target.kind === 'internal') {
      if (this.router) await this.router.navigateByUrl(target.path);
      else location.assign(target.path);
    } else if (target.kind === 'external') {
      window.open(target.href, '_blank', 'noopener,noreferrer');
    } else {
      console.warn(`[wings-inbox] deep link not followed (${target.reason}): ${url}`);
    }
  }

  private replace(updated: InAppMessage): void {
    this.messages.update((list) => list.map((x) => (x.id === updated.id ? updated : x)));
  }

  private remove(id: string): void {
    this.messages.update((list) => list.filter((x) => x.id !== id));
    this.dismissToast(id);
  }

  private learnCategories(items: InAppMessage[]): void {
    const current = this.knownCategories();
    const next = new Set(current);
    for (const m of items) if (m.category) next.add(m.category);
    if (next.size !== current.length) this.knownCategories.set([...next].sort((a, b) => a.localeCompare(b)));
  }

  private async run(fn: () => Promise<void>): Promise<void> {
    try {
      this.error.set(null);
      await fn();
    } catch (e) {
      this.error.set(describe(e));
    }
  }
}

/** Critical first, then newest first (the hub's ordering). */
function sortMessages(list: InAppMessage[]): InAppMessage[] {
  return [...list].sort((a, b) => Number(b.priority === 'Critical') - Number(a.priority === 'Critical') || b.createdAt.localeCompare(a.createdAt));
}

function describe(e: unknown): string {
  if (e instanceof NotificationHubError) return e.status === 0 ? 'Cannot reach the notification service.' : e.message;
  return e instanceof Error ? e.message : 'Something went wrong.';
}
