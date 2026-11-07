import { Component, DestroyRef, ElementRef, ViewChild, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Subject, debounceTime, switchMap, catchError, of } from 'rxjs';
import { ApiService } from '../core/api.service';
import { BodyFormat, PreviewResponse, TemplateDetail, TemplateVariable, TemplateVersion, TemplateVersionRequest, WhatsAppButton } from '../core/models';
import { UI } from '../shared/ui';
import { promptDialog } from '../shared/widgets';
import { MatDialog } from '@angular/material/dialog';
import { RichText } from './rich-text';
import { BLOCKS, BUILT_IN_VARIABLES, FILTERS, extractVariables, isBuiltIn, sampleData, smsSegments } from './template-utils';

type Field = 'subject' | 'body' | 'html' | 'text' | 'title';

export interface EditorModel {
  subject: string; body: string; bodyFormat: BodyFormat; textBody: string; title: string; changeNote: string;
  dltTemplateId: string; providerTemplateName: string; providerTemplateLanguage: string; providerCategory: string;
  waHeaderFormat: string; waHeaderText: string; waHeaderMediaVariable: string; waFooter: string; waButtons: WhatsAppButton[];
  pushIcon: string; pushImageUrl: string; pushSound: string; pushDeepLink: string;
  variables: TemplateVariable[];
}

export function toEditor(v: TemplateVersion | undefined): EditorModel {
  const c = v?.components;
  return {
    subject: v?.subject ?? '', body: v?.body ?? '', bodyFormat: v?.bodyFormat ?? 'Text', textBody: v?.textBody ?? '', title: c?.title ?? '', changeNote: '',
    dltTemplateId: v?.dltTemplateId ?? '', providerTemplateName: v?.providerTemplateName ?? '', providerTemplateLanguage: v?.providerTemplateLanguage ?? '',
    providerCategory: v?.providerCategory ?? '', waHeaderFormat: c?.whatsApp?.headerFormat ?? '', waHeaderText: c?.whatsApp?.headerText ?? '',
    waHeaderMediaVariable: c?.whatsApp?.headerMediaVariable ?? '', waFooter: c?.whatsApp?.footerText ?? '', waButtons: (c?.whatsApp?.buttons ?? []).map(b => ({ ...b })),
    pushIcon: c?.push?.icon ?? '', pushImageUrl: c?.push?.imageUrl ?? '', pushSound: c?.push?.sound ?? '', pushDeepLink: c?.push?.deepLink ?? '',
    variables: (v?.variables ?? []).map(x => ({ ...x })),
  };
}

export function toRequest(m: EditorModel, channel: string): TemplateVersionRequest {
  const n = (s: string) => (s.trim() ? s.trim() : null);
  const detected = extractVariables(m.subject, m.body, m.title, m.textBody, m.waHeaderText);
  const declared = new Set(m.variables.map(v => v.name.toLowerCase()));
  const variables = [...m.variables.filter(v => v.name.trim()), ...detected.filter(d => !declared.has(d.toLowerCase()) && !isBuiltIn(d)).map(name => ({ name, required: true, sampleValue: null, description: null }))];
  const wa = channel === 'whatsapp' && (m.waHeaderFormat || m.waFooter || m.waButtons.length)
    ? { headerFormat: n(m.waHeaderFormat), headerText: n(m.waHeaderText), headerMediaVariable: n(m.waHeaderMediaVariable), footerText: n(m.waFooter), buttons: m.waButtons } : null;
  const push = (channel === 'push' || channel === 'webpush') && (m.pushIcon || m.pushImageUrl || m.pushSound || m.pushDeepLink)
    ? { icon: n(m.pushIcon), imageUrl: n(m.pushImageUrl), sound: n(m.pushSound), deepLink: n(m.pushDeepLink) } : null;
  const components = m.title || wa || push ? { title: n(m.title), whatsApp: wa, push } : null;
  return {
    subject: n(m.subject), body: m.body, bodyFormat: m.bodyFormat, textBody: m.bodyFormat === 'Html' ? n(m.textBody) : null, components,
    providerTemplateName: n(m.providerTemplateName), providerTemplateLanguage: n(m.providerTemplateLanguage), providerCategory: n(m.providerCategory),
    dltTemplateId: n(m.dltTemplateId), variables, changeNote: n(m.changeNote),
  };
}

