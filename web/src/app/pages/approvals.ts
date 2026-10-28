import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { LookupsService } from '../core/lookups.service';
import { Approval } from '../core/models';
import { UI } from '../shared/ui';
import { confirmDialog } from '../shared/widgets';

const KIND: Record<string, { label: string; icon: string; link?: (a: Approval) => { path: string; query?: Record<string, string> } }> = {
  TemplateVersion: { label: 'Template version', icon: 'description', link: () => ({ path: '/templates' }) },
  Campaign: { label: 'Campaign', icon: 'campaign', link: a => ({ path: '/campaigns', query: { id: a.entityId } }) },
  ProviderCredentials: { label: 'Provider credentials', icon: 'key', link: a => ({ path: '/providers', query: { id: a.entityId } }) },
  AiNotification: { label: 'AI notification', icon: 'smart_toy', link: a => ({ path: '/explorer', query: { notificationId: a.entityId } }) },
};

/** Maker-checker inbox. The API refuses self-approval; the UI disables it up front (canDecide). */
@Component({
  selector: 'app-approvals',
  imports: [FormsModule, MatButtonModule, MatButtonToggleModule, MatIconModule, MatTooltipModule, RouterLink, ...UI],
  template: `
    <div class="page">
      <div class="page-head">
        <h1>Approvals</h1><span class="sub">Maker-checker: requests must be decided by someone other than the requester</span><span class="spacer"></span>
        <mat-button-toggle-group [(ngModel)]="status" (change)="load()" hideSingleSelectionIndicator aria-label="Status">
          <mat-button-toggle value="Pending">Pending</mat-button-toggle><mat-button-toggle value="Approved">Approved</mat-button-toggle>
          <mat-button-toggle value="Rejected">Rejected</mat-button-toggle><mat-button-toggle value="Cancelled">Cancelled</mat-button-toggle>
        </mat-button-toggle-group>
        <button mat-stroked-button (click)="load()"><mat-icon>refresh</mat-icon>Refresh</button>
      </div>
      <app-loading [on]="loading()" />
      <app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />
      <div class="table-wrap">
        <table class="data">
          <thead><tr><th>Kind</th><th>Request</th><th>Tenant</th><th>Requested by</th><th>Requested</th>@if (status !== 'Pending') {<th>Decision</th>}<th></th></tr></thead>
          <tbody>
            @for (a of items(); track a.id) {
              <tr>
                <td class="nowrap"><span class="chan"><mat-icon>{{ kind(a).icon }}</mat-icon>{{ kind(a).label }}</span></td>
                <td style="min-width:240px">{{ a.title }}@if (link(a); as l) { <div><a [routerLink]="l.path" [queryParams]="l.query ?? null" class="small">open</a></div> }</td>
                <td class="small">{{ lookups.tenantName(a.tenantId) }}</td>
                <td>{{ a.requestedByName ?? a.requestedBy }}</td>
                <td class="nowrap small">{{ a.requestedAt | dt }}</td>
                @if (status !== 'Pending') { <td class="small"><app-chip [value]="a.status" /> {{ a.decidedAt | dt }}@if (a.comment) { <div class="muted">“{{ a.comment }}”</div> }</td> }
                <td class="nowrap">
                  @if (a.status === 'Pending') {
                    <button mat-flat-button (click)="decide(a, true)" [disabled]="!a.canDecide || busy()" [matTooltip]="a.canDecide ? '' : 'You raised this request; another approver must decide it.'"><mat-icon>check</mat-icon>Approve</button>
                    <button mat-stroked-button class="danger" (click)="decide(a, false)" [disabled]="!a.canDecide || busy()"><mat-icon>close</mat-icon>Reject</button>
                    @if (!a.canDecide) { <div class="small muted">Your own request</div> }
                  }
                </td>
              </tr>
            } @empty { <tr><td colspan="7"><app-empty icon="fact_check" [text]="status === 'Pending' ? 'Nothing waiting for approval' : 'No ' + status.toLowerCase() + ' requests'" /></td></tr> }
          </tbody>
        </table>
      </div>
    </div>`,
})
export class Approvals implements OnInit {
  private api = inject(ApiService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  lookups = inject(LookupsService);
  status = 'Pending';
  items = signal<Approval[]>([]);
  loading = signal(false);
  busy = signal(false);
  error = signal<unknown>(null);

  ngOnInit() { this.load(); }

  load() {
    this.loading.set(true);
    this.api.approvals(this.status).subscribe({
      next: a => { this.items.set(a); this.loading.set(false); this.error.set(null); },
      error: e => { this.error.set(e); this.loading.set(false); },
    });
  }

  kind(a: Approval) { return KIND[a.kind] ?? { label: a.kind, icon: 'help' }; }
  link(a: Approval) { return KIND[a.kind]?.link?.(a) ?? null; }

  decide(a: Approval, approve: boolean) {
    const effect = { TemplateVersion: 'Approving activates the template version.', Campaign: 'Approving schedules or starts the campaign.',
      ProviderCredentials: 'Approving replaces the stored provider credentials.', AiNotification: 'Approving sends the AI-generated notification.' }[a.kind] ?? '';
    confirmDialog(this.dialog, { title: `${approve ? 'Approve' : 'Reject'}: ${a.title}`, message: approve ? effect : 'The requester is notified of the rejection through the audit trail.',
      confirmText: approve ? 'Approve' : 'Reject', danger: !approve, comment: { label: approve ? 'Comment (optional)' : 'Reason', required: !approve } }).subscribe(r => {
      if (!r) return;
      this.busy.set(true);
      this.api.decide(a.id, approve, r.comment || undefined).subscribe({
        next: () => { this.busy.set(false); this.snack.open(approve ? 'Approved' : 'Rejected', 'OK', { duration: 3000 }); this.load(); },
        error: e => { this.busy.set(false); this.error.set(e); this.load(); },
      });
    });
  }
}
