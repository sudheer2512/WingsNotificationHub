import { Component, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { LookupsService } from '../core/lookups.service';
import { ErrorBanner } from '../shared/ui';

@Component({
  selector: 'app-sign-in',
  imports: [FormsModule, MatFormFieldModule, MatInputModule, MatButtonModule, MatIconModule, MatProgressBarModule, ErrorBanner],
  template: `
    <div class="wrap">
      <form class="card" (ngSubmit)="submit()">
        <div class="brand"><img src="favicon.svg" alt="" width="40" height="40"><div><h1>Wings Notification Hub</h1><div class="muted">Administration console</div></div></div>
        @if (expired()) { <div class="callout warn">Your session expired or was rejected. Sign in again.</div> }
        <mat-form-field class="full">
          <mat-label>Email</mat-label>
          <input matInput type="email" name="email" [(ngModel)]="email" autocomplete="username" required autofocus>
        </mat-form-field>
        <mat-form-field class="full">
          <mat-label>Password</mat-label>
          <input matInput [type]="show() ? 'text' : 'password'" name="password" [(ngModel)]="password" autocomplete="current-password" required>
          <button mat-icon-button matSuffix type="button" (click)="show.set(!show())" [attr.aria-label]="show() ? 'Hide password' : 'Show password'">
            <mat-icon>{{ show() ? 'visibility_off' : 'visibility' }}</mat-icon>
          </button>
        </mat-form-field>
        <div style="height:4px">@if (busy()) { <mat-progress-bar mode="indeterminate" /> }</div>
        <app-error [error]="error()" title="Sign-in failed:" />
        <button mat-flat-button class="full" type="submit" [disabled]="!email.trim() || !password || busy()">Sign in</button>
        <p class="small muted">The session is kept only in this browser tab and ends after 8 hours or when you sign out.</p>
      </form>
    </div>`,
  styles: [`
    .wrap { min-height: 100vh; display: grid; place-items: center; padding: 16px; background: var(--canvas); }
    .card { width: min(420px, 100%); background: var(--surface); border: 1px solid var(--border); border-radius: 12px; padding: 24px; display: flex; flex-direction: column; gap: 10px; }
    .brand { display: flex; gap: 12px; align-items: center; margin-bottom: 8px; }`],
})
export class SignIn {
  private auth = inject(AuthService);
  private lookups = inject(LookupsService);
  private router = inject(Router);
  returnUrl = input<string>();
  expired = input<string>();
  email = '';
  password = '';
  show = signal(false);
  busy = signal(false);
  error = signal<unknown>(null);

  submit() {
    this.busy.set(true);
    this.error.set(null);
    this.auth.signIn(this.email, this.password).subscribe({
      next: async () => {
        this.password = '';
        this.lookups.reset();
        await this.lookups.load();
        this.busy.set(false);
        this.router.navigateByUrl(this.returnUrl() || '/');
      },
      error: e => { this.busy.set(false); this.error.set(e); },
    });
  }
}
