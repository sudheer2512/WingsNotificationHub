import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTooltipModule } from '@angular/material/tooltip';
import { Router } from '@angular/router';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { LookupsService } from '../core/lookups.service';
import { CHANNELS, Channel, DELIVERY_STATUSES, Group, MESSAGE_CLASSES, MessageClass, NotificationType, PRIORITIES, Rule, RuleAction, RuleRequest, SimulationResult, TemplateSummary } from '../core/models';
import { ConditionBuilder } from '../shared/condition-builder';
import { EdGroup, describe, fromApi, payloadPaths, toApi, validateTree } from '../shared/conditions';
import { UI } from '../shared/ui';
import { confirmDialog } from '../shared/widgets';

interface RuleForm {
  id: string | null; tenantId: string; companyId: string; applicationId: string; name: string; description: string; eventType: string; enabled: boolean;
  order: number; stopProcessing: boolean; notificationTypeCode: string;
  recipientType: string; recipientPath: string; recipientUsers: string; recipientRoles: string; recipientGroupId: string;
  actions: RuleAction[]; priority: string; delaySeconds: number;
  escalate: boolean; escAfter: number; escUnless: string; escChannel: Channel; escTemplate: string;
  retry: boolean; retryMax: number | null; retryInitial: number | null; retryMaxDelay: number | null;
  quiet: boolean; quietStart: string; quietEnd: string; quietClasses: MessageClass[]; quietChannels: Channel[];
}

const SAMPLE = `{
  "InvoiceNumber": "INV-1042",
  "CustomerName": "Ravi Kumar",
  "AmountDue": 25000,
  "DaysOverdue": 12,
  "DueDate": "2026-10-15",
  "PaymentLink": "https://pay.example.com/INV-1042",
  "customer": { "name": "Ravi Kumar", "email": "ravi@example.com", "mobile": "+919812345678", "tier": "gold" }
}`;

const splitList = (s: string) => s.split(',').map(x => x.trim()).filter(Boolean);