/** Editor for the working draft of a template: rich text / HTML / plain text, variables, channel options and live preview. */
@Component({
  selector: 'app-template-editor',
  imports: [FormsModule, MatButtonModule, MatButtonToggleModule, MatCheckboxModule, MatExpansionModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule,
    MatTooltipModule, RichText, ...UI],
  template: `
    @if (m(); as m) {
      <div class="ed-grid">
        <div class="ed-main">
          @if (!canEdit()) { <div class="callout">Read-only: you can view templates but not edit them.</div> }
          @else if (baseVersion(); as bv) {
            @if (bv.status !== 'Draft' && bv.status !== 'Rejected') { <div class="callout">Editing starts from v{{ bv.versionNumber }} ({{ bv.status | human }}). Saving creates a new draft version; the active version keeps sending until a new one is approved.</div> }
            @if (bv.status === 'Rejected') { <div class="callout bad">v{{ bv.versionNumber }} was rejected: {{ bv.rejectionReason || 'no reason given' }}<br>Saving turns it back into a draft.</div> }
          }
          @if (channel() === 'email') {
            <mat-form-field class="full"><mat-label>Subject</mat-label>
              <input matInput #subjectEl [(ngModel)]="m.subject" (ngModelChange)="touch()" (focus)="focus = 'subject'" [readonly]="!canEdit()"></mat-form-field>
          }
          @if (channel() === 'inapp' || channel() === 'push' || channel() === 'webpush') {
            <mat-form-field class="full"><mat-label>Title</mat-label>
              <input matInput #titleEl [(ngModel)]="m.title" (ngModelChange)="touch()" (focus)="focus = 'title'" [readonly]="!canEdit()"></mat-form-field>
          }
          <div class="row wrap-row" style="margin-bottom:6px">
            @if (channel() === 'email') {
              <mat-button-toggle-group [value]="m.bodyFormat" (change)="setFormat($event.value)" [disabled]="!canEdit()" hideSingleSelectionIndicator aria-label="Body format">
                <mat-button-toggle value="Html">HTML</mat-button-toggle><mat-button-toggle value="Text">Plain text</mat-button-toggle>
              </mat-button-toggle-group>
              @if (m.bodyFormat === 'Html') {
                <mat-button-toggle-group [value]="mode()" (change)="mode.set($event.value)" hideSingleSelectionIndicator aria-label="Editor mode">
                  <mat-button-toggle value="rich"><mat-icon>format_paint</mat-icon> Rich text</mat-button-toggle>
                  <mat-button-toggle value="html"><mat-icon>code</mat-icon> HTML source</mat-button-toggle>
                  <mat-button-toggle value="text"><mat-icon>notes</mat-icon> Text part</mat-button-toggle>
                </mat-button-toggle-group>
              }
            }
            <span class="spacer"></span>
            @if (channel() === 'sms') {
              <span class="small" [class.error]="sms().segments > 3" matTooltip="Estimate for the template text; the rendered preview shows the exact count">
                <mat-icon class="inline-ic">sms</mat-icon> {{ sms().encoding }} · {{ sms().characters }} chars · {{ sms().segments }} segment{{ sms().segments === 1 ? '' : 's' }} ({{ sms().perSegment }}/segment)
              </span>
            }
          </div>
          @if (m.bodyFormat === 'Html' && channel() === 'email') {
            @switch (mode()) {
              @case ('rich') { <app-rich-text #rte [value]="m.body" [readonly]="!canEdit()" (changed)="m.body = $event; touch()" (focusin)="focus = 'body'" /> }
              @case ('html') {
                <textarea #htmlEl class="code-area full src" rows="16" [(ngModel)]="m.body" (ngModelChange)="touch()" (focus)="focus = 'html'" [readonly]="!canEdit()" spellcheck="false" aria-label="HTML source"></textarea>
              }
              @case ('text') {
                <textarea #textEl class="code-area full src" rows="12" [(ngModel)]="m.textBody" (ngModelChange)="touch()" (focus)="focus = 'text'" [readonly]="!canEdit()" aria-label="Plain-text part"
                  placeholder="Optional plain-text alternative. Generated from the HTML when empty."></textarea>
              }
            }
          } @else {
            <textarea #htmlEl class="code-area full src" rows="10" [(ngModel)]="m.body" (ngModelChange)="touch()" (focus)="focus = 'html'" [readonly]="!canEdit()" aria-label="Message body"></textarea>
          }

          <div class="vars">
            <span class="small muted">Insert variable:</span>
            @for (v of allVars(); track v) { <button type="button" class="var-chip" (mousedown)="$event.preventDefault()" (click)="insert(v)" [disabled]="!canEdit()">{{ v }}</button> }
            <button type="button" class="var-chip" (mousedown)="$event.preventDefault()" (click)="addVariable()" [disabled]="!canEdit()">+ new</button>
            <span class="small muted" style="margin-left:8px">Built-in:</span>
            @for (v of builtIns; track v) { <button type="button" class="var-chip builtin" (mousedown)="$event.preventDefault()" (click)="insert(v)" [disabled]="!canEdit()" title="Supplied by the hub at send time">{{ v }}</button> }
          </div>

          @if (channel() === 'sms') {
            <div class="form-grid" style="margin-top:8px">
              <mat-form-field><mat-label>DLT template id (India)</mat-label><input matInput [(ngModel)]="m.dltTemplateId" (ngModelChange)="touch()" [readonly]="!canEdit()"></mat-form-field>
            </div>
          }
          @if (channel() === 'whatsapp') {
            <h3 style="margin:12px 0 6px">WhatsApp provider template</h3>
            <div class="form-grid">
              <mat-form-field><mat-label>Provider template name</mat-label><input matInput [(ngModel)]="m.providerTemplateName" (ngModelChange)="touch()" [readonly]="!canEdit()" placeholder="invoice_created_v1"></mat-form-field>
              <mat-form-field><mat-label>Language</mat-label><input matInput [(ngModel)]="m.providerTemplateLanguage" (ngModelChange)="touch()" [readonly]="!canEdit()" placeholder="en_US"></mat-form-field>
              <mat-form-field><mat-label>Category</mat-label>
                <mat-select [(ngModel)]="m.providerCategory" (selectionChange)="touch()" [disabled]="!canEdit()">
                  <mat-option value="">—</mat-option><mat-option value="UTILITY">Utility</mat-option><mat-option value="MARKETING">Marketing</mat-option><mat-option value="AUTHENTICATION">Authentication</mat-option>
                </mat-select></mat-form-field>
              <mat-form-field><mat-label>Header</mat-label>
                <mat-select [(ngModel)]="m.waHeaderFormat" (selectionChange)="touch()" [disabled]="!canEdit()">
                  <mat-option value="">None</mat-option><mat-option value="TEXT">Text</mat-option><mat-option value="IMAGE">Image</mat-option><mat-option value="DOCUMENT">Document</mat-option><mat-option value="VIDEO">Video</mat-option>
                </mat-select></mat-form-field>
              @if (m.waHeaderFormat === 'TEXT') { <mat-form-field><mat-label>Header text</mat-label><input matInput [(ngModel)]="m.waHeaderText" (ngModelChange)="touch()" [readonly]="!canEdit()"></mat-form-field> }
              @if (m.waHeaderFormat && m.waHeaderFormat !== 'TEXT') { <mat-form-field><mat-label>Media URL variable</mat-label><input matInput [(ngModel)]="m.waHeaderMediaVariable" (ngModelChange)="touch()" [readonly]="!canEdit()" placeholder="InvoicePdfUrl"></mat-form-field> }
              <mat-form-field class="span-2"><mat-label>Footer</mat-label><input matInput [(ngModel)]="m.waFooter" (ngModelChange)="touch()" [readonly]="!canEdit()"></mat-form-field>
            </div>
            <div class="small muted">Buttons (components)</div>
            @for (b of m.waButtons; track $index; let i = $index) {
              <div class="wrap-row">
                <mat-form-field style="width:150px"><mat-label>Type</mat-label>
                  <mat-select [(ngModel)]="b.type" (selectionChange)="touch()" [disabled]="!canEdit()"><mat-option value="QUICK_REPLY">Quick reply</mat-option><mat-option value="URL">URL</mat-option><mat-option value="PHONE_NUMBER">Phone</mat-option></mat-select></mat-form-field>
                <mat-form-field style="width:160px"><mat-label>Text</mat-label><input matInput [(ngModel)]="b.text" (ngModelChange)="touch()" [readonly]="!canEdit()"></mat-form-field>
                @if (b.type === 'URL') { <mat-form-field style="width:220px"><mat-label>{{ urlHint }}</mat-label><input matInput [(ngModel)]="b.url" (ngModelChange)="touch()" [readonly]="!canEdit()"></mat-form-field> }
                @if (b.type === 'PHONE_NUMBER') { <mat-form-field style="width:180px"><mat-label>Phone</mat-label><input matInput [(ngModel)]="b.phoneNumber" (ngModelChange)="touch()" [readonly]="!canEdit()"></mat-form-field> }
                @if (b.type !== 'PHONE_NUMBER') { <mat-form-field style="width:160px"><mat-label>Variable</mat-label><input matInput [(ngModel)]="b.variable" (ngModelChange)="touch()" [readonly]="!canEdit()"></mat-form-field> }
                @if (canEdit()) { <button mat-icon-button (click)="m.waButtons.splice(i, 1); touch()" aria-label="Remove button"><mat-icon>close</mat-icon></button> }
              </div>
            }
            @if (canEdit() && m.waButtons.length < 10) { <button mat-button (click)="m.waButtons.push({ type: 'QUICK_REPLY', text: '' }); touch()"><mat-icon>add</mat-icon>Button</button> }
          }
          @if (channel() === 'push' || channel() === 'webpush') {
            <h3 style="margin:12px 0 6px">Push options</h3>
            <div class="form-grid">
              <mat-form-field><mat-label>Icon</mat-label><input matInput [(ngModel)]="m.pushIcon" (ngModelChange)="touch()" [readonly]="!canEdit()"></mat-form-field>
              <mat-form-field><mat-label>Image URL</mat-label><input matInput [(ngModel)]="m.pushImageUrl" (ngModelChange)="touch()" [readonly]="!canEdit()"></mat-form-field>
              <mat-form-field><mat-label>Sound</mat-label><input matInput [(ngModel)]="m.pushSound" (ngModelChange)="touch()" [readonly]="!canEdit()"></mat-form-field>
              <mat-form-field><mat-label>Deep link</mat-label><input matInput [(ngModel)]="m.pushDeepLink" (ngModelChange)="touch()" [readonly]="!canEdit()"></mat-form-field>
            </div>
          }

          <mat-accordion multi style="display:block;margin-top:10px">
            <mat-expansion-panel>
              <mat-expansion-panel-header><mat-panel-title>Variables ({{ m.variables.length }})</mat-panel-title>
                <mat-panel-description>@if (undeclared().length) { <span class="error">{{ undeclared().length }} used but not declared</span> }</mat-panel-description></mat-expansion-panel-header>
              <div class="table-wrap">
                <table class="data">
                  <thead><tr><th>Name</th><th>Required</th><th>Sample value</th><th>Description</th><th></th></tr></thead>
                  <tbody>
                    @for (v of m.variables; track $index; let i = $index) {
                      <tr>
                        <td><input class="cell" [(ngModel)]="v.name" (ngModelChange)="touch()" [readonly]="!canEdit()" aria-label="Variable name"></td>
                        <td><mat-checkbox [(ngModel)]="v.required" (change)="touch()" [disabled]="!canEdit()" aria-label="Required" /></td>
                        <td><input class="cell" [ngModel]="v.sampleValue ?? ''" (ngModelChange)="v.sampleValue = $event; resetSample()" [readonly]="!canEdit()" aria-label="Sample value"></td>
                        <td><input class="cell" [ngModel]="v.description ?? ''" (ngModelChange)="v.description = $event; touch()" [readonly]="!canEdit()" aria-label="Description"></td>
                        <td>@if (canEdit()) { <button mat-icon-button (click)="m.variables.splice(i, 1); touch()" aria-label="Remove variable"><mat-icon>delete</mat-icon></button> }</td>
                      </tr>
                    }
                    @for (u of undeclared(); track u) { <tr class="muted"><td><code>{{ u }}</code></td><td colspan="3" class="small">used in the content; added as required on save</td><td></td></tr> }
                  </tbody>
                </table>
              </div>
            </mat-expansion-panel>
            <mat-expansion-panel>
              <mat-expansion-panel-header><mat-panel-title>Syntax help</mat-panel-title></mat-expansion-panel-header>
              <p class="small">Variables: <code>{{ ex.var }}</code>, nested <code>{{ ex.nested }}</code>. Filters chain with <code>|</code> and take one argument after <code>:</code>. Output is always HTML-escaped; there is no code execution.</p>
              <table class="data small"><tbody>
                @for (f of filters; track f.name) { <tr><td><code>{{ f.name }}{{ f.arg ? ':' + f.arg : '' }}</code></td><td><code>{{ f.example }}</code></td><td>{{ f.text }}</td></tr> }
                @for (b of blocks; track b.example) { <tr><td colspan="2"><code>{{ b.example }}</code></td><td>{{ b.text }}</td></tr> }
              </tbody></table>
            </mat-expansion-panel>
          </mat-accordion>

          @if (canEdit()) {
            <mat-form-field class="full" style="margin-top:10px"><mat-label>Change note</mat-label><input matInput [(ngModel)]="m.changeNote" (ngModelChange)="touch()" placeholder="What changed and why"></mat-form-field>
          }
        </div>

        <aside class="ed-side">
          <section class="panel">
            <div class="panel-head"><h2>Live preview</h2><span class="spacer"></span>@if (previewing()) { <span class="small muted">rendering…</span> }</div>
            <app-error [error]="previewError()" />
            @if (preview(); as p) {
              @for (e of p.errors; track $index) { <div class="callout bad small">{{ e }}</div> }
              @if (p.missingVariables.length) { <div class="callout warn small">Missing: @for (v of p.missingVariables; track v) { <code>{{ v }}</code> } </div> }
              @if (p.unknownVariables.length) { <div class="small muted">Not used by the template: @for (v of p.unknownVariables; track v) { <code>{{ v }}</code> } </div> }
              @if (p.subject) { <div class="small muted">{{ channel() === 'email' ? 'Subject' : 'Title' }}</div><div><b>{{ p.subject }}</b></div> }
              @if (channel() === 'email' && m.bodyFormat === 'Html') {
                <iframe class="preview-frame" sandbox="" [attr.srcdoc]="p.body ?? ''" title="Email preview"></iframe>
                @if (p.textBody) { <div class="small muted" style="margin-top:6px">Text part</div><pre class="json">{{ p.textBody }}</pre> }
              } @else {
                <pre class="json" [class.wa]="channel() === 'whatsapp'">{{ p.body }}</pre>
              }
              @if (p.sms; as s) { <div class="small">Rendered SMS: <b>{{ s.encoding }}</b>, {{ s.characters }} characters, <b>{{ s.segments }}</b> segment(s)</div> }
            }
          </section>
          <section class="panel">
            <div class="panel-head"><h2>Sample data</h2><span class="spacer"></span><button mat-button (click)="resetSample(true)">Reset</button></div>
            <textarea class="code-area full src" rows="8" [(ngModel)]="sample" (ngModelChange)="schedulePreview()" aria-label="Sample data JSON"></textarea>
            @if (sampleError()) { <div class="error small">{{ sampleError() }}</div> }
          </section>
        </aside>
      </div>
    }`,
  styles: [`
    .ed-grid { display: grid; grid-template-columns: minmax(0, 1fr) minmax(280px, 420px); gap: 12px; align-items: start; }
    @media (max-width: 1100px) { .ed-grid { grid-template-columns: 1fr; } }
    .src { box-sizing: border-box; padding: 8px; border: 1px solid var(--border); border-radius: 6px; background: var(--surface); color: var(--text); resize: vertical; }
    .src:focus { outline: none; border-color: var(--accent); }
    .vars { margin-top: 6px; display: flex; flex-wrap: wrap; align-items: center; gap: 2px; }
    input.cell { width: 100%; min-width: 90px; border: 1px solid transparent; background: transparent; color: var(--text); padding: 4px; border-radius: 4px; font: inherit; }
    input.cell:hover, input.cell:focus { border-color: var(--border); background: var(--surface); outline: none; }
    .inline-ic { font-size: 16px; width: 16px; height: 16px; vertical-align: -3px; }
    pre.wa { background: light-dark(#e7fbe0, #173322); }
    .var-chip.builtin { border-style: dashed; color: var(--muted); }
    .ed-main { min-width: 0; }`],
})
export class TemplateEditor {
  private api = inject(ApiService);
  private destroyRef = inject(DestroyRef);
  private dialog = inject(MatDialog);
  detail = input.required<TemplateDetail>();
  canEdit = input(false);
  dirtyChange = output<boolean>();
  @ViewChild('rte') rte?: RichText;
  @ViewChild('htmlEl') htmlEl?: ElementRef<HTMLTextAreaElement>;
  @ViewChild('textEl') textEl?: ElementRef<HTMLTextAreaElement>;
  @ViewChild('subjectEl') subjectEl?: ElementRef<HTMLInputElement>;
  @ViewChild('titleEl') titleEl?: ElementRef<HTMLInputElement>;

