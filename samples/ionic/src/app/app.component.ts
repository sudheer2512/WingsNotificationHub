import { Component, inject, type OnInit } from '@angular/core';
import { IonApp, IonRouterOutlet } from '@ionic/angular';
import { PushService } from './push.service';

@Component({
  selector: 'app-root',
  imports: [IonApp, IonRouterOutlet],
  template: `<ion-app><ion-router-outlet /></ion-app>`,
})
export class AppComponent implements OnInit {
  private readonly push = inject(PushService);

  ngOnInit(): void {
    // After sign-in in a real app; the sample has a fixed demo user.
    void this.push.init();
  }
}
