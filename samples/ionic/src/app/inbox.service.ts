import { inject, Injectable, NgZone, signal } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { InboxClient, type AppAction, type InAppMessage, type PushInteraction, type SignalRModule } from '@wings/notification-hub-client/inbox';
import { environment } from '../environments/environment';
import { BackendService } from './backend.service';

/** In-app inbox state on top of the TypeScript InboxClient (REST + SignalR), authenticated with inbox tokens from the backend. */
@Injectable({ providedIn: 'root' })
export class InboxService {
  private readonly backend = inject(BackendService);
  private readonly zone = inject(NgZone);
  private started = false;

  readonly client = new InboxClient({
    baseUrl: environment.notificationHubUrl,
    getToken: () => this.backend.getInboxToken(),
    signalR: signalR as unknown as SignalRModule,
  });
  readonly messages = signal<InAppMessage[]>([]);
  readonly unread = signal(0);
  readonly error = signal<string | null>(null);

  constructor() {
    // SignalR callbacks can arrive outside Angular's zone; re-enter it so the UI updates.
    this.client.onUnreadCountChange((n) => this.zone.run(() => this.unread.set(n)));
    this.client.onNotification((m) => this.zone.run(() => this.messages.update((list) => (list.some((x) => x.id === m.id) ? list : [m, ...list]))));
  }

  async start(): Promise<void> {
    if (this.started) return;
    this.started = true;
    await this.refresh();
    try {
      await this.client.connect();
    } catch (e) {
      console.warn('[inbox] realtime unavailable; pull-to-refresh still works', e);
    }
  }

  async refresh(): Promise<void> {
    try {
      this.error.set(null);
      const page = await this.client.list({ pageSize: 50 });
      this.messages.set(page.items);
    } catch (e) {
      this.error.set(e instanceof Error ? e.message : 'Could not load notifications.');
    }
  }

  async open(m: InAppMessage): Promise<void> {
    if (!m.readAt) this.replace(await this.client.markRead(m.id));
  }

  async toggleRead(m: InAppMessage): Promise<void> {
    this.replace(m.readAt ? await this.client.markUnread(m.id) : await this.client.markRead(m.id));
  }

  async act(m: InAppMessage, action: AppAction): Promise<void> {
    this.replace(await this.client.performAction(m.id, action.id));
  }

  async archive(m: InAppMessage): Promise<void> {
    await this.client.archive(m.id);
    this.messages.update((list) => list.filter((x) => x.id !== m.id));
  }

  async dismiss(m: InAppMessage): Promise<void> {
    await this.client.dismiss(m.id);
    this.messages.update((list) => list.filter((x) => x.id !== m.id));
  }

  async markAllRead(): Promise<void> {
    await this.client.markAllRead();
    const now = new Date().toISOString();
    this.messages.update((list) => list.map((x) => (x.readAt ? x : { ...x, readAt: now })));
  }

  /** POST /api/v1/push/events with the inbox token (the hub checks the device belongs to this user). */
  reportPush(deliveryId: string, interaction: PushInteraction): Promise<void> {
    return this.client.reportPushInteraction(deliveryId, interaction).catch((e) => console.warn('[push] could not report', interaction, e));
  }

  private replace(updated: InAppMessage): void {
    this.messages.update((list) => list.map((x) => (x.id === updated.id ? updated : x)));
  }
}