  filters = FILTERS; blocks = BLOCKS; builtIns = BUILT_IN_VARIABLES;
  ex = { var: '{{CustomerName}}', nested: '{{Customer.Name}}' };
  urlHint = 'URL (may end with {{1}})';
  m = signal<EditorModel | null>(null);
  mode = signal<'rich' | 'html' | 'text'>('rich');
  version = signal(0); // bumps on edits so computed() re-evaluates the mutable model
  dirty = false;
  focus: Field = 'body';
  sample = '{}';
  sampleError = signal<string | null>(null);
  preview = signal<PreviewResponse | null>(null);
  previewError = signal<unknown>(null);
  previewing = signal(false);
  private previewReq = new Subject<void>();

  channel = computed(() => this.detail().template.channel);
  baseVersion = computed(() => this.detail().versions[0]);
  allVars = computed(() => {
    this.version();
    const m = this.m();
    if (!m) return [];
    const names = new Map<string, string>();
    for (const v of m.variables) if (v.name) names.set(v.name.toLowerCase(), v.name);
    for (const d of extractVariables(m.subject, m.body, m.title, m.textBody)) if (!names.has(d.toLowerCase()) && !isBuiltIn(d)) names.set(d.toLowerCase(), d);
    return [...names.values()];
  });
  undeclared = computed(() => {
    this.version();
    const m = this.m();
    if (!m) return [];
    const declared = new Set(m.variables.map(v => v.name.toLowerCase()));
    return extractVariables(m.subject, m.body, m.title, m.textBody, m.waHeaderText).filter(d => !declared.has(d.toLowerCase()) && !isBuiltIn(d));
  });
  sms = computed(() => { this.version(); return smsSegments(this.m()?.body ?? ''); });

