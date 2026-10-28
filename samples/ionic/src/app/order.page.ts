import { Component, input } from '@angular/core';
import { IonBackButton, IonButtons, IonContent, IonHeader, IonTitle, IonToolbar } from '@ionic/angular';

/** Deep-link target for "/sales/orders/:id" (ORDER_APPROVED notifications). */
@Component({
  selector: 'app-order',
  imports: [IonHeader, IonToolbar, IonButtons, IonBackButton, IonTitle, IonContent],
  template: `
    <ion-header><ion-toolbar><ion-buttons slot="start"><ion-back-button defaultHref="/inbox" /></ion-buttons><ion-title>Order {{ id() }}</ion-title></ion-toolbar></ion-header>
    <ion-content class="ion-padding"><p>Sales order {{ id() }} details would be shown here.</p></ion-content>
  `,
})
export class OrderPage {
  readonly id = input<string>();
}
