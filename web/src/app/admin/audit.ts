import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatSelectModule } from '@angular/material/select';
import { ApiService } from '../core/api.service';
import { LookupsService } from '../core/lookups.service';
import { AuditEntry } from '../core/models';
import { UI } from '../shared/ui';

@Component({
  selector: 'app-audit',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatPaginatorModule, MatSelectModule, ...UI],
  template: `
    <div class="page">
      <div class="page-head"><h1>Audit log</h1><span class="sub">{{ total() | num }} entries</span><span class="spacer"></span><button mat-stroked-button (click)="load()"><mat-icon>refresh</mat-icon>Refresh</button></div>
      <div class="filters">
        @if (lookups.tenants().length > 1) {
          <mat-form-field><mat-label>Tenant</mat-label><mat-select [(ngModel)]="tenantId" (selectionChange)="search()"><mat-option value="">All</mat-option>
            @for (t of lookups.tenants(); track t.id) { <mat-option [value]="t.id">{{ t.name }}</mat-option> }</mat-select></mat-form-field>
        }
        <mat-form-field><mat-label>Action starts with</mat-label><input matInput [(ngModel)]="action" (keyup.enter)="search()" placeholder="template."></mat-form-field>
        <mat-form-field><mat-label>Entity type</mat-label><input matInput [(ngModel)]="entityType" (keyup.enter)="search()" placeholder="NotificationTemplate"></mat-form-field>
        <mat-form-field class="wide"><mat-label>Entity id</mat-label><input matInput [(ngModel)]="entityId" (keyup.enter)="search()"></mat-form-field>
        <mat-form-field style="width:150px"><mat-label>From</mat-label><input matInput type="date" [(ngModel)]="from" (change)="search()"></mat-form-field>
        <button mat-stroked-button (click)="search()"><mat-icon>search</mat-icon>Search</button>
      </div>
      <app-loading [on]="loading()" />
      <app-error [error]="error()" />
      <div class="table-wrap">
        <table class="data">
          <thead><tr><th>Time</th><th>Actor</th><th>Action</th><th>Entity</th><th>Summary</th><th>Scope</th><th>Correlation</th></tr></thead>
          <tbody>
            @for (a of items(); track a.id) {
              <tr class="clickable" (click)="open.set(open() === a.id ? null : a.id)">
                <td class="nowrap small" [title]="a.occurredAt | dt">{{ a.occurredAt | dt }}</td>
                <td>{{ a.actorName ?? a.actorId }} <span class="tag">{{ a.actorType }}</span></td>
                <td><code>{{ a.action }}</code></td>
                <td class="small">{{ a.entityType }}@if (a.entityId) { <br><code>{{ a.entityId | short:12 }}</code> }</td>
                <td class="small" style="max-width:340px">{{ $any(a)['summary'] ?? '' }}</td>
                <td class="small">{{ a.tenantId ? lookups.tenantName(a.tenantId) : 'platform' }}@if (a.companyId) { / {{ lookups.companyName(a.companyId) }} }</td>
                <td><code class="small">{{ a.correlationId ?? '—' }}</code></td>
              </tr>
              @if (open() === a.id) {
                <tr><td colspan="7"><pre class="json">{{ detail(a) }}</pre></td></tr>
              }
            } @empty { @if (!loading()) { <tr><td colspan="7"><app-empty icon="receipt_long" text="No audit entries" /></td></tr> } }
          </tbody>
        </table>
      </div>
      <mat-paginator [length]="total()" [pageIndex]="page - 1" [pageSize]="100" [hidePageSize]="true" (page)="onPage($event)" showFirstLastButtons />
    </div>`,
})
export class Audit implements OnInit {
  private api = inject(ApiService);
  lookups = inject(LookupsService);
  items = signal<AuditEntry[]>([]);
  total = signal(0);
  loading = signal(false);
  error = signal<unknown>(null);
  open = signal<string | null>(null);
  tenantId = ''; action = ''; entityType = ''; entityId = ''; from = ''; page = 1;

  ngOnInit() { this.load(); }
  search() { this.page = 1; this.load(); }
  onPage(e: PageEvent) { this.page = e.pageIndex + 1; this.load(); }
  load() {
    this.loading.set(true);
    this.api.audit({ tenantId: this.tenantId || undefined, action: this.action.trim() || undefined, entityType: this.entityType.trim() || undefined, entityId: this.entityId.trim() || undefined,
      from: this.from ? new Date(this.from + 'T00:00:00').toISOString() : undefined, page: this.page }).subscribe({
      next: r => { this.items.set(r.items); this.total.set(r.total); this.loading.set(false); this.error.set(null); },
      error: e => { this.error.set(e); this.loading.set(false); },
    });
  }
  detail(a: AuditEntry) {
    const raw = (a as Record<string, unknown>)['detailJson'];
    let detail: unknown = raw;
    if (typeof raw === 'string') { try { detail = JSON.parse(raw); } catch { detail = raw; } }
    return JSON.stringify({ ...a, detailJson: undefined, detail }, null, 2);
  }
}
