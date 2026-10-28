import { Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { Observable } from 'rxjs';
import { fmtNum, parseDate } from './ui';

// ---------------------------------------------------------------- confirm dialog (optional comment)

export interface ConfirmData {
  title: string; message: string; confirmText?: string; danger?: boolean;
  /** Shows a text field; the dialog then closes with {comment}. */
  comment?: { label: string; required?: boolean; placeholder?: string };
  acknowledge?: string;
  details?: string[];
}
export interface ConfirmResult { comment: string; }

@Component({
  selector: 'app-confirm-dialog',
  imports: [MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatCheckboxModule, FormsModule],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      <p class="pre">{{ data.message }}</p>
      @if (data.details?.length) { <ul class="small">@for (d of data.details; track $index) { <li>{{ d }}</li> }</ul> }
      @if (data.comment; as c) {
        <mat-form-field class="full">
          <mat-label>{{ c.label }}</mat-label>
          <textarea matInput rows="3" [(ngModel)]="comment" [placeholder]="c.placeholder ?? ''" cdkFocusInitial></textarea>
        </mat-form-field>
      }
      @if (data.acknowledge) { <mat-checkbox [(ngModel)]="ack">{{ data.acknowledge }}</mat-checkbox> }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button [class.danger]="data.danger" [disabled]="!ok()" [mat-dialog-close]="{ comment: comment.trim() }">{{ data.confirmText ?? 'Confirm' }}</button>
    </mat-dialog-actions>`,
})
export class ConfirmDialog {
  data = inject<ConfirmData>(MAT_DIALOG_DATA);
  comment = ''; ack = false;
  ok() { return (!this.data.comment?.required || !!this.comment.trim()) && (!this.data.acknowledge || this.ack); }
}

export function confirmDialog(dialog: MatDialog, data: ConfirmData): Observable<ConfirmResult | undefined> {
  return dialog.open(ConfirmDialog, { data, width: '520px', maxWidth: '94vw' }).afterClosed();
}

/** Single text input dialog (replacement for window.prompt). Emits the trimmed text, or undefined when cancelled. */
export function promptDialog(dialog: MatDialog, title: string, label: string, message = '', placeholder = ''): Observable<string | undefined> {
  return new Observable<string | undefined>(sub => {
    confirmDialog(dialog, { title, message, comment: { label, required: true, placeholder }, confirmText: 'OK' }).subscribe(r => { sub.next(r?.comment || undefined); sub.complete(); });
  });
}

// ---------------------------------------------------------------- secret dialog (shown once)

@Component({
  selector: 'app-secret-dialog',
  imports: [MatDialogModule, MatButtonModule, MatIconModule],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      <div class="callout warn">{{ data.message }}</div>
      @for (f of data.fields; track f.label) {
        <div class="small muted" style="margin-top:8px">{{ f.label }}</div>
        <div class="row" style="align-items:stretch">
          <code class="secret">{{ f.value }}</code>
          @if (f.secret) { <button mat-stroked-button (click)="copy(f.value, f.label)"><mat-icon>content_copy</mat-icon>{{ copied() === f.label ? 'Copied' : 'Copy' }}</button> }
        </div>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end"><button mat-flat-button mat-dialog-close>I have stored it</button></mat-dialog-actions>`,
  styles: [`.secret { flex: 1; padding: 8px; background: var(--surface-2); border: 1px solid var(--border); border-radius: 4px; word-break: break-all; }`],
})
export class SecretDialog {
  data = inject<{ title: string; message: string; fields: { label: string; value: string; secret?: boolean }[] }>(MAT_DIALOG_DATA);
  copied = signal<string | null>(null);
  copy(v: string, label: string) { navigator.clipboard?.writeText(v).then(() => this.copied.set(label)); }
}

export function showSecret(dialog: MatDialog, title: string, message: string, fields: { label: string; value: string; secret?: boolean }[]) {
  return dialog.open(SecretDialog, { data: { title, message, fields }, width: '560px', maxWidth: '94vw', disableClose: true }).afterClosed();
}

// ---------------------------------------------------------------- JSON viewer dialog

@Component({
  selector: 'app-json-dialog',
  imports: [MatDialogModule, MatButtonModule],
  template: `<h2 mat-dialog-title>{{ data.title }}</h2><mat-dialog-content><pre class="json">{{ text }}</pre></mat-dialog-content>
    <mat-dialog-actions align="end"><button mat-button mat-dialog-close>Close</button></mat-dialog-actions>`,
})
export class JsonDialog {
  data = inject<{ title: string; value: unknown }>(MAT_DIALOG_DATA);
  text = typeof this.data.value === 'string' ? this.data.value : JSON.stringify(this.data.value, null, 2);
}

// ---------------------------------------------------------------- charts
// Thin marks, 4px rounded data ends on the baseline, recessive axes, hover tooltip per mark, visible value labels
// (light-mode contrast of some series colours is below 3:1, so values are always printed as text too).

export interface BarItem { key: string; label: string; value: number; note?: string; }

/** Single-series horizontal bars with direct value labels (no legend: the panel title names the series). */
@Component({
  selector: 'app-hbar-chart',
  template: `
    @if (items().length) {
      <div class="hbars" role="img" [attr.aria-label]="ariaLabel()">
        @for (b of items(); track b.key) {
          <div class="row" [attr.title]="b.label + ': ' + fmt(b.value) + (b.note ? ' · ' + b.note : '')">
            <div class="lbl ellipsis">{{ b.label }}</div>
            <div class="track"><div class="bar" [style.width.%]="max() ? (b.value / max()) * 100 : 0"></div></div>
            <div class="val">{{ fmt(b.value) }}@if (b.note) {<span class="note">{{ b.note }}</span>}</div>
          </div>
        }
      </div>
    } @else { <div class="muted small" style="padding:24px;text-align:center">{{ emptyText() }}</div> }`,
  styles: [`
    .hbars { display: flex; flex-direction: column; gap: 6px; }
    .row { display: grid; grid-template-columns: minmax(70px, 110px) 1fr auto; gap: 8px; align-items: center; }
    .row:hover .bar { filter: brightness(1.12); }
    .lbl { font-size: 12px; color: var(--muted); }
    .track { height: 14px; display: flex; align-items: center; border-left: 1px solid var(--grid); }
    .bar { height: 12px; background: var(--series-1); border-radius: 0 4px 4px 0; min-width: 2px; transition: width .3s; }
    .val { font-size: 12px; font-variant-numeric: tabular-nums; text-align: right; white-space: nowrap; min-width: 40px; }
    .note { color: var(--muted); margin-left: 6px; }`],
})
export class HBarChart {
  items = input<BarItem[]>([]);
  ariaLabel = input('Bar chart');
  emptyText = input('No data in this period');
  max = computed(() => Math.max(0, ...this.items().map(i => i.value)));
  fmt(v: number) { return fmtNum(v); }
}

export interface ColumnPoint { at: string; total: number; failed: number; }

/** Hourly trend: stacked columns (other outcomes + failed) with a legend and a hover tooltip. */
@Component({
  selector: 'app-trend-chart',
  template: `
    @if (points().length) {
      <div class="legend small">
        <span><i class="sw s1"></i>Not failed</span><span><i class="sw s2"></i>Failed</span>
        <span class="spacer"></span><span class="muted">peak {{ max() }} / hour</span>
      </div>
      <div class="plot" role="img" [attr.aria-label]="'Deliveries per hour, last ' + points().length + ' hours'">
        <div class="grid"><span></span><span></span><span></span></div>
        <div class="cols">
          @for (p of points(); track p.at; let i = $index) {
            <div class="col" (mouseenter)="hover.set(i)" (mouseleave)="hover.set(null)" (focus)="hover.set(i)" (blur)="hover.set(null)" tabindex="0">
              <div class="stack" [style.height.%]="max() ? (p.total / max()) * 100 : 0">
                @if (p.failed) { <div class="seg s2" [style.flex]="p.failed"></div> }
                @if (p.total - p.failed) { <div class="seg s1" [style.flex]="p.total - p.failed"></div> }
              </div>
              @if (hover() === i) {
                <div class="tip" [class.left]="i > points().length / 2">
                  <b>{{ label(p.at) }}</b><br>{{ p.total }} deliveries<br>{{ p.failed }} failed
                </div>
              }
            </div>
          }
        </div>
      </div>
      <div class="axis">
        @for (p of points(); track p.at; let i = $index) { <div class="tick">{{ i % step() === 0 ? hourLabel(p.at) : '' }}</div> }
      </div>
    } @else { <div class="muted small" style="padding:24px;text-align:center">No deliveries in the last 24 hours</div> }`,
  styles: [`
    .legend { display: flex; gap: 12px; align-items: center; margin-bottom: 6px; color: var(--text); }
    .sw { display: inline-block; width: 10px; height: 10px; border-radius: 2px; margin-right: 4px; vertical-align: -1px; }
    .s1 { background: var(--series-1); } .s2 { background: var(--series-2); }
    .plot { position: relative; height: 160px; border-bottom: 1px solid var(--grid); }
    .grid { position: absolute; inset: 0; display: flex; flex-direction: column; justify-content: space-between; pointer-events: none; }
    .grid span { border-top: 1px dashed var(--grid); }
    .cols { position: absolute; inset: 0; display: flex; align-items: flex-end; gap: 2px; }
    .col { flex: 1; height: 100%; display: flex; flex-direction: column; justify-content: flex-end; position: relative; min-width: 3px; outline: none; }
    .col:hover .stack, .col:focus .stack { filter: brightness(1.12); }
    .stack { display: flex; flex-direction: column; gap: 2px; min-height: 0; transition: height .3s; }
    .seg { min-height: 2px; } .stack .seg:first-child { border-radius: 4px 4px 0 0; }
    .tip { position: absolute; bottom: calc(100% - 40px); left: 50%; z-index: 3; background: var(--surface); color: var(--text); border: 1px solid var(--border);
      border-radius: 6px; padding: 6px 8px; font-size: 12px; white-space: nowrap; box-shadow: 0 2px 8px rgba(0,0,0,.18); pointer-events: none; }
    .tip.left { left: auto; right: 50%; }
    .axis { display: flex; gap: 2px; } .tick { flex: 1; font-size: 10px; color: var(--muted); text-align: center; white-space: nowrap; overflow: visible; min-width: 3px; }`],
})
export class TrendChart {
  points = input<ColumnPoint[]>([]);
  hover = signal<number | null>(null);
  max = computed(() => Math.max(0, ...this.points().map(p => p.total)));
  step = computed(() => Math.max(1, Math.ceil(this.points().length / 8)));
  hourLabel(at: string) { const d = parseDate(at); return d ? d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', hour12: false }) : ''; }
  label(at: string) { const d = parseDate(at); return d ? d.toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : at; }
}

/** Fills missing hours with zeros so gaps read as "no traffic" rather than being skipped. */
export function fillHours(rows: { hour: string; total: number; failed: number }[], hours = 24, now = new Date()): ColumnPoint[] {
  const byHour = new Map<number, { total: number; failed: number }>();
  for (const r of rows) { const d = parseDate(r.hour); if (d) byHour.set(Math.floor(d.getTime() / 3600000), r); }
  const end = Math.floor(now.getTime() / 3600000);
  const out: ColumnPoint[] = [];
  for (let h = end - hours + 1; h <= end; h++) {
    const r = byHour.get(h);
    out.push({ at: new Date(h * 3600000).toISOString(), total: r?.total ?? 0, failed: r?.failed ?? 0 });
  }
  return out;
}
