import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { LookupsService } from '../core/lookups.service';
import { Application, Campaign, Company, Tenant } from '../core/models';
import { UI } from '../shared/ui';
import { confirmDialog } from '../shared/widgets';

type Kind = 'tenant' | 'company' | 'application' | 'campaign';
interface Row { kind: Kind; id: string; name: string; parent: string; suspended: boolean; reason?: string | null; }

/** Emergency suspension: holds pending messages (not lost) until resumed. Providers and senders are suspended on the Providers screen. */
@Component({
  selector: 'app-suspension',
  imports: [FormsModule, MatButtonModule, MatIconModule, RouterLink, ...UI],
  template: `
    <div class="page">
      <div class="page-head"><h1>Emergency suspension</h1><span class="spacer"></span><button mat-stroked-button (click)="load()"><mat-icon>refresh</mat-icon>Refresh</button></div>
      <div class="callout warn">Suspending stops new sends immediately; pending messages are held, not lost, and continue when resumed. Every action is audited and raises a critical alert.
        Provider accounts and sender identities are suspended on the <a routerLink="/providers">Providers</a> screen.</div>
      <app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />
      @if (suspendedRows().length) {
        <section class="panel" style="margin-bottom:12px">
          <h2>Currently suspended</h2>
          @for (r of suspendedRows(); track r.kind + r.id) {
            <div class="row" style="padding:4px 0"><app-chip value="Suspended" /><span class="tag">{{ r.kind }}</span><b>{{ r.name }}</b><span class="small muted">{{ r.parent }} {{ r.reason ? '· ' + r.reason : '' }}</span>
              <span class="spacer"></span><button mat-stroked-button (click)="toggle(r)"><mat-icon>play_arrow</mat-icon>Resume</button></div>
          }
        </section>
      }
      @for (g of groups(); track g.kind) {
        <section class="panel" style="margin-bottom:12px">
          <h2>{{ g.title }}</h2>
          <div class="table-wrap"><table class="data">
            <thead><tr><th>Name</th><th>Scope</th><th>Status</th><th></th></tr></thead>
            <tbody>@for (r of g.rows; track r.id) {
              <tr><td>{{ r.name }}</td><td class="small">{{ r.parent }}</td><td>@if (r.suspended) { <app-chip value="Suspended" [tip]="r.reason ?? null" /> } @else { <app-chip tone="good" label="Active" /> }</td>
                <td>@if (r.kind !== 'tenant' || auth.isPlatformAdmin()) {
                  <button mat-stroked-button [class.danger]="!r.suspended" (click)="toggle(r)"><mat-icon>{{ r.suspended ? 'play_arrow' : 'block' }}</mat-icon>{{ r.suspended ? 'Resume' : 'Suspend' }}</button> }</td></tr>
            } @empty { <tr><td colspan="4" class="muted">None</td></tr> }</tbody>
          </table></div>
        </section>
      }
    </div>`,
})
export class Suspension implements OnInit {
  private api = inject(ApiService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  auth = inject(AuthService);
  lookups = inject(LookupsService);
  tenants = signal<Tenant[]>([]);
  companies = signal<Company[]>([]);
  apps = signal<Application[]>([]);
  campaigns = signal<Campaign[]>([]);
  error = signal<unknown>(null);

  groups = computed(() => [
    { kind: 'tenant', title: 'Tenants', rows: this.tenants().map(t => ({ kind: 'tenant' as Kind, id: t.id, name: t.name, parent: t.code, suspended: t.status === 'Suspended', reason: t.suspendedReason })) },
    { kind: 'company', title: 'Companies', rows: this.companies().map(c => ({ kind: 'company' as Kind, id: c.id, name: c.name, parent: this.lookups.tenantName(c.tenantId), suspended: c.status === 'Suspended', reason: c.suspendedReason })) },
    { kind: 'application', title: 'Applications', rows: this.apps().map(a => ({ kind: 'application' as Kind, id: a.id, name: a.name, parent: this.lookups.tenantName(a.tenantId), suspended: a.status === 'Suspended', reason: a.suspendedReason })) },
    { kind: 'campaign', title: 'Active campaigns', rows: this.campaigns().filter(c => !['Completed', 'Cancelled'].includes(c.status))
      .map(c => ({ kind: 'campaign' as Kind, id: c.id, name: c.name, parent: `${this.lookups.companyName(c.companyId)} · ${c.status}`, suspended: c.suspended })) },
  ] as { kind: Kind; title: string; rows: Row[] }[]);
  suspendedRows = computed(() => this.groups().flatMap(g => g.rows).filter(r => r.suspended));

  ngOnInit() { this.load(); }
  load() {
    if (this.auth.can('ViewTenancy')) {
      this.api.tenants().subscribe({ next: t => this.tenants.set(t), error: e => this.error.set(e) });
      this.api.companies().subscribe({ next: c => this.companies.set(c), error: e => this.error.set(e) });
      this.api.applications().subscribe({ next: a => this.apps.set(a), error: e => this.error.set(e) });
    }
    if (this.auth.can('ViewCampaigns')) this.api.campaigns().subscribe({ next: c => this.campaigns.set(c), error: e => this.error.set(e) });
  }
  toggle(r: Row) {
    const on = !r.suspended;
    confirmDialog(this.dialog, { title: `${on ? 'Suspend' : 'Resume'} ${r.kind} "${r.name}"`, danger: on, confirmText: on ? 'Suspend now' : 'Resume',
      message: on ? 'New and pending messages are held until resumed.' : 'Held messages resume processing.', comment: { label: 'Reason (audited)', required: on },
      acknowledge: on && r.kind === 'tenant' ? 'I understand this stops all messaging for every company in the tenant.' : undefined }).subscribe(res => {
      if (!res) return;
      this.api.suspend(r.kind, r.id, on, res.comment || undefined).subscribe({
        next: () => { this.snack.open(`${r.name} ${on ? 'suspended' : 'resumed'}`, 'OK', { duration: 3000 }); this.load(); this.lookups.refresh(); }, error: e => this.error.set(e),
      });
    });
  }
}
