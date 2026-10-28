import { BreakpointObserver } from '@angular/cdk/layout';
import { Component, computed, effect, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { MatBadgeModule } from '@angular/material/badge';
import { MatButtonModule } from '@angular/material/button';
import { MatDividerModule } from '@angular/material/divider';
import { MatIconModule } from '@angular/material/icon';
import { MatListModule } from '@angular/material/list';
import { MatMenuModule } from '@angular/material/menu';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatToolbarModule } from '@angular/material/toolbar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { map } from 'rxjs';
import { AuthService } from './core/auth.service';
import { LookupsService } from './core/lookups.service';
import { RealtimeService } from './core/realtime.service';
import { ThemeService } from './core/theme.service';
import { NAV } from './nav';
import { UI, humanize } from './shared/ui';

@Component({
  selector: 'app-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, MatSidenavModule, MatToolbarModule, MatListModule, MatIconModule, MatButtonModule,
    MatMenuModule, MatTooltipModule, MatDividerModule, MatSlideToggleModule, MatBadgeModule, ...UI],
  template: `
    <mat-sidenav-container class="shell">
      <mat-sidenav #nav [mode]="mobile() ? 'over' : 'side'" [opened]="!mobile()" class="nav">
        <a class="brand" routerLink="/" (click)="mobile() && nav.close()">
          <img src="favicon.svg" alt="" width="28" height="28">
          <span><b>Wings</b> Notification Hub</span>
        </a>
        <nav class="nav-scroll">
          @for (s of sections(); track s.title) {
            @if (s.title) { <div class="section">{{ s.title }}</div> }
            <mat-nav-list>
              @for (item of s.items; track item.path) {
                <a mat-list-item [routerLink]="item.path" routerLinkActive="active" (click)="mobile() && nav.close()">
                  <mat-icon matListItemIcon>{{ item.icon }}</mat-icon><span matListItemTitle>{{ item.label }}</span>
                </a>
              }
            </mat-nav-list>
          }
        </nav>
        <div class="foot small"><a href="/api-docs" target="_blank" rel="noopener">API reference</a></div>
      </mat-sidenav>
      <mat-sidenav-content class="content">
        <mat-toolbar class="bar">
          @if (mobile()) { <button mat-icon-button (click)="nav.toggle()" aria-label="Menu"><mat-icon>menu</mat-icon></button> }
          <span class="spacer"></span>
          <span class="live" [matTooltip]="rt.connected() ? 'Live updates connected' : 'Live updates disconnected (retrying)'">
            <span class="dot" [class.on]="rt.connected()"></span><span class="live-l">{{ rt.connected() ? 'Live' : 'Offline' }}</span>
          </span>
          <button mat-icon-button (click)="theme.cycle()" [matTooltip]="'Theme: ' + theme.mode()" [attr.aria-label]="'Theme: ' + theme.mode()">
            <mat-icon>{{ theme.mode() === 'dark' ? 'dark_mode' : theme.mode() === 'light' ? 'light_mode' : 'contrast' }}</mat-icon>
          </button>
          <button mat-icon-button [matMenuTriggerFor]="events" (menuOpened)="rt.unseen.set(0)" aria-label="Live events" matTooltip="Live events">
            <mat-icon aria-hidden="false" aria-label="Live events" [matBadge]="rt.unseen() || null" matBadgeSize="small">notifications</mat-icon>
          </button>
          <mat-menu #events="matMenu" class="events-menu">
            <div class="menu-head" (click)="$event.stopPropagation()">
              <mat-slide-toggle [checked]="rt.toastsEnabled()" (change)="rt.toastsEnabled.set($event.checked)">Pop-ups for customer replies</mat-slide-toggle>
            </div>
            <mat-divider />
            @for (e of rt.recent().slice(0, 15); track $index) {
              <a mat-menu-item [routerLink]="e.link ?? null" [queryParams]="e.query ?? null">
                <app-chip [tone]="e.tone" [label]="e.kind === 'inbound' ? 'Reply' : 'Delivery'" />
                <span class="small"> {{ e.text }}</span><br><span class="small muted">{{ e.at | ago }}</span>
              </a>
            } @empty { <div class="small muted" style="padding:12px 16px">Failed deliveries and customer replies appear here as they happen.</div> }
          </mat-menu>
          <button mat-button [matMenuTriggerFor]="user" class="user-btn">
            <mat-icon>account_circle</mat-icon>
            <span class="uname">{{ auth.displayName() }}</span>
          </button>
          <mat-menu #user="matMenu">
            <div class="menu-head small" (click)="$event.stopPropagation()">
              <div><b>{{ auth.displayName() }}</b></div>
              <div class="muted">{{ auth.session()?.user?.email }}</div>
              <div style="margin-top:6px">
                @for (g of auth.me()?.grants ?? []; track $index) { <div><span class="tag">{{ role(g.role) }}</span> <span class="muted">{{ scope(g) }}</span></div> }
              </div>
            </div>
            <mat-divider />
            <button mat-menu-item (click)="theme.set('system')"><mat-icon>contrast</mat-icon>System theme</button>
            <button mat-menu-item (click)="theme.set('light')"><mat-icon>light_mode</mat-icon>Light theme</button>
            <button mat-menu-item (click)="theme.set('dark')"><mat-icon>dark_mode</mat-icon>Dark theme</button>
            <mat-divider />
            <button mat-menu-item (click)="auth.signOut()"><mat-icon>logout</mat-icon>Sign out</button>
          </mat-menu>
        </mat-toolbar>
        <main><router-outlet /></main>
      </mat-sidenav-content>
    </mat-sidenav-container>`,
  styles: [`
    .shell { height: 100vh; }
    .nav { width: var(--shell-w); background: var(--chrome); color: var(--chrome-fg); border-right: 0; display: flex; flex-direction: column; }
    .nav ::ng-deep .mat-drawer-inner-container { display: flex; flex-direction: column; }
    .brand { display: flex; align-items: center; gap: 10px; padding: 14px 16px; color: var(--chrome-fg); text-decoration: none; font-size: 15px; flex: none; }
    .nav-scroll { flex: 1; overflow-y: auto; padding-bottom: 8px; }
    .nav .mat-mdc-nav-list { padding: 0; --mat-list-list-item-label-text-color: var(--chrome-muted); --mat-list-list-item-leading-icon-color: var(--chrome-muted);
      --mat-list-list-item-hover-label-text-color: #fff; --mat-list-list-item-one-line-container-height: 38px; }
    .nav a.active { background: rgba(255,255,255,.12); --mat-list-list-item-label-text-color: #fff; --mat-list-list-item-leading-icon-color: #fff; }
    .section { padding: 12px 16px 4px; font-size: 11px; text-transform: uppercase; letter-spacing: .06em; color: var(--chrome-muted); opacity: .8; }
    .foot { padding: 8px 16px 12px; flex: none; } .foot a { color: var(--chrome-muted); }
    .content { background: var(--canvas); display: flex; flex-direction: column; }
    .bar { position: sticky; top: 0; z-index: 5; background: var(--surface); border-bottom: 1px solid var(--border); height: 52px; gap: 2px; font-size: 13px; padding: 0 8px; }
    .live { display: inline-flex; align-items: center; gap: 6px; color: var(--muted); font-size: 12px; margin-right: 4px; }
    .dot { width: 8px; height: 8px; border-radius: 50%; background: var(--off); } .dot.on { background: var(--good); }
    .menu-head { padding: 8px 16px; max-width: 360px; }
    .uname { max-width: 180px; overflow: hidden; text-overflow: ellipsis; display: inline-block; vertical-align: middle; }
    @media (max-width: 600px) { .uname, .live-l { display: none; } }
    main { flex: 1; min-width: 0; }`],
})
export class Shell {
  auth = inject(AuthService);
  rt = inject(RealtimeService);
  theme = inject(ThemeService);
  private lookups = inject(LookupsService);
  mobile = toSignal(inject(BreakpointObserver).observe('(max-width: 900px)').pipe(map(r => r.matches)), { initialValue: false });
  sections = computed(() => {
    this.auth.permissions();
    return NAV.map(s => ({ title: s.title ?? '', items: s.items.filter(i => this.auth.can(...i.perms)) })).filter(s => s.items.length);
  });

  constructor() {
    effect(() => { if (this.auth.me()) this.lookups.ensure(); });
  }

  role(r: string) { return humanize(r); }
  scope(g: { tenantId?: string | null; companyId?: string | null; applicationId?: string | null; channel?: string | null }) {
    const parts = [g.tenantId ? this.lookups.tenantName(g.tenantId) : 'all tenants'];
    if (g.companyId) parts.push(this.lookups.companyName(g.companyId));
    if (g.applicationId) parts.push(this.lookups.appName(g.applicationId));
    if (g.channel) parts.push(g.channel);
    return parts.join(' / ');
  }
}
