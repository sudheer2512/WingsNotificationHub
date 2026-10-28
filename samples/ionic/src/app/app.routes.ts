import type { Routes } from '@angular/router';
import { InboxPage } from './inbox.page';
import { OrderPage } from './order.page';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'inbox' },
  { path: 'inbox', component: InboxPage },
  { path: 'sales/orders/:id', component: OrderPage },
];
