import { Component, ElementRef, Pipe, PipeTransform, computed, effect, inject, input, output } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Problem, toProblem } from '../core/errors';

export type Tone = 'good' | 'warn' | 'bad' | 'info' | 'maint' | 'off';

// ---------------------------------------------------------------- formatting

/** Parses API timestamps; tolerates values without a zone designator (treated as UTC). */
export function parseDate(iso: string | null | undefined): Date | null {
  if (!iso) return null;
  const s = /T\d{2}:\d{2}(:\d{2}(\.\d+)?)?$/.test(iso) ? iso + 'Z' : iso;
  const d = new Date(s);
  return isNaN(d.getTime()) ? null : d;
}

export function timeAgo(iso: string | null | undefined): string {
  const d = parseDate(iso);
  if (!d) return '—';
  const s = Math.round((Date.now() - d.getTime()) / 1000);
  const abs = Math.abs(s), suffix = s >= 0 ? 'ago' : 'from now';
  if (abs < 45) return s >= 0 ? 'just now' : 'in a moment';
  if (abs < 3600) return `${Math.round(abs / 60)} min ${suffix}`;
  if (abs < 86400) return `${Math.round(abs / 3600)} h ${suffix}`;
  if (abs < 86400 * 30) return `${Math.round(abs / 86400)} d ${suffix}`;
  return d.toLocaleDateString();
}

