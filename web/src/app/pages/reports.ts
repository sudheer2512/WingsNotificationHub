import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Router } from '@angular/router';
import { ApiService } from '../core/api.service';
import { problemText } from '../core/errors';
import { ReportInfo, ReportResult } from '../core/models';
import { FilterModel, ScopeFilters, emptyFilters, filterQuery } from '../shared/filters';
import { UI, fmtDate, fmtNum } from '../shared/ui';

@Component({
  selector: 'app-reports',
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatIconModule, MatSelectModule, ScopeFilters, ...UI],
  template: `
    <div class="page">
      <div class="page-head"><h1>Reports</h1><span class="spacer"></span>
        <button mat-stroked-button (click)="export('csv')" [disabled]="!code || exporting()"><mat-icon>download</mat-icon>CSV</button>
        <button mat-stroked-button (click)="export('xlsx')" [disabled]="!code || exporting()"><mat-icon>download</mat-icon>Excel</button>
      </div>
      <div class="split rep">
        <section class="panel">
          <nav class="rep-list" aria-label="Reports">
            @for (r of reports(); track r.code) {
              <button [class.on]="r.code === code" (click)="pick(r.code)">{{ r.name }}</button>
            }
          </nav>
        </section>
        <section style="min-width:0">
          <app-scope-filters [(value)]="filters" (changed)="run()" />
          <app-loading [on]="loading()" />
          <app-error [error]="error()" />
          @if (result(); as r) {
            <section class="panel">
              <div class="panel-head"><h2>{{ r.name }}</h2><span class="spacer"></span><span class="small muted">{{ r.rows.length | num }} rows</span></div>
              @if (r.rows.length) {
                <div class="table-wrap" style="max-height:calc(100vh - 300px)">
                  <table class="data">
                    <thead><tr>@for (c of columns(); track c) { <th [class.num]="numeric().has(c)"><button class="sort" (click)="sortBy(c)">{{ c }}@if (sort === c) { {{ desc ? '↓' : '↑' }} }</button></th> }</tr></thead>
                    <tbody>@for (row of sorted(); track $index) { <tr>@for (c of columns(); track c) { <td [class.num]="numeric().has(c)">{{ cell(row[c]) }}</td> }</tr> }</tbody>
                  </table>
                </div>
              } @else { <app-empty icon="summarize" text="No data for these filters" /> }
            </section>
          } @else if (!code) { <div class="panel"><app-empty icon="summarize" text="Choose a report" /></div> }
        </section>
      </div>
    </div>`,
  styles: [`
    .rep { grid-template-columns: minmax(220px, 280px) 1fr; }
    @media (max-width: 900px) { .rep { grid-template-columns: 1fr; } }
    .rep-list { display: flex; flex-direction: column; }
    .rep-list button { all: unset; cursor: pointer; padding: 7px 10px; border-radius: 6px; }
    .rep-list button:hover { background: var(--hover); } .rep-list button.on { background: var(--selected); font-weight: 600; }
    .rep-list button:focus-visible { outline: 2px solid var(--accent); }
    button.sort { all: unset; cursor: pointer; }`],
})
export class Reports implements OnInit {
  private api = inject(ApiService);
  private snack = inject(MatSnackBar);
  private router = inject(Router);
  report = input<string>();
  reports = signal<ReportInfo[]>([]);
  result = signal<ReportResult | null>(null);
  loading = signal(false);
  exporting = signal(false);
  error = signal<unknown>(null);
  filters: FilterModel = emptyFilters();
  code = '';
  sort = ''; desc = false;
  private sortTick = signal(0);

  columns = computed(() => { const rows = this.result()?.rows ?? []; const cols = new Set<string>(); for (const r of rows.slice(0, 50)) Object.keys(r).forEach(k => cols.add(k)); return [...cols]; });
  numeric = computed(() => new Set(this.columns().filter(c => (this.result()?.rows ?? []).every(r => r[c] === null || r[c] === undefined || typeof r[c] === 'number'))));
  sorted = computed(() => {
    this.sortTick();
    const rows = [...(this.result()?.rows ?? [])];
    if (!this.sort) return rows;
    const k = this.sort, d = this.desc ? -1 : 1;
    return rows.sort((a, b) => { const x = a[k], y = b[k]; return (x === y ? 0 : x === null || x === undefined ? -1 : y === null || y === undefined ? 1 : x < y ? -1 : 1) * d; });
  });

  ngOnInit() {
    this.api.reports().subscribe({ next: r => { this.reports.set(r); const c = this.report() ?? r[0]?.code; if (c) this.pick(c); }, error: e => this.error.set(e) });
  }

  pick(code: string) {
    this.code = code; this.sort = '';
    this.router.navigate([], { queryParams: { report: code }, replaceUrl: true });
    this.run();
  }

  run() {
    if (!this.code) return;
    this.loading.set(true); this.error.set(null);
    this.api.report(this.code, filterQuery(this.filters)).subscribe({
      next: r => { this.result.set(r); this.loading.set(false); }, error: e => { this.error.set(e); this.loading.set(false); this.result.set(null); },
    });
  }

  sortBy(c: string) { if (this.sort === c) this.desc = !this.desc; else { this.sort = c; this.desc = this.numeric().has(c); } this.sortTick.update(v => v + 1); }

  cell(v: unknown): string {
    if (v === null || v === undefined) return '—';
    if (typeof v === 'number') return Number.isInteger(v) ? fmtNum(v) : fmtNum(v, Math.abs(v) < 1 ? 4 : 2);
    if (typeof v === 'string' && /^\d{4}-\d{2}-\d{2}T/.test(v)) return fmtDate(v);
    if (typeof v === 'object') return JSON.stringify(v);
    return String(v);
  }

  export(format: 'csv' | 'xlsx') {
    this.exporting.set(true);
    this.api.download(`/reports/${this.code}`, { ...filterQuery(this.filters), format }, `${this.code}.${format}`).subscribe({
      next: n => { this.exporting.set(false); this.snack.open(`Downloaded ${n}`, 'OK', { duration: 3000 }); },
      error: e => { this.exporting.set(false); this.snack.open(problemText(e), 'OK', { duration: 8000, panelClass: 'snack-error' }); },
    });
  }
}