@Component({
  selector: 'app-rules',
  imports: [FormsModule, MatButtonModule, MatCheckboxModule, MatExpansionModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, MatSlideToggleModule,
    MatTooltipModule, ConditionBuilder, ...UI],
  template: `
    <div class="page">
      <div class="page-head">
        <h1>Rule designer</h1><span class="sub">Business events → conditions → recipients → channel actions</span><span class="spacer"></span>
        @if (canManage()) { <button mat-flat-button (click)="newRule()"><mat-icon>add</mat-icon>New rule</button> }
      </div>
      <div class="split rules">
        <section class="panel">
          <mat-form-field class="full"><mat-label>Filter by event type</mat-label>
            <mat-select [(ngModel)]="eventFilter"><mat-option value="">All events</mat-option>@for (e of eventTypes(); track e) { <mat-option [value]="e">{{ e }}</mat-option> }</mat-select></mat-form-field>
          <app-loading [on]="loading()" />
          <app-error [error]="listError()" />
          <div class="list-scroll">
            @for (g of grouped(); track g.event) {
              <h3 style="margin:8px 0 4px"><code>{{ g.event }}</code></h3>
              @for (r of g.rules; track r.id) {
                <div class="rule-item" [class.selected]="r.id === f()?.id" (click)="select(r)" tabindex="0" (keyup.enter)="select(r)">
                  <div class="row"><b class="ellipsis">{{ r.name }}</b><span class="spacer"></span>
                    @if (!r.enabled) { <app-chip value="Disabled" tone="off" /> }<span class="small muted">#{{ r.order }}</span></div>
                  <div class="small muted">@for (a of r.actions; track $index) { <span class="tag">{{ a.channel | channel }}@if (a.fallbackChannel) { → {{ a.fallbackChannel | channel }} }</span> }
                    · {{ r.recipients.type }}</div>
                </div>
              }
            } @empty { <app-empty icon="rule" text="No rules" /> }
          </div>
        </section>

        <section style="min-width:0">
          <app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />
          @if (f(); as f) {
            <div class="panel">
              <div class="panel-head">
                <h2>{{ f.id ? 'Edit rule' : 'New rule' }}</h2><span class="spacer"></span>
                @if (canManage()) {
                  @if (f.id) { <button mat-button (click)="duplicate()"><mat-icon>content_copy</mat-icon>Duplicate</button>
                    <button mat-button class="danger" (click)="remove()"><mat-icon>delete</mat-icon>Delete</button> }
                  <button mat-flat-button (click)="save()" [disabled]="busy()"><mat-icon>save</mat-icon>Save</button>
                }
              </div>
              <div class="form-grid">
                <mat-form-field><mat-label>Tenant</mat-label>
                  <mat-select [(ngModel)]="f.tenantId" (selectionChange)="f.companyId = ''; f.applicationId = ''; loadGroups()" [disabled]="!!f.id">
                    @for (t of lookups.tenants(); track t.id) { <mat-option [value]="t.id">{{ t.name }}</mat-option> }</mat-select></mat-form-field>
                <mat-form-field><mat-label>Company</mat-label>
                  <mat-select [(ngModel)]="f.companyId" (selectionChange)="loadGroups()"><mat-option value="">All companies</mat-option>
                    @for (c of lookups.companiesOf(f.tenantId); track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }</mat-select></mat-form-field>
                <mat-form-field><mat-label>Application</mat-label>
                  <mat-select [(ngModel)]="f.applicationId"><mat-option value="">All applications</mat-option>
                    @for (a of lookups.appsOf(f.tenantId); track a.id) { <mat-option [value]="a.id">{{ a.name }}</mat-option> }</mat-select></mat-form-field>
                <mat-form-field><mat-label>Name</mat-label><input matInput [(ngModel)]="f.name" required></mat-form-field>
                <mat-form-field><mat-label>Event type</mat-label><input matInput [(ngModel)]="f.eventType" list="rule-events" required placeholder="invoice.created"></mat-form-field>
                <datalist id="rule-events">@for (e of eventTypes(); track e) { <option [value]="e"></option> }</datalist>
                <mat-form-field><mat-label>Notification type</mat-label>
                  <mat-select [(ngModel)]="f.notificationTypeCode"><mat-option value="">None</mat-option>
                    @for (t of types(); track t.id) { <mat-option [value]="t.code">{{ t.code }} · {{ t.messageClass }}</mat-option> }</mat-select></mat-form-field>
                <mat-form-field><mat-label>Order (lower runs first)</mat-label><input matInput type="number" [(ngModel)]="f.order"></mat-form-field>
                <mat-form-field class="span-2"><mat-label>Description</mat-label><input matInput [(ngModel)]="f.description"></mat-form-field>
              </div>
              <div class="wrap-row">
                <mat-slide-toggle [(ngModel)]="f.enabled">Enabled</mat-slide-toggle>
                <mat-checkbox [(ngModel)]="f.stopProcessing">Stop evaluating later rules when this one matches</mat-checkbox>
              </div>
            </div>

            <div class="panel">
              <div class="panel-head"><h2>Conditions</h2><span class="spacer"></span><span class="small muted ellipsis" style="max-width:60%">{{ summary() }}</span></div>
              <app-condition-builder [root]="tree" [fields]="fieldSuggestions()" [readonly]="!canManage()" (changed)="treeVersion.set(treeVersion() + 1)" />
              @for (e of treeErrors(); track $index) { <div class="error small">{{ e }}</div> }
            </div>

            <div class="panel">
              <h2>Recipients</h2>
              <div class="form-grid">
                <mat-form-field><mat-label>Recipient source</mat-label>
                  <mat-select [(ngModel)]="f.recipientType">
                    <mat-option value="payload">From the event payload</mat-option><mat-option value="users">Specific users</mat-option>
                    <mat-option value="roles">Users with roles</mat-option><mat-option value="group">Recipient group</mat-option><mat-option value="company">All company users</mat-option>
                  </mat-select></mat-form-field>
                @switch (f.recipientType) {
                  @case ('payload') { <mat-form-field><mat-label>Payload path</mat-label><input matInput [(ngModel)]="f.recipientPath" placeholder="customer" list="payload-objects">
                    <mat-hint>Object or array with name / email / mobile / whatsApp / userId</mat-hint></mat-form-field>
                    <datalist id="payload-objects">@for (p of objectPaths(); track p) { <option [value]="p"></option> }</datalist> }
                  @case ('users') { <mat-form-field class="span-2"><mat-label>User ids (comma-separated)</mat-label><input matInput [(ngModel)]="f.recipientUsers"></mat-form-field> }
                  @case ('roles') { <mat-form-field class="span-2"><mat-label>Roles (comma-separated)</mat-label><input matInput [(ngModel)]="f.recipientRoles" placeholder="Manager, StoreKeeper"></mat-form-field> }
                  @case ('group') { <mat-form-field><mat-label>Group</mat-label>
                    <mat-select [(ngModel)]="f.recipientGroupId">@for (g of groups(); track g.id) { <mat-option [value]="g.id">{{ g.name }} ({{ g.members }}) · {{ lookups.companyName(g.companyId) }}</mat-option> }</mat-select></mat-form-field> }
                }
              </div>
            </div>

            <div class="panel">
              <div class="panel-head"><h2>Channel actions</h2><span class="spacer"></span>
                @if (canManage()) { <button mat-button (click)="f.actions.push({ channel: 'email', templateCode: '' })"><mat-icon>add</mat-icon>Action</button> }</div>
              @for (a of f.actions; track $index; let i = $index) {
                <div class="wrap-row action-row">
                  <mat-form-field style="width:140px"><mat-label>Channel</mat-label>
                    <mat-select [(ngModel)]="a.channel">@for (c of channels; track c) { <mat-option [value]="c">{{ c | channel }}</mat-option> }</mat-select></mat-form-field>
                  <mat-form-field style="width:220px"><mat-label>Template</mat-label>
                    <mat-select [(ngModel)]="a.templateCode">@for (t of templatesFor(a.channel); track t) { <mat-option [value]="t">{{ t }}</mat-option> }
                      @if (a.templateCode && !templatesFor(a.channel).includes(a.templateCode)) { <mat-option [value]="a.templateCode">{{ a.templateCode }} (not found)</mat-option> }</mat-select></mat-form-field>
                  <mat-icon class="muted">subdirectory_arrow_right</mat-icon>
                  <mat-form-field style="width:140px"><mat-label>Fallback</mat-label>
                    <mat-select [(ngModel)]="a.fallbackChannel"><mat-option [value]="null">None</mat-option>@for (c of channels; track c) { <mat-option [value]="c">{{ c | channel }}</mat-option> }</mat-select></mat-form-field>
                  @if (a.fallbackChannel) {
                    <mat-form-field style="width:220px"><mat-label>Fallback template</mat-label>
                      <mat-select [(ngModel)]="a.fallbackTemplateCode"><mat-option [value]="null">Same code</mat-option>@for (t of templatesFor(a.fallbackChannel); track t) { <mat-option [value]="t">{{ t }}</mat-option> }</mat-select></mat-form-field>
                  }
                  @if (canManage()) { <button mat-icon-button (click)="f.actions.splice(i, 1)" aria-label="Remove action"><mat-icon>delete</mat-icon></button> }
                </div>
              } @empty { <div class="small error">At least one channel action is required.</div> }
            </div>

            <mat-accordion multi>
              <mat-expansion-panel>
                <mat-expansion-panel-header><mat-panel-title>Priority, delay & retry</mat-panel-title>
                  <mat-panel-description>{{ f.priority || 'type default' }} · delay {{ f.delaySeconds }} s@if (f.retry) { · retry policy }</mat-panel-description></mat-expansion-panel-header>
                <div class="form-grid">
                  <mat-form-field><mat-label>Priority</mat-label><mat-select [(ngModel)]="f.priority"><mat-option value="">Notification-type default</mat-option>
                    @for (p of priorities; track p) { <mat-option [value]="p">{{ p }}</mat-option> }</mat-select></mat-form-field>
                  <mat-form-field><mat-label>Delay (seconds)</mat-label><input matInput type="number" min="0" [(ngModel)]="f.delaySeconds"></mat-form-field>
                </div>
                <mat-checkbox [(ngModel)]="f.retry">Custom retry policy</mat-checkbox>
                @if (f.retry) {
                  <div class="form-grid">
                    <mat-form-field><mat-label>Max attempts</mat-label><input matInput type="number" min="1" [(ngModel)]="f.retryMax"></mat-form-field>
                    <mat-form-field><mat-label>Initial delay (s)</mat-label><input matInput type="number" min="1" [(ngModel)]="f.retryInitial"></mat-form-field>
                    <mat-form-field><mat-label>Max delay (s)</mat-label><input matInput type="number" min="1" [(ngModel)]="f.retryMaxDelay"></mat-form-field>
                  </div>
                }
              </mat-expansion-panel>
              <mat-expansion-panel>
                <mat-expansion-panel-header><mat-panel-title>Escalation</mat-panel-title>
                  <mat-panel-description>@if (f.escalate) { after {{ f.escAfter }} min unless {{ f.escUnless }} → {{ f.escChannel | channel }} } @else { off }</mat-panel-description></mat-expansion-panel-header>
                <mat-checkbox [(ngModel)]="f.escalate">Escalate when the message is not acted on</mat-checkbox>
                @if (f.escalate) {
                  <div class="form-grid">
                    <mat-form-field><mat-label>After (minutes)</mat-label><input matInput type="number" min="1" [(ngModel)]="f.escAfter"></mat-form-field>
                    <mat-form-field><mat-label>Unless status reached</mat-label><mat-select [(ngModel)]="f.escUnless">@for (s of statuses; track s) { <mat-option [value]="s">{{ s }}</mat-option> }</mat-select></mat-form-field>
                    <mat-form-field><mat-label>Escalation channel</mat-label><mat-select [(ngModel)]="f.escChannel">@for (c of channels; track c) { <mat-option [value]="c">{{ c | channel }}</mat-option> }</mat-select></mat-form-field>
                    <mat-form-field><mat-label>Template</mat-label><mat-select [(ngModel)]="f.escTemplate"><mat-option value="">Same code</mat-option>@for (t of templatesFor(f.escChannel); track t) { <mat-option [value]="t">{{ t }}</mat-option> }</mat-select></mat-form-field>
                  </div>
                }
              </mat-expansion-panel>
              <mat-expansion-panel>
                <mat-expansion-panel-header><mat-panel-title>Quiet hours</mat-panel-title>
                  <mat-panel-description>@if (f.quiet) { {{ f.quietStart }}–{{ f.quietEnd }} recipient local time } @else { company default }</mat-panel-description></mat-expansion-panel-header>
                <mat-checkbox [(ngModel)]="f.quiet">Override quiet hours for this rule</mat-checkbox>
                @if (f.quiet) {
                  <div class="form-grid">
                    <mat-form-field><mat-label>Start</mat-label><input matInput type="time" [(ngModel)]="f.quietStart"></mat-form-field>
                    <mat-form-field><mat-label>End</mat-label><input matInput type="time" [(ngModel)]="f.quietEnd"></mat-form-field>
                    <mat-form-field><mat-label>Applies to</mat-label><mat-select [(ngModel)]="f.quietClasses" multiple>@for (c of classes; track c) { <mat-option [value]="c">{{ c }}</mat-option> }</mat-select></mat-form-field>
                    <mat-form-field><mat-label>Channels (empty = all except in-app)</mat-label><mat-select [(ngModel)]="f.quietChannels" multiple>@for (c of channels; track c) { <mat-option [value]="c">{{ c | channel }}</mat-option> }</mat-select></mat-form-field>
                  </div>
                  <div class="small muted">Messages inside the window are deferred to its end. Critical messages and critical priority are never deferred.</div>
                }
              </mat-expansion-panel>
            </mat-accordion>

            <section class="panel" style="margin-top:12px">
              <div class="panel-head"><h2>Simulation</h2><span class="small muted">evaluates a sample event; nothing is sent</span></div>
              <div class="form-grid">
                <mat-form-field><mat-label>Company</mat-label>
                  <mat-select [(ngModel)]="simCompany">@for (c of lookups.companiesOf(f.tenantId); track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }</mat-select></mat-form-field>
                <mat-form-field><mat-label>Application</mat-label>
                  <mat-select [(ngModel)]="simApp">@for (a of lookups.appsForCompany(simCompany); track a.id) { <mat-option [value]="a.id">{{ a.name }}</mat-option> }</mat-select></mat-form-field>
                <mat-form-field><mat-label>Event type</mat-label><input matInput [ngModel]="f.eventType" readonly></mat-form-field>
              </div>
              <mat-form-field class="full"><mat-label>Sample payload (JSON)</mat-label><textarea matInput class="code" rows="8" [(ngModel)]="samplePayload"></textarea></mat-form-field>
              <div class="wrap-row">
                <mat-checkbox [(ngModel)]="simOnlyThis" [disabled]="!f.id">Only this rule (saved version, even if disabled)</mat-checkbox>
                <span class="spacer"></span>
                <button mat-stroked-button (click)="simulate()" [disabled]="simBusy() || !simCompany || !simApp"><mat-icon>science</mat-icon>Run simulation</button>
              </div>
              @if (f.id && dirtySinceLoad()) { <div class="small muted">Simulation uses saved rules: save first to test your edits.</div> }
              <app-error [error]="simError()" />
              @if (sim(); as s) {
                <h3 style="margin:10px 0 4px">Rules evaluated</h3>
                @for (r of s.rules; track r.ruleId) { <div class="row"><app-chip [tone]="r.matched ? 'good' : 'off'" [label]="r.matched ? 'Matched' : 'No match'" /> {{ r.name }} <span class="small muted">{{ r.reason }}</span></div> }
                @empty { <div class="small muted">No enabled rules for this event type and scope.</div> }
                @for (e of s.errors; track $index) { <div class="callout bad small">{{ e }}</div> }
                @if (s.notifications.length) { <h3 style="margin:10px 0 4px">Notifications that would be created</h3> }
                @for (n of s.notifications; track $index) {
                  <div class="panel">
                    @for (w of n.warnings; track $index) { <div class="small warn-text">{{ w }}</div> }
                    @for (d of n.deliveries; track $index) { <div class="row small"><app-channel [value]="d.channel" /> {{ d.recipient }} <app-chip [value]="d.status" /> <span class="muted">{{ d.reason }}</span></div> }
                    @for (p of n.preview ?? []; track $index) {
                      <div class="sim-preview">
                        <div class="small muted"><app-channel [value]="p.channel" /> to {{ p.recipient }}@if (p.policyDecision) { · {{ p.policyDecision }} }@if (p.sendAt) { · send at {{ p.sendAt | dt }} }</div>
                        @if (p.subject) { <b>{{ p.subject }}</b> }
                        <pre class="json">{{ p.textBody ?? p.body }}</pre>
                      </div>
                    }
                  </div>
                }
              }
            </section>
          } @else {
            <div class="panel"><app-empty icon="rule" text="Select a rule" hint="Rules turn business events (POST /api/v1/events) into notifications." /></div>
          }
        </section>
      </div>
    </div>`,
  styles: [`
    .rules { grid-template-columns: minmax(240px, 340px) 1fr; }
    @media (max-width: 1200px) { .rules { grid-template-columns: 1fr; } .rules .list-scroll { max-height: 300px; } }
    .rule-item { padding: 6px 8px; border-radius: 6px; cursor: pointer; border: 1px solid transparent; }
    .rule-item:hover { background: var(--hover); } .rule-item.selected { background: var(--selected); border-color: var(--border); }
    section > .panel + .panel, section > .panel + mat-accordion, mat-accordion + .panel { margin-top: 12px; }
    .action-row { border-bottom: 1px dashed var(--border); padding: 4px 0; }
    .sim-preview { border-top: 1px dashed var(--border); margin-top: 6px; padding-top: 6px; }
    .warn-text { color: var(--warn); }`],
})
export class RuleDesigner implements OnInit {
  private api = inject(ApiService);
  private snack = inject(MatSnackBar);
  private dialog = inject(MatDialog);
  private router = inject(Router);
  auth = inject(AuthService);
  lookups = inject(LookupsService);