  constructor() {
    effect(() => {
      const d = this.detail();
      untracked(() => this.reset(d));
    });
    this.previewReq.pipe(
      debounceTime(500),
      switchMap(() => {
        const m = this.m();
        if (!m) return of(null);
        let variables: Record<string, unknown> = {};
        try { variables = this.sample.trim() ? JSON.parse(this.sample) : {}; this.sampleError.set(null); }
        catch (e) { this.sampleError.set('Sample data is not valid JSON: ' + (e as Error).message); return of(null); }
        this.previewing.set(true);
        const t = this.detail().template;
        return this.api.preview({ channel: t.channel, subject: t.channel === 'email' ? m.subject : (m.title || null), body: m.body, bodyFormat: m.bodyFormat, variables, useSampleData: false,
          companyId: t.companyId, applicationId: t.applicationId }).pipe(catchError(e => { this.previewError.set(e); return of(null); }));
      }),
      takeUntilDestroyed(this.destroyRef),
    ).subscribe(p => { this.previewing.set(false); if (p) { this.preview.set(p); this.previewError.set(null); } });
  }

  reset(d: TemplateDetail = this.detail()) {
    const m = toEditor(d.versions[0]);
    if (!d.versions.length) m.bodyFormat = d.template.channel === 'email' ? 'Html' : 'Text';
    this.m.set(m);
    this.mode.set('rich');
    this.setDirty(false);
    this.resetSample(true);
  }

