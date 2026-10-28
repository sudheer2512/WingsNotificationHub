import { ChangeDetectionStrategy, Component, inject, output } from '@angular/core';
import type { InAppMessage } from '@wings/notification-hub-client/inbox';
import { WingsInboxStore } from './inbox.store';

/** Transient toasts for messages that arrive in realtime (bottom-right). Critical messages stay until closed. */
@Component({
  selector: 'wings-inbox-toasts',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'wings-inbox-toasts',
    '[style.--wings-inbox-primary]': 'store.theme().primary',
    '[style.--wings-inbox-on-primary]': 'store.theme().onPrimary',
  },
  template: `
    <div class="stack" aria-live="polite">
      @for (t of store.toasts(); track t.id) {
        <div class="toast" [class.critical]="t.priority === 'Critical'" [class.high]="t.priority === 'High'" role="status">
          <button type="button" class="main" (click)="open(t)">
            <strong>{{ t.title }}</strong>
            <span>{{ t.body }}</span>
          </button>
          <button type="button" class="close" aria-label="Close notification" (click)="store.dismissToast(t.id)">×</button>
        </div>
      }
    </div>
  `,
  styles: `
    :host { position: fixed; right: 16px; bottom: 16px; z-index: 1100; pointer-events: none; }
    .stack { display: flex; flex-direction: column; gap: 8px; width: min(360px, calc(100vw - 32px)); }
    .toast {
      pointer-events: auto; display: flex; align-items: flex-start; gap: 4px; padding: 10px 8px 10px 14px; border-radius: 10px;
      background: var(--wings-inbox-surface, #fff); color: var(--wings-inbox-text, #1f1f1f); box-shadow: 0 6px 20px rgba(0, 0, 0, .2);
      border-left: 4px solid var(--wings-inbox-primary, #1565c0); font: 14px/1.4 var(--wings-inbox-font, system-ui, sans-serif);
      animation: wings-toast-in .2s ease-out;
    }
    .toast.high { border-left-color: var(--wings-inbox-high, #ef6c00); }
    .toast.critical { border-left-color: var(--wings-inbox-critical, #c62828); }
    button { font: inherit; color: inherit; background: none; border: 0; cursor: pointer; }
    .main { flex: 1; display: flex; flex-direction: column; gap: 2px; text-align: left; padding: 0; min-width: 0; }
    .main span { color: var(--wings-inbox-muted, #5f6368); overflow: hidden; text-overflow: ellipsis; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; }
    .close { width: 24px; height: 24px; font-size: 18px; line-height: 1; color: var(--wings-inbox-muted, #5f6368); border-radius: 50%; }
    .close:hover { background: var(--wings-inbox-border, #e3e3e3); }
    @keyframes wings-toast-in { from { transform: translateY(8px); opacity: 0; } to { transform: none; opacity: 1; } }
    @media (prefers-reduced-motion: reduce) { .toast { animation: none; } }
  `,
})
export class WingsInboxToastsComponent {
  protected readonly store = inject(WingsInboxStore);
  readonly messageOpened = output<InAppMessage>();

  protected open(m: InAppMessage): void {
    this.messageOpened.emit(m);
    void this.store.open(m);
  }
}
