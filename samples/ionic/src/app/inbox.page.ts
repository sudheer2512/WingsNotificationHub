import { DatePipe } from '@angular/common';
import { Component, inject, type OnInit } from '@angular/core';
import { Router } from '@angular/router';
import {
  IonBadge, IonButton, IonButtons, IonContent, IonHeader, IonItem, IonItemOption, IonItemOptions, IonItemSliding, IonLabel, IonList, IonNote,
  IonRefresher, IonRefresherContent, IonText, IonTitle, IonToolbar,
} from '@ionic/angular';
import type { AppAction, InAppMessage } from '@wings/notification-hub-client/inbox';
import { InboxService } from './inbox.service';

@Component({
  selector: 'app-inbox',
  imports: [DatePipe, IonHeader, IonToolbar, IonTitle, IonButtons, IonButton, IonBadge, IonContent, IonRefresher, IonRefresherContent, IonList,
    IonItemSliding, IonItem, IonLabel, IonNote, IonItemOptions, IonItemOption, IonText],
  template: `
    <ion-header>
      <ion-toolbar color="primary">
        <ion-title>Notifications @if (inbox.unread()) { <ion-badge color="danger">{{ inbox.unread() }}</ion-badge> }</ion-title>
        <ion-buttons slot="end">
          <ion-button (click)="inbox.markAllRead()" [disabled]="!inbox.unread()">Mark all read</ion-button>
        </ion-buttons>
      </ion-toolbar>
    </ion-header>
    <ion-content>
      <ion-refresher slot="fixed" (ionRefresh)="refresh($event)"><ion-refresher-content /></ion-refresher>
      @if (inbox.error(); as err) { <ion-text color="danger"><p class="ion-padding">{{ err }}</p></ion-text> }
      <ion-list>
        @for (m of inbox.messages(); track m.id) {
          <ion-item-sliding>
            <ion-item button (click)="open(m)" [class.unread]="!m.readAt" [class.critical]="m.priority === 'Critical'" [class.high]="m.priority === 'High'">
              <ion-label class="ion-text-wrap">
                <p class="meta">
                  @if (m.priority === 'Critical' || m.priority === 'High') { <ion-badge [color]="m.priority === 'Critical' ? 'danger' : 'warning'">{{ m.priority }}</ion-badge> }
                  {{ m.category }} · {{ m.createdAt | date: 'short' }}
                </p>
                <h2>{{ m.title }}</h2>
                <p>{{ m.body }}</p>
                @if (m.actions.length) {
                  <div class="actions">
                    @for (a of m.actions; track a.id) {
                      <ion-button size="small" [fill]="m.actionTaken === a.id ? 'solid' : 'outline'" [disabled]="!!m.actionTaken" (click)="act($event, m, a)">{{ a.label }}</ion-button>
                    }
                  </div>
                }
              </ion-label>
              @if (!m.readAt) { <ion-note slot="end" color="primary">●</ion-note> }
            </ion-item>
            <ion-item-options side="end">
              <ion-item-option (click)="inbox.toggleRead(m)">{{ m.readAt ? 'Unread' : 'Read' }}</ion-item-option>
              <ion-item-option color="medium" (click)="inbox.archive(m)">Archive</ion-item-option>
              <ion-item-option color="danger" (click)="inbox.dismiss(m)">Dismiss</ion-item-option>
            </ion-item-options>
          </ion-item-sliding>
        } @empty {
          <p class="ion-padding ion-text-center">You're all caught up.</p>
        }
      </ion-list>
    </ion-content>
  `,
  styles: `
    ion-item.unread h2 { font-weight: 600; }
    ion-item.critical { --border-color: var(--ion-color-danger); border-left: 4px solid var(--ion-color-danger); }
    ion-item.high { border-left: 4px solid var(--ion-color-warning); }
    .meta { font-size: 12px; }
    .actions { display: flex; gap: 4px; margin-top: 6px; }
  `,
})
export class InboxPage implements OnInit {
  protected readonly inbox = inject(InboxService);
  private readonly router = inject(Router);

  ngOnInit(): void {
    void this.inbox.start();
  }

  protected async refresh(event: CustomEvent): Promise<void> {
    await this.inbox.refresh();
    (event.target as HTMLIonRefresherElement).complete();
  }

  protected async open(m: InAppMessage): Promise<void> {
    await this.inbox.open(m);
    this.follow(m.deepLink);
  }

  protected async act(event: Event, m: InAppMessage, a: AppAction): Promise<void> {
    event.stopPropagation();
    await this.inbox.act(m, a);
    this.follow(a.deepLink ?? m.deepLink);
  }

  /** In-app routes only; external links would go through @capacitor/browser. */
  private follow(link: string | null | undefined): void {
    if (link && link.startsWith('/') && !link.startsWith('//')) void this.router.navigateByUrl(link);
  }
}