  id = input<string>();
  channels = CHANNELS; priorities = PRIORITIES; classes = MESSAGE_CLASSES; statuses = DELIVERY_STATUSES;
  rules = signal<Rule[]>([]);
  types = signal<NotificationType[]>([]);
  templates = signal<TemplateSummary[]>([]);
  groups = signal<Group[]>([]);
  loading = signal(false);
  listError = signal<unknown>(null);
  error = signal<unknown>(null);
  busy = signal(false);
  f = signal<RuleForm | null>(null);
  tree: EdGroup = fromApi(null);
  treeVersion = signal(0);
  eventFilter = '';
  samplePayload = SAMPLE;
  simCompany = ''; simApp = ''; simOnlyThis = false;
  sim = signal<SimulationResult | null>(null);
  simError = signal<unknown>(null);
  simBusy = signal(false);
  private loadedJson = '';

  canManage = computed(() => this.auth.can('ManageRules'));
  eventTypes = computed(() => [...new Set(this.rules().map(r => r.eventType))].sort());
  grouped = computed(() => {
    const map = new Map<string, Rule[]>();
    for (const r of this.rules()) if (!this.eventFilter || r.eventType === this.eventFilter) map.set(r.eventType, [...(map.get(r.eventType) ?? []), r]);
    return [...map.entries()].sort((a, b) => a[0].localeCompare(b[0])).map(([event, rules]) => ({ event, rules: rules.sort((a, b) => a.order - b.order) }));
  });
  summary = computed(() => { this.treeVersion(); return describe(toApi(this.tree)); });
  treeErrors = computed(() => { this.treeVersion(); return validateTree(this.tree); });
  fieldSuggestions = computed(() => { try { return payloadPaths(JSON.parse(this.samplePayload)); } catch { return []; } });
  objectPaths = computed(() => { try { return payloadPaths(JSON.parse(this.samplePayload), '').map(p => p.slice(1)); } catch { return []; } });