  /** Called after every edit. */
  touch() { this.version.update(v => v + 1); this.setDirty(true); this.schedulePreview(); }

  private setDirty(v: boolean) { if (this.dirty !== v) { this.dirty = v; this.dirtyChange.emit(v); } }

  schedulePreview() { this.previewReq.next(); }

  resetSample(force = false) {
    const m = this.m();
    if (!m) return;
    if (!force) this.touch();
    const base = sampleData(m.variables);
    for (const v of extractVariables(m.subject, m.body, m.title, m.textBody)) if (!(v in base)) base[v] = v === 'CompanyName' ? 'Sample Company' : v === 'CurrentDate' ? new Date().toISOString().slice(0, 10) : `[${v}]`;
    this.sample = JSON.stringify(base, null, 2);
    this.schedulePreview();
  }

  setFormat(f: BodyFormat) {
    const m = this.m()!;
    if (f === m.bodyFormat) return;
    if (f === 'Text') m.body = m.body.replace(/<br\s*\/?>/gi, '\n').replace(/<\/p>/gi, '\n').replace(/<[^>]+>/g, '').replace(/\n{3,}/g, '\n\n').trim();
    else m.body = m.body.split(/\n{2,}/).map(p => `<p>${p.replace(/\n/g, '<br>')}</p>`).join('\n');
    m.bodyFormat = f;
    this.touch();
  }

