import { Injectable, NgZone, effect, inject, signal } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Router } from '@angular/router';
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { Observable, Subject, debounceTime, filter, map, merge } from 'rxjs';
import { AuthService } from './auth.service';
import { DeliveryPush, InboundPush } from './models';

export interface LiveEvent { kind: 'delivery' | 'inbound'; at: string; text: string; tone: 'good' | 'bad' | 'warn' | 'info'; link?: unknown[]; query?: Record<string, string>; }

const BAD = new Set(['Failed', 'Expired', 'Unknown']);
const GOOD = new Set(['Delivered', 'Read', 'Sent', 'ProviderAccepted']);

/** SignalR client for /hubs/admin: delivery status changes and inbound customer replies, scoped to the user's tenants. */
@Injectable({ providedIn: 'root' })
export class RealtimeService {
  private auth = inject(AuthService);
  private snack = inject(MatSnackBar);
  private router = inject(Router);
  private zone = inject(NgZone);
  private hub: HubConnection | null = null;
  private deliveries = new Subject<DeliveryPush>();
  private inbound = new Subject<InboundPush>();

  readonly connected = signal(false);
  readonly recent = signal<LiveEvent[]>([]);
  readonly unseen = signal(0);
  readonly toastsEnabled = signal(true);
  readonly deliveries$ = this.deliveries.asObservable();
  readonly inbound$ = this.inbound.asObservable();

  constructor() {
    effect(() => {
      const token = this.auth.token(), me = this.auth.me();
      if (token && me) this.start(); else this.stop();
    });
  }

  /** Debounced "something changed" stream for auto-refreshing screens. */
  changes(debounceMs = 1500, kinds: ('delivery' | 'inbound')[] = ['delivery', 'inbound']): Observable<void> {
    const streams: Observable<unknown>[] = [];
    if (kinds.includes('delivery')) streams.push(this.deliveries);
    if (kinds.includes('inbound')) streams.push(this.inbound);
    return merge(...streams).pipe(debounceTime(debounceMs), map(() => undefined));
  }

  deliveryUpdates(deliveryId: string): Observable<DeliveryPush> { return this.deliveries.pipe(filter(d => d.deliveryId === deliveryId)); }

  private async start() {
    if (this.hub) return;
    const hub = new HubConnectionBuilder()
      .withUrl('/hubs/admin', { accessTokenFactory: () => this.auth.token() ?? '' })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();
    this.hub = hub;
    hub.on('delivery', (d: DeliveryPush) => this.zone.run(() => this.onDelivery(d)));
    hub.on('inbound', (m: InboundPush) => this.zone.run(() => this.onInbound(m)));
    hub.onreconnected(() => this.zone.run(() => this.connected.set(true)));
    hub.onreconnecting(() => this.zone.run(() => this.connected.set(false)));
    hub.onclose(() => this.zone.run(() => this.connected.set(false)));
    try {
      await hub.start();
      this.connected.set(true);
    } catch (e) {
      console.warn('Real-time updates unavailable', e);
      this.connected.set(false);
      setTimeout(() => {
        if (this.hub === hub && hub.state === HubConnectionState.Disconnected) { this.hub = null; if (this.auth.signedIn()) this.start(); }
      }, 15000);
    }
  }

  private stop() {
    const hub = this.hub;
    this.hub = null;
    this.connected.set(false);
    this.recent.set([]);
    this.unseen.set(0);
    if (hub) hub.stop().catch(() => undefined);
  }

  private push(e: LiveEvent) {
    this.recent.update(list => [e, ...list].slice(0, 60));
    this.unseen.update(n => n + 1);
  }

  private onDelivery(d: DeliveryPush) {
    this.deliveries.next(d);
    const status = d.status ?? 'updated';
    const tone = BAD.has(status) ? 'bad' : GOOD.has(status) ? 'good' : status === 'RetryPending' || status === 'Suppressed' ? 'warn' : 'info';
    // Only failures and unknown outcomes are listed; routine status changes would drown the feed.
    if (tone !== 'bad') return;
    const e: LiveEvent = { kind: 'delivery', at: d.at, tone, text: `${d.channel} delivery ${status === 'Unknown' ? 'needs an operator decision' : status.toLowerCase()} (${d.source})`,
      link: ['/explorer'], query: { delivery: d.deliveryId } };
    this.push(e);
  }

  private onInbound(m: InboundPush) {
    this.inbound.next(m);
    const e: LiveEvent = { kind: 'inbound', at: m.receivedAt, tone: 'info', text: `New ${m.channel} reply from ${m.from}: ${(m.body ?? '').slice(0, 80)}`, link: ['/inbox'], query: { thread: m.threadId } };
    this.push(e);
    if (!this.toastsEnabled()) return;
    const ref = this.snack.open(e.text, 'Open', { duration: 6000, horizontalPosition: 'right', verticalPosition: 'bottom' });
    ref.onAction().subscribe(() => this.router.navigate(['/inbox'], { queryParams: { thread: m.threadId } }));
  }
}