  ngOnInit() {
    this.load();
    this.api.notificationTypes().subscribe({ next: t => this.types.set(t), error: () => undefined });
    this.api.templates().subscribe({ next: t => { this.templates.set(t); const cur = this.rules().find(x => x.id === this.f()?.id); if (cur) this.samplePayload = this.sampleFor(cur); }, error: () => undefined });
  }

  load(selectId?: string) {
    this.loading.set(true);
    this.api.rules().subscribe({
      next: r => {
        this.rules.set(r); this.loading.set(false); this.listError.set(null);
        const id = selectId ?? this.id();
        const found = id ? r.find(x => x.id === id) : null;
        if (found) this.select(found);
      },
      error: e => { this.listError.set(e); this.loading.set(false); },
    });
  }

  templatesFor(channel: Channel | null | undefined): string[] {
    return [...new Set(this.templates().filter(t => !channel || t.channel === channel).map(t => t.code))].sort();
  }

  loadGroups() {
    const f = this.f();
    if (!f || !this.auth.can('ViewContacts')) return;
    this.api.groups(f.companyId || undefined).subscribe({ next: g => this.groups.set(g.filter(x => !f.tenantId || this.lookups.company(x.companyId)?.tenantId === f.tenantId)), error: () => undefined });
  }

  select(r: Rule) {
    this.f.set(this.toForm(r));
    this.tree = fromApi(r.conditions);
    this.treeVersion.update(v => v + 1);
    this.sim.set(null); this.simError.set(null); this.error.set(null);
    this.simOnlyThis = true;
    this.samplePayload = this.sampleFor(r);
    this.pickSimScope(r.companyId ?? undefined, r.applicationId ?? undefined, r.tenantId);
    this.loadGroups();
    this.loadedJson = JSON.stringify(this.toRequest());
    this.router.navigate([], { queryParams: { id: r.id }, replaceUrl: true });
  }

