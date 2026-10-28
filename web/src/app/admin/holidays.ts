import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { LookupsService } from '../core/lookups.service';
import { HolidayCalendar } from '../core/models';
import { UI } from '../shared/ui';

/** Parses "2026-11-08 Diwali" lines into holiday dates; returns the bad lines too. Exported for tests. */
export function parseHolidayLines(text: string): { dates: { date: string; name: string }[]; errors: string[] } {
  const dates: { date: string; name: string }[] = [], errors: string[] = [];
  for (const raw of text.split('\n')) {
    const line = raw.trim();
    if (!line) continue;
    const m = /^(\d{4}-\d{2}-\d{2})[\s,;:-]+(.+)$/.exec(line);
    if (!m || isNaN(new Date(m[1] + 'T00:00:00').getTime())) { errors.push(line); continue; }
    dates.push({ date: m[1], name: m[2].trim() });
  }
  return { dates, errors };
}

@Component({
  selector: 'app-holidays',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, ...UI],
  template: `
    <div class="page">
      <div class="page-head"><h1>Holiday calendars</h1><span class="sub">Schedules marked "working days only" skip these dates</span></div>
      <app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />
      <div class="grid-2">
        @for (c of calendars(); track c.id) {
          <section class="panel">
            <div class="panel-head"><h2>{{ c.name }}</h2><span class="tag">{{ c.country ?? '—' }}</span><span class="spacer"></span><span class="small muted">{{ c.tenantId ? lookups.tenantName(c.tenantId) : 'Platform-wide' }}</span></div>
            <div class="table-wrap" style="max-height:300px"><table class="data">
              <thead><tr><th>Date</th><th>Holiday</th></tr></thead>
              <tbody>@for (d of c.dates; track d.date) { <tr><td class="nowrap">{{ fmt(d.date) }}</td><td>{{ d.name }}</td></tr> } @empty { <tr><td colspan="2" class="muted">No dates</td></tr> }</tbody>
            </table></div>
          </section>
        } @empty { <div class="panel"><app-empty icon="event" text="No holiday calendars" /></div> }
      </div>
      @if (auth.can('ManageTenancy')) {
        <section class="panel" style="margin-top:12px">
          <h2>New calendar</h2>
          <div class="form-grid">
            <mat-form-field><mat-label>Name</mat-label><input matInput [(ngModel)]="m.name" placeholder="India 2027"></mat-form-field>
            <mat-form-field><mat-label>Country</mat-label><input matInput [(ngModel)]="m.country" placeholder="IN"></mat-form-field>
            <mat-form-field><mat-label>Tenant</mat-label><mat-select [(ngModel)]="m.tenantId">@if (auth.isPlatformAdmin()) { <mat-option value="">Platform-wide</mat-option> }
              @for (t of lookups.tenants(); track t.id) { <mat-option [value]="t.id">{{ t.name }}</mat-option> }</mat-select></mat-form-field>
            <mat-form-field class="span-2"><mat-label>Dates (one per line: yyyy-mm-dd Name)</mat-label><textarea matInput rows="6" class="code" [(ngModel)]="m.dates" placeholder="2027-01-26 Republic Day"></textarea></mat-form-field>
          </div>
          @if (bad().length) { <div class="error small">Unrecognised lines: {{ bad().join('; ') }}</div> }
          <button mat-flat-button (click)="create()" [disabled]="!m.name.trim()"><mat-icon>event_available</mat-icon>Create calendar</button>
        </section>
      }
    </div>`,
})
export class Holidays implements OnInit {
  private api = inject(ApiService);
  private snack = inject(MatSnackBar);
  auth = inject(AuthService);
  lookups = inject(LookupsService);
  calendars = signal<HolidayCalendar[]>([]);
  error = signal<unknown>(null);
  bad = signal<string[]>([]);
  m = { name: '', country: 'IN', tenantId: '', dates: '' };

  ngOnInit() { this.load(); this.m.tenantId = this.auth.isPlatformAdmin() ? '' : this.lookups.tenants()[0]?.id ?? ''; }
  load() { this.api.holidayCalendars().subscribe({ next: c => this.calendars.set(c), error: e => this.error.set(e) }); }
  fmt(d: string) { return new Date(d + 'T00:00:00').toLocaleDateString(undefined, { weekday: 'short', year: 'numeric', month: 'short', day: 'numeric' }); }
  create() {
    const { dates, errors } = parseHolidayLines(this.m.dates);
    this.bad.set(errors);
    if (errors.length) return;
    this.api.createHolidayCalendar({ tenantId: this.m.tenantId || null, name: this.m.name.trim(), country: this.m.country || null, dates }).subscribe({
      next: () => { this.snack.open('Calendar created', 'OK', { duration: 2500 }); this.m = { ...this.m, name: '', dates: '' }; this.load(); }, error: e => this.error.set(e),
    });
  }
}
