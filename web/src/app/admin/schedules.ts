import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { LookupsService } from '../core/lookups.service';
import { CHANNELS, Channel, HolidayCalendar, NotificationType, Schedule } from '../core/models';
import { UI } from '../shared/ui';
import { JsonDialog, confirmDialog } from '../shared/widgets';

@Component({
  selector: 'app-schedules',
  imports: [FormsModule, MatButtonModule, MatButtonToggleModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, MatSlideToggleModule, ...UI],
  template: `
    <div class="page">
      <div class="page-head"><h1>Schedules</h1><span class="sub">Recurring notifications and reminder sequences (time-zone, working-day and holiday aware)</span><span class="spacer"></span>
        <button mat-stroked-button (click)="load()"><mat-icon>refresh</mat-icon>Refresh</button></div>
      <app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />
      <section class="panel">
        <div class="table-wrap"><table class="data">
          <thead><tr><th>Name</th><th>Company / application</th><th>Pattern</th><th>Time zone</th><th>Next run</th><th>Last run</th><th class="num">Runs</th><th>Enabled</th><th></th></tr></thead>
          <tbody>@for (s of schedules(); track s.id) {
            <tr><td><b>{{ s.name }}</b>@if (s.workingDaysOnly) { <span class="tag">working days</span> }@if (s.holidayCalendarId) { <span class="tag">{{ calName(s.holidayCalendarId) }}</span> }</td>
              <td class="small">{{ lookups.companyName(s.companyId) }}<br><span class="muted">{{ lookups.appName(s.applicationId) }}</span></td>
              <td>@if (s.cronExpression) { <code>{{ s.cronExpression }}</code> } @else { sequence {{ (s.sequence ?? []).join(', ') }} min (step {{ s.sequenceIndex }}) }</td>
              <td class="small">{{ s.timeZone }}</td><td class="small nowrap">{{ s.nextRunAt ? (s.nextRunAt | dt) : '—' }}</td><td class="small nowrap">{{ s.lastRunAt ? (s.lastRunAt | ago) : 'never' }}</td>
              <td class="num">{{ s.runCount }}@if (s.maxRuns) { / {{ s.maxRuns }} }</td>
              <td><mat-slide-toggle [checked]="s.enabled" (change)="toggle(s, $event.checked)" [disabled]="!canSend()" [attr.aria-label]="'Enable ' + s.name" /></td>
              <td class="nowrap"><button mat-icon-button (click)="view(s)" aria-label="View request"><mat-icon>data_object</mat-icon></button>
                @if (canSend()) { <button mat-icon-button (click)="remove(s)" aria-label="Delete schedule"><mat-icon>delete</mat-icon></button> }</td></tr>
          } @empty { <tr><td colspan="9" class="muted">No schedules</td></tr> }</tbody>
        </table></div>
      </section>
      @if (canSend()) {
        <section class="panel" style="margin-top:12px">
          <h2>New schedule</h2>
          <div class="form-grid">
            <mat-form-field><mat-label>Name</mat-label><input matInput [(ngModel)]="m.name"></mat-form-field>
            <mat-form-field><mat-label>Company</mat-label><mat-select [(ngModel)]="m.companyId" (selectionChange)="m.applicationId = lookups.appsForCompany(m.companyId)[0]?.id ?? ''">
              @for (c of lookups.companies(); track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }</mat-select></mat-form-field>
            <mat-form-field><mat-label>Application</mat-label><mat-select [(ngModel)]="m.applicationId">@for (a of lookups.appsForCompany(m.companyId); track a.id) { <mat-option [value]="a.id">{{ a.name }}</mat-option> }</mat-select></mat-form-field>
            <mat-form-field><mat-label>Time zone</mat-label><input matInput [(ngModel)]="m.timeZone"></mat-form-field>
          </div>
          <mat-button-toggle-group [(ngModel)]="m.kind" hideSingleSelectionIndicator aria-label="Schedule kind">
            <mat-button-toggle value="cron">Recurring (cron)</mat-button-toggle><mat-button-toggle value="sequence">Reminder sequence</mat-button-toggle>
          </mat-button-toggle-group>
          <div class="form-grid" style="margin-top:8px">
            @if (m.kind === 'cron') { <mat-form-field><mat-label>Cron expression</mat-label><input matInput [(ngModel)]="m.cron" placeholder="0 9 * * MON-FRI"><mat-hint>minute hour day month weekday</mat-hint></mat-form-field> }
            @else { <mat-form-field><mat-label>Offsets in minutes</mat-label><input matInput [(ngModel)]="m.sequence" placeholder="0, 1440, 4320"><mat-hint>Reminders at start + each offset</mat-hint></mat-form-field> }
            <mat-form-field><mat-label>Start</mat-label><input matInput type="datetime-local" [(ngModel)]="m.startAt"></mat-form-field>
            <mat-form-field><mat-label>End (optional)</mat-label><input matInput type="datetime-local" [(ngModel)]="m.endAt"></mat-form-field>
            <mat-form-field><mat-label>Max runs (optional)</mat-label><input matInput type="number" min="1" [(ngModel)]="m.maxRuns"></mat-form-field>
            <mat-form-field><mat-label>Holiday calendar</mat-label><mat-select [(ngModel)]="m.holidayCalendarId"><mat-option value="">None</mat-option>
              @for (c of calendars(); track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }</mat-select></mat-form-field>
          </div>
          <mat-checkbox [(ngModel)]="m.workingDaysOnly">Working days only (Mon–Fri)</mat-checkbox>
          <h3 style="margin:10px 0 4px">Notification</h3>
          <div class="form-grid">
            <mat-form-field><mat-label>Notification type</mat-label><mat-select [(ngModel)]="m.notificationType"><mat-option value="">— template only —</mat-option>
              @for (t of types(); track t.id) { <mat-option [value]="t.code">{{ t.code }}</mat-option> }</mat-select></mat-form-field>
            <mat-form-field><mat-label>Template code (optional)</mat-label><input matInput [(ngModel)]="m.templateCode"></mat-form-field>
            <mat-form-field><mat-label>Channels (empty = type default)</mat-label><mat-select [(ngModel)]="m.channels" multiple>@for (c of channels; track c) { <mat-option [value]="c">{{ c | channel }}</mat-option> }</mat-select></mat-form-field>
            <mat-form-field><mat-label>Recipient email</mat-label><input matInput [(ngModel)]="m.email"></mat-form-field>
            <mat-form-field><mat-label>Recipient mobile</mat-label><input matInput [(ngModel)]="m.mobile"></mat-form-field>
            <mat-form-field><mat-label>Recipient user id</mat-label><input matInput [(ngModel)]="m.userId"></mat-form-field>
            <mat-form-field class="span-2"><mat-label>Variables (JSON)</mat-label><textarea matInput rows="4" class="code" [(ngModel)]="m.variables"></textarea></mat-form-field>
          </div>
          <div class="small muted">The request is validated with a dry run before the schedule is saved.</div>
          <button mat-flat-button (click)="create()" [disabled]="busy() || !m.name || !m.companyId || !m.applicationId"><mat-icon>add_alarm</mat-icon>Create schedule</button>
        </section>
      }
    </div>`,
})
export class Schedules implements OnInit {
  private api = inject(ApiService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  auth = inject(AuthService);
  lookups = inject(LookupsService);
  channels = CHANNELS;
  schedules = signal<Schedule[]>([]);
  calendars = signal<HolidayCalendar[]>([]);
  types = signal<NotificationType[]>([]);
  busy = signal(false);
  error = signal<unknown>(null);
  canSend = computed(() => this.auth.can('SendNotifications'));
  m = {
    name: '', companyId: '', applicationId: '', timeZone: 'Asia/Kolkata', kind: 'cron', cron: '0 9 * * MON-FRI', sequence: '0, 1440, 4320', startAt: '', endAt: '', maxRuns: null as number | null,
    holidayCalendarId: '', workingDaysOnly: true, notificationType: '', templateCode: '', channels: [] as Channel[], email: '', mobile: '', userId: '', variables: '{\n  "CustomerName": "Ravi"\n}',
  };

  ngOnInit() {
    this.load();
    this.api.holidayCalendars().subscribe({ next: c => this.calendars.set(c), error: () => undefined });
    if (this.auth.can('ViewTemplates')) this.api.notificationTypes().subscribe({ next: t => this.types.set(t), error: () => undefined });
    this.m.companyId = this.lookups.companies().find(c => this.lookups.appsForCompany(c.id).length)?.id ?? '';
    this.m.applicationId = this.lookups.appsForCompany(this.m.companyId)[0]?.id ?? '';
  }
  load() { this.api.schedules().subscribe({ next: s => this.schedules.set(s), error: e => this.error.set(e) }); }
  calName(id: string) { return this.calendars().find(c => c.id === id)?.name ?? 'holiday calendar'; }
  view(s: Schedule) { this.dialog.open(JsonDialog, { data: { title: `${s.name}: notification request`, value: s.request }, width: '640px', maxWidth: '96vw' }); }
  toggle(s: Schedule, enabled: boolean) { this.api.setScheduleEnabled(s.id, enabled).subscribe({ next: () => this.load(), error: e => { this.error.set(e); this.load(); } }); }
  remove(s: Schedule) {
    confirmDialog(this.dialog, { title: 'Delete schedule', message: `Delete "${s.name}"? Future runs are cancelled.`, confirmText: 'Delete', danger: true }).subscribe(r => {
      if (r) this.api.deleteSchedule(s.id).subscribe({ next: () => this.load(), error: e => this.error.set(e) });
    });
  }
  create() {
    let variables: unknown = null;
    try { variables = this.m.variables.trim() ? JSON.parse(this.m.variables) : null; } catch (e) { this.error.set(new Error('Variables must be valid JSON: ' + (e as Error).message)); return; }
    const m = this.m;
    const recipient = Object.fromEntries(Object.entries({ email: m.email, mobile: m.mobile, userId: m.userId }).filter(([, v]) => !!v.trim()));
    const body = {
      companyId: m.companyId, applicationId: m.applicationId, name: m.name, timeZone: m.timeZone, cronExpression: m.kind === 'cron' ? m.cron : null,
      sequenceOffsetsMinutes: m.kind === 'sequence' ? m.sequence.split(',').map(x => Number(x.trim())).filter(x => !isNaN(x)) : null,
      startAt: m.startAt ? new Date(m.startAt).toISOString() : null, endAt: m.endAt ? new Date(m.endAt).toISOString() : null, maxRuns: m.maxRuns || null,
      workingDaysOnly: m.workingDaysOnly, holidayCalendarId: m.holidayCalendarId || null,
      notification: { companyId: m.companyId, applicationId: m.applicationId, notificationType: m.notificationType || null, templateCode: m.templateCode || null,
        channels: m.channels.length ? m.channels : null, recipient, variables },
    };
    this.busy.set(true); this.error.set(null);
    this.api.createSchedule(body).subscribe({
      next: r => { this.busy.set(false); this.snack.open(`Schedule created; next run ${r.nextRunAt ? new Date(r.nextRunAt).toLocaleString() : 'not scheduled'}`, 'OK', { duration: 4000 }); this.load(); },
      error: e => { this.busy.set(false); this.error.set(e); },
    });
  }
}