  newRule() {
    const tenantId = this.lookups.tenants().find(t => this.lookups.companiesOf(t.id).length)?.id ?? this.lookups.tenants()[0]?.id ?? '';
    this.f.set(this.toForm({ tenantId, name: '', eventType: this.eventFilter || '', enabled: true, order: 0, stopProcessing: false, recipients: { type: 'payload', path: 'customer' },
      actions: [{ channel: 'email', templateCode: '' }], delaySeconds: 0 } as unknown as Rule));
    this.f()!.id = null;
    this.tree = fromApi(null);
    this.treeVersion.update(v => v + 1);
    this.sim.set(null);
    this.simOnlyThis = false;
    this.pickSimScope(undefined, undefined, tenantId);
    this.loadGroups();
    this.loadedJson = '';
    this.router.navigate([], { queryParams: { id: null }, replaceUrl: true });
  }

  duplicate() {
    const f = this.f();
    if (!f) return;
    this.f.set({ ...structuredClone(f), id: null, name: f.name + ' (copy)', enabled: false });
    this.simOnlyThis = false;
    this.snack.open('Copy created (disabled). Save to keep it.', 'OK', { duration: 3500 });
  }

  private pickSimScope(companyId?: string, appId?: string, tenantId?: string) {
    const companies = this.lookups.companiesOf(tenantId);
    this.simCompany = companyId ?? companies.find(c => this.lookups.appsForCompany(c.id).length)?.id ?? companies[0]?.id ?? '';
    const apps = this.lookups.appsForCompany(this.simCompany);
    this.simApp = appId && apps.some(a => a.id === appId) ? appId : apps[0]?.id ?? '';
  }