export function fmtDate(iso: string | null | undefined): string {
  const d = parseDate(iso);
  return d ? d.toLocaleString(undefined, { year: 'numeric', month: 'short', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' }) : '—';
}

export function fmtNum(v: number | null | undefined, digits = 0): string {
  if (v === null || v === undefined || isNaN(v)) return '—';
  return v.toLocaleString(undefined, { minimumFractionDigits: digits, maximumFractionDigits: digits });
}

export function fmtMoney(v: number | null | undefined, currency = 'INR'): string {
  if (v === null || v === undefined || isNaN(v)) return '—';
  const digits = Math.abs(v) < 1 && v !== 0 ? 4 : 2;
  try { return v.toLocaleString(undefined, { style: 'currency', currency: currency || 'INR', minimumFractionDigits: 2, maximumFractionDigits: digits }); }
  catch { return `${v.toFixed(digits)} ${currency}`; }
}

/** "RetryPending" -> "Retry pending", "mark_sent" -> "Mark sent". */
export function humanize(v: string | null | undefined): string {
  if (!v) return '—';
  const s = v.replace(/[_-]+/g, ' ').replace(/([a-z])([A-Z])/g, '$1 $2').replace(/([A-Z])([A-Z][a-z])/g, '$1 $2');
  return s.charAt(0).toUpperCase() + s.slice(1).toLowerCase().replace(/\bai\b/g, 'AI').replace(/\bsms\b/g, 'SMS').replace(/\bdlt\b/g, 'DLT');
}

@Pipe({ name: 'ago', pure: false })
export class AgoPipe implements PipeTransform { transform(v: string | null | undefined) { return timeAgo(v); } }

@Pipe({ name: 'dt' })
export class DtPipe implements PipeTransform { transform(v: string | null | undefined) { return fmtDate(v); } }

@Pipe({ name: 'num' })
export class NumPipe implements PipeTransform { transform(v: number | null | undefined, digits = 0) { return fmtNum(v, digits); } }

@Pipe({ name: 'money' })
export class MoneyPipe implements PipeTransform { transform(v: number | null | undefined, currency = 'INR') { return fmtMoney(v, currency); } }

@Pipe({ name: 'human' })
export class HumanizePipe implements PipeTransform { transform(v: string | null | undefined) { return humanize(v); } }

@Pipe({ name: 'short' })
export class ShortIdPipe implements PipeTransform { transform(v: string | null | undefined, n = 8) { return v ? v.substring(0, n) : '—'; } }

// ---------------------------------------------------------------- tone mapping

const TONES: Record<string, Tone> = {
  // delivery
  Delivered: 'good', Read: 'good', Sent: 'good', ProviderAccepted: 'good', Submitted: 'info', Processing: 'info', Queued: 'warn', Scheduled: 'maint',
  RetryPending: 'warn', Failed: 'bad', Expired: 'bad', Unknown: 'bad', Suppressed: 'off', Cancelled: 'off', Draft: 'off',
  // requests
  Accepted: 'info', Completed: 'good', PartiallyFailed: 'warn', PendingApproval: 'warn', Rejected: 'bad',
  // templates
  InReview: 'warn', Approved: 'info', Active: 'good', Retired: 'off',
  // campaigns
  Running: 'info', Paused: 'warn',
  // approvals
  Pending: 'warn',
  // providers / health
  Healthy: 'good', Degraded: 'warn', Unhealthy: 'bad', CircuitOpen: 'bad',
  // attempts
  PermanentFailure: 'bad', TransientFailure: 'warn', Ambiguous: 'bad', Throttled: 'warn',
  // entities
  Suspended: 'bad',
  // inbound routing
  Routed: 'good', NoSubscriber: 'warn',
  // workers
  running: 'good', stopped: 'off', unresponsive: 'bad',
  // severity
  info: 'info', warning: 'warn', critical: 'bad', error: 'bad',
  // consent
  Granted: 'good', Withdrawn: 'off',
};
const ICONS: Partial<Record<Tone, string>> = { good: 'check_circle', warn: 'schedule', bad: 'error', info: 'info', maint: 'event', off: 'remove_circle_outline' };

export function toneOf(v: string | null | undefined): Tone {
  if (!v) return 'off';
  return TONES[v] ?? (/(fail|error|unhealthy|critical)/i.test(v) ? 'bad' : /(succe|ok|healthy|deliver)/i.test(v) ? 'good' : /(warn|degrad|pending)/i.test(v) ? 'warn' : 'off');
}

/** Generic state chip: icon + label + tone (never colour alone). */
@Component({
  selector: 'app-chip',
  imports: [MatIconModule, MatTooltipModule, HumanizePipe],
  template: `<span class="chip" [attr.data-tone]="t()" [matTooltip]="tip() ?? ''"><mat-icon>{{ icon() ?? ic() }}</mat-icon>{{ label() ?? (value() | human) }}</span>`,
})
export class StateChip {
  value = input<string | null | undefined>();
  label = input<string | null>();
  tone = input<Tone | null>();
  icon = input<string | null>();
  tip = input<string | null>();
  t = computed(() => this.tone() ?? toneOf(this.value()));
  ic = computed(() => ICONS[this.t()] ?? 'circle');
}

export const CHANNEL_META: Record<string, { label: string; icon: string }> = {
  email: { label: 'Email', icon: 'mail' }, sms: { label: 'SMS', icon: 'sms' }, whatsapp: { label: 'WhatsApp', icon: 'chat' },
  inapp: { label: 'In-app', icon: 'notifications' }, push: { label: 'Push', icon: 'phone_iphone' }, webpush: { label: 'Web push', icon: 'web' },
  webhook: { label: 'Webhook', icon: 'webhook' },
};
export function channelLabel(c: string | null | undefined) { return c ? CHANNEL_META[c]?.label ?? c : '—'; }

@Pipe({ name: 'channel' })
export class ChannelPipe implements PipeTransform { transform(v: string | null | undefined) { return channelLabel(v); } }

@Component({
  selector: 'app-channel',
  imports: [MatIconModule],
  template: `@if (value()) {<span class="chan"><mat-icon>{{ meta().icon }}</mat-icon>{{ meta().label }}</span>} @else {<span class="muted">—</span>}`,
})
export class ChannelBadge {
  value = input<string | null | undefined>();
  meta = computed(() => CHANNEL_META[this.value() ?? ''] ?? { label: this.value() ?? '', icon: 'forum' });
}

// ---------------------------------------------------------------- banners & states

@Component({
  selector: 'app-error',
  imports: [MatIconModule, MatButtonModule],
  template: `
    @if (p(); as p) {
      <div class="callout bad row" role="alert" style="align-items:flex-start">
        <mat-icon style="color:var(--bad)">error</mat-icon>
        <div style="flex:1;min-width:0">
          <div><b>{{ title() }}</b> {{ p.message }}</div>
          @if (details().length) { <ul>@for (d of details(); track $index) {<li>{{ d }}</li>}</ul> }
          <div class="small muted">
            @if (p.status > 0) { HTTP {{ p.status }} }
            @if (p.code) { · {{ p.code }} }
            @if (p.correlationId) { · correlation id <code>{{ p.correlationId }}</code> }
          </div>
        </div>
        @if (dismissable()) { <button mat-icon-button (click)="dismiss.emit()" aria-label="Dismiss"><mat-icon>close</mat-icon></button> }
      </div>
    }`,
})
export class ErrorBanner {
  error = input<unknown>();
  title = input('Error:');
  dismissable = input(false);
  dismiss = output<void>();
  p = computed<Problem | null>(() => { const e = this.error(); return e ? toProblem(e) : null; });
  private host = inject<ElementRef<HTMLElement>>(ElementRef);
  /** Errors often follow a click far below the banner (long forms): bring the banner into view. */
  private reveal = effect(() => { if (this.p()) setTimeout(() => this.host.nativeElement.scrollIntoView?.({ block: 'nearest', behavior: 'smooth' })); });
  details = computed(() => { const p = this.p(); return p?.errors ? Object.entries(p.errors).flatMap(([k, v]) => v.map(m => (k && k !== 'rule' && k !== 'segment' ? `${k}: ${m}` : m))) : []; });
}

@Component({
  selector: 'app-empty',
  imports: [MatIconModule],
  template: `<div class="empty-state"><mat-icon>{{ icon() }}</mat-icon><div class="t">{{ text() }}</div>@if (hint()) {<div class="small muted">{{ hint() }}</div>}<ng-content /></div>`,
  styles: [`.empty-state { padding: 28px 16px; text-align: center; color: var(--muted); }
    .empty-state mat-icon { font-size: 36px; width: 36px; height: 36px; opacity: .6; }
    .empty-state .t { font-weight: 600; margin: 4px 0 2px; color: var(--text); }`],
})
export class EmptyState {
  icon = input('inbox');
  text = input('Nothing here yet');
  hint = input<string | null>(null);
}

@Component({
  selector: 'app-loading',
  imports: [MatProgressBarModule],
  template: `<div style="height:4px;margin-bottom:8px">@if (on()) { <mat-progress-bar mode="indeterminate" /> }</div>`,
})
export class LoadingBar { on = input(false); }

/** Horizontal usage meter (quota bars). Tone escalates at the alert threshold and at 100 %. */
@Component({
  selector: 'app-meter',
  template: `<div class="meter" role="meter" [attr.aria-valuenow]="pct()" aria-valuemin="0" aria-valuemax="100" [attr.data-tone]="tone()"><span [style.width.%]="min100()"></span></div>`,
})
export class Meter {
  value = input(0);
  max = input(100);
  warnAt = input(80);
  pct = computed(() => this.max() > 0 ? Math.round((100 * this.value()) / this.max()) : 0);
  min100 = computed(() => Math.min(100, this.pct()));
  tone = computed(() => this.pct() >= 100 ? 'bad' : this.pct() >= this.warnAt() ? 'warn' : null);
}

export const UI = [AgoPipe, DtPipe, NumPipe, MoneyPipe, HumanizePipe, ShortIdPipe, ChannelPipe, StateChip, ChannelBadge, ErrorBanner, EmptyState, LoadingBar, Meter] as const;
