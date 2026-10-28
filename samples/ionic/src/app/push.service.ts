import { inject, Injectable, NgZone } from '@angular/core';
import { Router } from '@angular/router';
import { App } from '@capacitor/app';
import { Capacitor } from '@capacitor/core';
import { PushNotifications, type ActionPerformed, type PushNotificationSchema } from '@capacitor/push-notifications';
import { environment } from '../environments/environment';
import { BackendService } from './backend.service';
import { InboxService } from './inbox.service';

/**
 * Native push with Capacitor:
 *  1. ask permission, create the Android channel, register with FCM/APNs;
 *  2. send the device token to the APP BACKEND (POST /me/devices), which registers it with the hub;
 *  3. report receipt/open back to the hub (POST /api/v1/push/events) using the user's inbox token;
 *  4. follow the notification's deep link inside the app.
 * Push payloads from the hub carry `deliveryId` and `deepLink` in `notification.data`.
 */
@Injectable({ providedIn: 'root' })
export class PushService {
  private readonly backend = inject(BackendService);
  private readonly inbox = inject(InboxService);
  private readonly router = inject(Router);
  private readonly zone = inject(NgZone);
  private initialized = false;

  async init(): Promise<void> {
    if (this.initialized || !Capacitor.isNativePlatform()) return; // browsers would use Web Push instead
    this.initialized = true;

    await PushNotifications.addListener('registration', (t) => void this.onToken(t.value));
    await PushNotifications.addListener('registrationError', (e) => console.error('[push] registration failed', e.error));
    await PushNotifications.addListener('pushNotificationReceived', (n) => this.onReceived(n));
    await PushNotifications.addListener('pushNotificationActionPerformed', (a) => this.onOpened(a));

    let permission = await PushNotifications.checkPermissions();
    if (permission.receive === 'prompt' || permission.receive === 'prompt-with-rationale') permission = await PushNotifications.requestPermissions();
    if (permission.receive !== 'granted') return;

    if (Capacitor.getPlatform() === 'android') {
      await PushNotifications.createChannel({ id: environment.androidChannelId, name: 'Notifications', importance: 4, visibility: 1 });
    }
    await PushNotifications.register();
  }

  /** Called on every app start with the current FCM (Android) or APNs (iOS) token; registration is an idempotent upsert. */
  private async onToken(token: string): Promise<void> {
    const platform = Capacitor.getPlatform() === 'ios' ? 'ios' : 'android';
    const info = await App.getInfo().catch(() => undefined);
    try {
      await this.backend.registerDevice({ platform, token, deviceName: `${info?.name ?? 'Wings ERP'} (${platform})`, appVersion: info?.version, locale: navigator.language });
    } catch (e) {
      console.error('[push] backend device registration failed', e);
    }
  }

  /** Foreground delivery: the OS shows nothing on Android, so refresh the inbox and record the receipt. */
  private onReceived(n: PushNotificationSchema): void {
    const deliveryId = asString(n.data?.['deliveryId']);
    if (deliveryId) void this.inbox.reportPush(deliveryId, 'received');
    void this.zone.run(() => this.inbox.refresh());
  }

  /** The user tapped the notification: record the open (marks the delivery Read) and follow the deep link. */
  private onOpened(a: ActionPerformed): void {
    const data = a.notification.data ?? {};
    const deliveryId = asString(data['deliveryId']);
    if (deliveryId) void this.inbox.reportPush(deliveryId, 'opened');
    const link = asString(data['deepLink']);
    // Only in-app routes; never navigate to arbitrary URLs from a payload.
    if (link && link.startsWith('/') && !link.startsWith('//')) void this.zone.run(() => this.router.navigateByUrl(link));
  }
}

function asString(v: unknown): string | undefined {
  return typeof v === 'string' && v.length > 0 ? v : undefined;
}