  /** Sample event payload for a rule: its templates' sample values plus a recipient object at the payload path. */
  private sampleFor(r: Rule): string {
    const payload: Record<string, unknown> = {};
    for (const a of r.actions ?? []) {
      for (const code of [a.templateCode, a.fallbackTemplateCode].filter(Boolean)) {
        for (const t of this.templates().filter(x => x.code === code)) {
          for (const v of t.variables) if (!(v.name in payload)) payload[v.name] = v.sampleValue != null && /^-?\d+(\.\d+)?$/.test(v.sampleValue) ? Number(v.sampleValue) : v.sampleValue ?? `[${v.name}]`;
        }
      }
    }
    // Make leaf conditions true where the value is a simple number/string so the sample matches by default.
    const walk = (n: import('../core/models').ConditionNode | null | undefined) => {
      if (!n) return;
      if (n.op) { (n.children ?? []).forEach(walk); return; }
      const path = (n.field ?? '').replace(/^payload\./i, '');
      if (!path || path.includes('.') || path in payload) return;
      const v = n.value;
      payload[path] = typeof v === 'number' ? (n.comparison === 'gt' ? v + 1 : n.comparison === 'lt' ? v - 1 : v) : v ?? 'sample';
    };
    walk(r.conditions);
    const rp = r.recipients?.type === 'payload' ? r.recipients.path || 'customer' : null;
    if (rp) payload[rp] = { name: 'Ravi Kumar', email: 'ravi@example.com', mobile: '+919812345678', whatsApp: '+919812345678', userId: 'u1', tier: 'gold' };
    return JSON.stringify(payload, null, 2);
  }

