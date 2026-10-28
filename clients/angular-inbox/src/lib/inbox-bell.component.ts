import { CdkConnectedOverlay, CdkOverlayOrigin, type ConnectedPosition } from '@angular/cdk/overlay';
import { afterNextRender, ChangeDetectionStrategy, Component, computed, inject, input, output, signal, viewChild, type ElementRef } from '@angular/core';
import type { InAppMessage } from '@wings/notification-hub-client/inbox';
import { WingsInboxPanelComponent, type InboxActionEvent } from './inbox-panel.component';
import { WingsInboxStore } from './inbox.store';
import { WingsInboxToastsComponent } from './inbox-toasts.component';
import { badgeText } from './utils';

export type { InboxActionEvent } from './inbox-panel.component';

/**
 * Notification bell with an unread badge. Clicking it opens the inbox panel in a CDK overlay dropdown;
 * new realtime messages also appear as toasts. Requires provideWingsInbox() in the application providers.
 *
 * ```html
 * <wings-inbox-bell (messageOpened)="track($event)" />
 * ```
 */
@Component({
  selector: 'wings-inbox-bell',
  imports: [CdkOverlayOrigin, CdkConnectedOverlay, WingsInboxPanelComponent, WingsInboxToastsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'wings-inbox-bell',
    '[style.--wings-inbox-primary]': 'store.theme().primary',
    '[style.--wings-inbox-on-primary]': 'store.theme().onPrimary',
  },
  template: `
    <button #trigger type="button" class="bell" cdkOverlayOrigin #origin="cdkOverlayOrigin" (click)="toggle()"
            [attr.aria-label]="label()" aria-haspopup="dialog" [attr.aria-expanded]="open()" [title]="label()">
      <svg viewBox="0 0 24 24" aria-hidden="true">
        <path fill="currentColor"
              d="M12 22a2.5 2.5 0 0 0 2.45-2h-4.9A2.5 2.5 0 0 0 12 22Zm7-6V11c0-3.07-1.64-5.64-4.5-6.32V4a2.5 2.5 0 0 0-5 0v.68C6.63 5.36 5 7.92 5 11v5l-2 2v1h18v-1l-2-2Z" />
      </svg>
      @if (badge()) {
        <span class="badge" [class.wide]="badge().length > 1" aria-hidden="true">{{ badge() }}</span>
      }
      @if (store.connection() === 'reconnecting') {
        <span class="offline" aria-hidden="true"></span>
      }
    </button>

    <ng-template cdkConnectedOverlay [cdkConnectedOverlayOrigin]="origin" [cdkConnectedOverlayOpen]="open()"
                 [cdkConnectedOverlayPositions]="positions" [cdkConnectedOverlayPush]="true"
                 (overlayOutsideClick)="onOutsideClick($event)" (overlayKeydown)="onKeydown($event)" (detach)="close(false)">
      <wings-inbox-panel [style.width]="'min(' + panelWidth() + 'px, calc(100vw - 16px))'"
                         (messageOpened)="onOpened($event)" (actionInvoked)="actionInvoked.emit($event)" />
    </ng-template>

    @if (toastsEnabled()) {
      <wings-inbox-toasts (messageOpened)="messageOpened.emit($event)" />
    }
  `,
  styles: `
    :host { display: inline-block; position: relative; }
    .bell {
      position: relative; display: grid; place-items: center; width: 40px; height: 40px; padding: 0; border: 0; border-radius: 50%;
      background: transparent; color: var(--wings-inbox-bell-color, currentColor); cursor: pointer;
    }
    .bell:hover { background: rgba(127, 127, 127, .14); }
    .bell:focus-visible { outline: 2px solid var(--wings-inbox-primary, #1565c0); outline-offset: 2px; }
    .bell svg { width: 24px; height: 24px; }
    .badge {
      position: absolute; top: 4px; right: 4px; min-width: 18px; height: 18px; padding: 0 4px; box-sizing: border-box; border-radius: 9px;
      background: var(--wings-inbox-badge, var(--wings-inbox-critical, #d32f2f)); color: #fff;
      font: 600 11px/18px var(--wings-inbox-font, system-ui, sans-serif); text-align: center; pointer-events: none;
    }
    .badge.wide { right: 0; }
    .offline { position: absolute; bottom: 6px; right: 6px; width: 8px; height: 8px; border-radius: 50%; background: #f9a825; }
  `,
})
export class WingsInboxBellComponent {
  protected readonly store = inject(WingsInboxStore);

  /** Accessible name of the bell (the unread count is appended). */
  readonly ariaLabel = input('Notifications');
  /** Dropdown width in pixels (capped to the viewport). */
  readonly panelWidth = input(380);
  /** Overrides the provideWingsInbox `toasts` setting. */
  readonly showToasts = input<boolean | undefined>(undefined);
  /** Close the dropdown after a message is opened (default true). */
  readonly closeOnOpen = input(true);

  readonly messageOpened = output<InAppMessage>();
  readonly actionInvoked = output<InboxActionEvent>();

  protected readonly open = signal(false);
  protected readonly badge = computed(() => badgeText(this.store.unreadCount()));
  protected readonly label = computed(() => {
    const n = this.store.unreadCount();
    return n ? `${this.ariaLabel()}, ${n} unread` : this.ariaLabel();
  });
  protected readonly toastsEnabled = computed(() => this.showToasts() ?? this.store.toastsEnabled);
  protected readonly positions: ConnectedPosition[] = [
    { originX: 'end', originY: 'bottom', overlayX: 'end', overlayY: 'top', offsetY: 6 },
    { originX: 'start', originY: 'bottom', overlayX: 'start', overlayY: 'top', offsetY: 6 },
    { originX: 'end', originY: 'top', overlayX: 'end', overlayY: 'bottom', offsetY: -6 },
  ];
  private readonly trigger = viewChild.required<ElementRef<HTMLButtonElement>>('trigger');

  constructor() {
    // Browser only (SSR-safe): load the inbox and connect realtime after the first render.
    afterNextRender(() => void this.store.start());
  }

  toggle(): void {
    this.open.update((v) => !v);
  }

  close(restoreFocus = true): void {
    if (!this.open()) return;
    this.open.set(false);
    if (restoreFocus) this.trigger().nativeElement.focus();
  }

  protected onOutsideClick(event: MouseEvent): void {
    // Clicks on the bell itself are handled by toggle().
    if (event.target instanceof Node && this.trigger().nativeElement.contains(event.target)) return;
    this.close(false);
  }

  protected onKeydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      event.preventDefault();
      this.close();
    }
  }

  protected onOpened(m: InAppMessage): void {
    this.messageOpened.emit(m);
    if (this.closeOnOpen()) this.close(false);
  }
}