  addVariable() {
    const focus = this.focus;
    promptDialog(this.dialog, 'New variable', 'Variable name', 'Letters, digits and underscore; must start with a letter.', 'CustomerName').subscribe(name => {
      if (!name) return;
      if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(name)) { this.previewError.set(new Error(`"${name}" is not a valid variable name.`)); return; }
      if (!this.m()!.variables.some(v => v.name.toLowerCase() === name.toLowerCase())) this.m()!.variables.push({ name, required: true, sampleValue: '', description: '' });
      this.focus = focus;
      this.insert(name);
    });
  }

  /** Inserts {{Name}} into the field that last had focus. */
  insert(name: string) {
    const m = this.m()!;
    const token = `{{${name}}}`;
    if (this.focus === 'body' && this.rte && this.mode() === 'rich' && m.bodyFormat === 'Html' && this.channel() === 'email') { this.rte.insert(token); return; }
    const target: { el?: HTMLInputElement | HTMLTextAreaElement; key: 'subject' | 'title' | 'body' | 'textBody' } =
      this.focus === 'subject' ? { el: this.subjectEl?.nativeElement, key: 'subject' }
        : this.focus === 'title' ? { el: this.titleEl?.nativeElement, key: 'title' }
          : this.focus === 'text' ? { el: this.textEl?.nativeElement, key: 'textBody' } : { el: this.htmlEl?.nativeElement, key: 'body' };
    const cur = m[target.key];
    const el = target.el;
    const at = el && el.selectionStart !== null ? el.selectionStart : cur.length;
    const end = el && el.selectionEnd !== null ? el.selectionEnd : at;
    m[target.key] = cur.slice(0, at) + token + cur.slice(end);
    this.touch();
    if (el) setTimeout(() => { el.focus(); el.setSelectionRange(at + token.length, at + token.length); });
  }

  request(): TemplateVersionRequest { return toRequest(this.m()!, this.channel()); }
  markSaved() { this.setDirty(false); }
}