  dirtySinceLoad() { return !!this.loadedJson && this.loadedJson !== JSON.stringify(this.toRequest()); }

  private toForm(r: Rule): RuleForm {
    return {
      id: r.id ?? null, tenantId: r.tenantId, companyId: r.companyId ?? '', applicationId: r.applicationId ?? '', name: r.name, description: r.description ?? '', eventType: r.eventType,
      enabled: r.enabled, order: r.order, stopProcessing: r.stopProcessing, notificationTypeCode: r.notificationTypeCode ?? '',
      recipientType: r.recipients?.type ?? 'payload', recipientPath: r.recipients?.path ?? '', recipientUsers: (r.recipients?.userIds ?? []).join(', '),
      recipientRoles: (r.recipients?.roles ?? []).join(', '), recipientGroupId: r.recipients?.groupId ?? '',
      actions: (r.actions ?? []).map(a => ({ channel: a.channel, templateCode: a.templateCode, fallbackChannel: a.fallbackChannel ?? null, fallbackTemplateCode: a.fallbackTemplateCode ?? null })),
      priority: r.priority ?? '', delaySeconds: r.delaySeconds ?? 0,
      escalate: !!r.escalation, escAfter: r.escalation?.afterMinutes ?? 30, escUnless: r.escalation?.unlessStatus ?? 'Read', escChannel: r.escalation?.channel ?? 'sms', escTemplate: r.escalation?.templateCode ?? '',
      retry: !!r.retryPolicy, retryMax: r.retryPolicy?.maxAttempts ?? null, retryInitial: r.retryPolicy?.initialDelaySeconds ?? null, retryMaxDelay: r.retryPolicy?.maxDelaySeconds ?? null,
      quiet: !!r.quietHours, quietStart: r.quietHours?.start ?? '21:00', quietEnd: r.quietHours?.end ?? '08:00', quietClasses: r.quietHours?.appliesTo ?? ['Promotional'], quietChannels: r.quietHours?.channels ?? [],
    };
  }

