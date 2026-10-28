import { HttpClient } from '@angular/common/http';
import { inject, Injectable, signal } from '@angular/core';
import type { Device, InboxTokenResponse } from '@wings/notification-hub-client/inbox';
import { firstValueFrom } from 'rxjs';
import { environment } from '../environments/environment';

export interface DeviceRegistration {
  platform: 'android' | 'ios';
  token: string;
  deviceName?: string;
  appVersion?: string;
  locale?: string;
}

/**
 * Calls the app's OWN backend (samples/AspNetCore.OrderService). The backend holds the Notification Hub client
 * credential and talks to the hub; the mobile app never sees a client secret.
 */
@Injectable({ providedIn: 'root' })
export class BackendService {
  private readonly http = inject(HttpClient);

  /** DEMO: the sample backend identifies the user by X-User-Id. Use your real session/bearer token (HttpInterceptor). */
  readonly userId = signal('u-1001');

  /** GET /me/inbox-token → the hub's InboxTokenResponse for the signed-in user. */
  getInboxToken(): Promise<InboxTokenResponse> {
    return firstValueFrom(this.http.get<InboxTokenResponse>(`${environment.backendUrl}/me/inbox-token`, { headers: this.headers() }));
  }

  /** POST /me/devices → backend calls POST /api/v1/devices on the hub with its client credential. */
  registerDevice(device: DeviceRegistration): Promise<Device> {
    return firstValueFrom(this.http.post<Device>(`${environment.backendUrl}/me/devices`, device, { headers: this.headers() }));
  }

  private headers(): Record<string, string> {
    return { 'X-User-Id': this.userId() };
  }
}
