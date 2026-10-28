import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { AuthService } from '../core/auth.service';
import { EmptyState } from '../shared/ui';

@Component({
  selector: 'app-no-access',
  imports: [EmptyState, MatButtonModule],
  template: `<div class="page"><app-empty icon="lock" text="No screens available" hint="Your account has no role grants that include console permissions. Ask an administrator to grant a role.">
    <button mat-stroked-button (click)="auth.signOut()" style="margin-top:12px">Sign out</button></app-empty></div>`,
})
export class NoAccess { auth = inject(AuthService); }