  private toRequest(): RuleRequest {
    const f = this.f()!;
    const recipients = f.recipientType === 'payload' ? { type: 'payload', path: f.recipientPath || null }
      : f.recipientType === 'users' ? { type: 'users', userIds: splitList(f.recipientUsers) }
        : f.recipientType === 'roles' ? { type: 'roles', roles: splitList(f.recipientRoles) }
          : f.recipientType === 'group' ? { type: 'group', groupId: f.recipientGroupId || null } : { type: 'company' };
    return {
      tenantId: f.tenantId, companyId: f.companyId || null, applicationId: f.applicationId || null, name: f.name, description: f.description || null, eventType: f.eventType,
      enabled: f.enabled, order: Number(f.order) || 0, stopProcessing: f.stopProcessing, notificationTypeCode: f.notificationTypeCode || null, conditions: toApi(this.tree),
      recipients, actions: f.actions.map(a => ({ channel: a.channel, templateCode: a.templateCode, fallbackChannel: a.fallbackChannel || null, fallbackTemplateCode: a.fallbackChannel ? a.fallbackTemplateCode || null : null })),
      priority: (f.priority || null) as RuleRequest['priority'], delaySeconds: Number(f.delaySeconds) || 0,
      escalation: f.escalate ? { afterMinutes: Number(f.escAfter) || 30, unlessStatus: f.escUnless as never, channel: f.escChannel, templateCode: f.escTemplate || null } : null,
      retryPolicy: f.retry ? { maxAttempts: f.retryMax, initialDelaySeconds: f.retryInitial, maxDelaySeconds: f.retryMaxDelay } : null,
      quietHours: f.quiet ? { start: f.quietStart, end: f.quietEnd, appliesTo: f.quietClasses, channels: f.quietChannels, enabled: true } : null,
    };
  }

  save() {
    const f = this.f();
    if (!f) return;
    const errs = validateTree(this.tree);
    if (errs.length) { this.error.set(new Error(errs.join(' '))); return; }
    this.busy.set(true); this.error.set(null);
    const body = this.toRequest();
    (f.id ? this.api.updateRule(f.id, body) : this.api.createRule(body)).subscribe({
      next: r => { this.busy.set(false); this.snack.open('Rule saved', 'OK', { duration: 3000 }); this.load(r.id); },
      error: e => { this.busy.set(false); this.error.set(e); },
    });
  }

  remove() {
    const f = this.f();
    if (!f?.id) return;
    confirmDialog(this.dialog, { title: 'Delete rule', message: `Delete "${f.name}"? Events will no longer trigger it.`, confirmText: 'Delete', danger: true }).subscribe(ok => {
      if (!ok) return;
      this.api.deleteRule(f.id!).subscribe({ next: () => { this.f.set(null); this.load(); this.snack.open('Rule deleted', 'OK', { duration: 3000 }); }, error: e => this.error.set(e) });
    });
  }

  simulate() {
    const f = this.f();
    if (!f) return;
    let payload: unknown;
    try { payload = JSON.parse(this.samplePayload); } catch (e) { this.simError.set(new Error('Sample payload is not valid JSON: ' + (e as Error).message)); return; }
    this.simBusy.set(true); this.simError.set(null); this.sim.set(null);
    this.api.simulate({ companyId: this.simCompany, applicationId: this.simApp, eventType: f.eventType, payload }, this.simOnlyThis && f.id ? f.id : null).subscribe({
      next: s => { this.simBusy.set(false); this.sim.set(s); },
      error: e => { this.simBusy.set(false); this.simError.set(e); },
    });
  }
}
