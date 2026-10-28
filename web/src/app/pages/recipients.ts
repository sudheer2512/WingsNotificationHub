import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MAT_DIALOG_DATA, MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatSelectModule } from '@angular/material/select';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTabsModule } from '@angular/material/tabs';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { LookupsService } from '../core/lookups.service';
import { CHANNELS, Channel, Contact, Group, Preferences, Suppression } from '../core/models';
import { ConditionBuilder } from '../shared/condition-builder';
import { EdGroup, describe, fromApi, toApi, validateTree } from '../shared/conditions';
import { UI } from '../shared/ui';
import { confirmDialog } from '../shared/widgets';

/** Add / update a contact (PUT /contacts upserts by company + kind + externalId). */
@Component({
  selector: 'app-contact-form',
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, ...UI],
  template: `
    <h2 mat-dialog-title>{{ data ? 'Edit contact' : 'Add contact' }}</h2>
    <mat-dialog-content>
      <div class="form-grid">
        <mat-form-field><mat-label>Company</mat-label><mat-select [(ngModel)]="m.companyId" [disabled]="!!data">@for (c of lookups.companies(); track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }</mat-select></mat-form-field>
        <mat-form-field><mat-label>Kind</mat-label><mat-select [(ngModel)]="m.kind" [disabled]="!!data"><mat-option value="User">User</mat-option><mat-option value="Customer">Customer</mat-option><mat-option value="Other">Other</mat-option></mat-select></mat-form-field>
        <mat-form-field><mat-label>External id</mat-label><input matInput [(ngModel)]="m.externalId" [readonly]="!!data" required></mat-form-field>
        <mat-form-field><mat-label>Name</mat-label><input matInput [(ngModel)]="m.name"></mat-form-field>
        <mat-form-field><mat-label>Email</mat-label><input matInput type="email" [(ngModel)]="m.email" [placeholder]="data?.email ?? ''"></mat-form-field>
        <mat-form-field><mat-label>Mobile (E.164)</mat-label><input matInput [(ngModel)]="m.mobile" [placeholder]="data?.mobile ?? '+9198…'"></mat-form-field>
        <mat-form-field><mat-label>WhatsApp (E.164)</mat-label><input matInput [(ngModel)]="m.whatsApp" [placeholder]="data?.whatsapp ?? ''"></mat-form-field>
        <mat-form-field><mat-label>Time zone</mat-label><input matInput [(ngModel)]="m.timeZone" placeholder="Asia/Kolkata"></mat-form-field>
        <mat-form-field><mat-label>Locale</mat-label><input matInput [(ngModel)]="m.locale" placeholder="en"></mat-form-field>
        <mat-form-field><mat-label>Country</mat-label><input matInput [(ngModel)]="m.country" placeholder="IN"></mat-form-field>
        <mat-form-field class="span-2"><mat-label>Roles (comma-separated)</mat-label><input matInput [(ngModel)]="m.roles"></mat-form-field>
      </div>
      @if (data) { <div class="small muted">Masked values are shown as placeholders; leave a field empty to keep the stored value.</div> }
      <app-error [error]="error()" />
    </mat-dialog-content>
    <mat-dialog-actions align="end"><button mat-button mat-dialog-close>Cancel</button>
      <button mat-flat-button (click)="save()" [disabled]="busy() || !m.companyId || !m.externalId.trim()">Save</button></mat-dialog-actions>`,
})
export class ContactForm {
  private api = inject(ApiService);
  private ref = inject(MatDialogRef<ContactForm>);
  lookups = inject(LookupsService);
  data = inject<Contact | null>(MAT_DIALOG_DATA, { optional: true });
  busy = signal(false); error = signal<unknown>(null);
  m = { companyId: this.data?.companyId ?? this.lookups.companies()[0]?.id ?? '', kind: this.data?.kind ?? 'Customer', externalId: this.data?.externalId ?? '', name: this.data?.name ?? '',
    email: '', mobile: '', whatsApp: '', timeZone: this.data?.timeZone ?? '', locale: this.data?.locale ?? '', country: this.data?.country ?? '', roles: (this.data?.roles ?? []).join(', ') };
  save() {
    const n = (s: string) => s.trim() || null;
    this.busy.set(true); this.error.set(null);
    this.api.upsertContact({ companyId: this.m.companyId, kind: this.m.kind, externalId: this.m.externalId.trim(), name: n(this.m.name), email: n(this.m.email), mobile: n(this.m.mobile),
      whatsApp: n(this.m.whatsApp), timeZone: n(this.m.timeZone), locale: n(this.m.locale), country: n(this.m.country), roles: this.m.roles.split(',').map(s => s.trim()).filter(Boolean) })
      .subscribe({ next: c => this.ref.close(c), error: e => { this.busy.set(false); this.error.set(e); } });
  }
}

@Component({
  selector: 'app-recipients',
  imports: [FormsModule, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatIconModule, MatInputModule, MatPaginatorModule, MatSelectModule, MatSlideToggleModule, MatTabsModule,
    ConditionBuilder, ...UI],
  template: `
    <div class="page">
      <div class="page-head"><h1>Recipients</h1><span class="sub">Contacts, preferences, consent, suppressions and groups</span></div>
      <app-error [error]="error()" [dismissable]="true" (dismiss)="error.set(null)" />
      <mat-tab-group mat-stretch-tabs="false" [(selectedIndex)]="tab">
        <mat-tab label="Contacts">
          <div class="filters">
            <mat-form-field class="wide"><mat-label>Search</mat-label><input matInput [(ngModel)]="q" (keyup.enter)="page = 1; loadContacts()" placeholder="id, name, email or mobile"></mat-form-field>
            <mat-form-field><mat-label>Company</mat-label><mat-select [(ngModel)]="companyId" (selectionChange)="page = 1; loadContacts()"><mat-option value="">All</mat-option>
              @for (c of lookups.companies(); track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }</mat-select></mat-form-field>
            <mat-form-field><mat-label>Kind</mat-label><mat-select [(ngModel)]="kind" (selectionChange)="page = 1; loadContacts()"><mat-option value="">All</mat-option>
              <mat-option value="User">User</mat-option><mat-option value="Customer">Customer</mat-option><mat-option value="Other">Other</mat-option></mat-select></mat-form-field>
            <span class="spacer"></span>
            @if (canManage()) { <button mat-flat-button (click)="editContact()"><mat-icon>person_add</mat-icon>Add contact</button> }
          </div>
          <div class="table-wrap">
            <table class="data">
              <thead><tr><th>Name</th><th>External id</th><th>Kind</th><th>Company</th><th>Email</th><th>Mobile</th><th>WhatsApp</th><th>Locale / TZ</th><th></th></tr></thead>
              <tbody>
                @for (c of contacts(); track c.id) {
                  <tr class="clickable" (click)="openPrefs(c)" [class.selected]="c.id === prefContact()?.id">
                    <td><b>{{ c.name ?? '—' }}</b>@for (r of c.roles; track r) { <span class="tag">{{ r }}</span> }</td><td><code>{{ c.externalId }}</code></td><td>{{ c.kind }}</td>
                    <td class="small">{{ lookups.companyName(c.companyId) }}</td><td>{{ c.email ?? '—' }}</td><td>{{ c.mobile ?? '—' }}</td><td>{{ c.whatsapp ?? '—' }}</td>
                    <td class="small">{{ c.locale ?? '—' }} · {{ c.timeZone ?? '—' }}</td>
                    <td class="nowrap" (click)="$event.stopPropagation()">
                      <button mat-icon-button (click)="openPrefs(c)" aria-label="Preferences and consent"><mat-icon>tune</mat-icon></button>
                      @if (canManage()) { <button mat-icon-button (click)="editContact(c)" aria-label="Edit"><mat-icon>edit</mat-icon></button>
                        <button mat-icon-button (click)="erase(c)" aria-label="Erase personal data"><mat-icon>person_remove</mat-icon></button> }
                    </td>
                  </tr>
                } @empty { <tr><td colspan="9"><app-empty icon="contacts" text="No contacts" /></td></tr> }
              </tbody>
            </table>
          </div>
          <mat-paginator [length]="total()" [pageIndex]="page - 1" [pageSize]="50" [hidePageSize]="true" (page)="onPage($event)" />
        </mat-tab>

        <mat-tab label="Preferences & consent">
          @if (prefContact(); as c) {
            <section class="panel">
              <div class="panel-head"><h2>{{ c.name ?? c.externalId }}</h2><span class="small muted">{{ c.kind }} · {{ c.externalId }} · {{ lookups.companyName(c.companyId) }}</span></div>
              <app-loading [on]="prefsLoading()" />
              @if (prefs(); as p) {
                <h3>Channel preferences</h3>
                <p class="small muted">Category <code>*</code> applies to every category. Mandatory and critical notification types ignore opt-outs (never hard suppressions).</p>
                <div class="table-wrap"><table class="data">
                  <thead><tr><th>Category</th>@for (ch of prefChannels; track ch) { <th>{{ ch | channel }}</th> }</tr></thead>
                  <tbody>
                    @for (cat of categories(); track cat) {
                      <tr><td><code>{{ cat }}</code></td>
                        @for (ch of prefChannels; track ch) {
                          <td><mat-slide-toggle [checked]="pref(cat, ch)" (change)="setPref(cat, ch, $event.checked)" [disabled]="!canManage()" [attr.aria-label]="cat + ' ' + ch" /></td>
                        }
                      </tr>
                    }
                  </tbody>
                </table></div>
                @if (canManage()) {
                  <div class="wrap-row" style="margin-top:6px">
                    <mat-form-field><mat-label>Add category</mat-label><input matInput [(ngModel)]="newCategory" placeholder="Promotions"></mat-form-field>
                    <button mat-button (click)="addCategory()" [disabled]="!newCategory.trim()">Add</button><span class="spacer"></span>
                    <button mat-flat-button (click)="savePrefs(c)" [disabled]="!prefsDirty()"><mat-icon>save</mat-icon>Save preferences</button>
                  </div>
                }
                <h3 style="margin-top:12px">Consent (latest per channel and purpose)</h3>
                <div class="table-wrap"><table class="data">
                  <thead><tr><th>Channel</th><th>Purpose</th><th>Status</th><th>Source</th><th>Evidence</th><th>Captured</th></tr></thead>
                  <tbody>@for (x of p.consents; track x.id) { <tr><td><app-channel [value]="x.channel" /></td><td>{{ x.purpose | human }}</td><td><app-chip [value]="x.status" /></td><td>{{ x.source }}</td><td class="small">{{ x.evidence ?? '—' }}</td><td class="small nowrap">{{ x.capturedAt | dt }}</td></tr> }
                    @empty { <tr><td colspan="6" class="muted">No consent recorded. WhatsApp needs channel opt-in; promotional messages need marketing consent.</td></tr> }</tbody>
                </table></div>
                @if (canManage()) {
                  <h3 style="margin-top:12px">Record consent</h3>
                  <div class="form-grid">
                    <mat-form-field><mat-label>Channel</mat-label><mat-select [(ngModel)]="consent.channel">@for (ch of prefChannels; track ch) { <mat-option [value]="ch">{{ ch | channel }}</mat-option> }</mat-select></mat-form-field>
                    <mat-form-field><mat-label>Purpose</mat-label><mat-select [(ngModel)]="consent.purpose"><mat-option value="ChannelOptIn">Channel opt-in</mat-option><mat-option value="Marketing">Marketing</mat-option></mat-select></mat-form-field>
                    <mat-form-field><mat-label>Status</mat-label><mat-select [(ngModel)]="consent.status"><mat-option value="Granted">Granted</mat-option><mat-option value="Withdrawn">Withdrawn</mat-option></mat-select></mat-form-field>
                    <mat-form-field><mat-label>Source</mat-label><input matInput [(ngModel)]="consent.source" placeholder="web-form, call-centre…"></mat-form-field>
                    <mat-form-field class="span-2"><mat-label>Evidence</mat-label><input matInput [(ngModel)]="consent.evidence" placeholder="Form submission id, recording reference…"></mat-form-field>
                  </div>
                  <button mat-stroked-button (click)="recordConsent(c)" [disabled]="!consent.source.trim()"><mat-icon>verified</mat-icon>Record</button>
                }
                @if (history().length) {
                  <h3 style="margin-top:12px">Consent history (append-only)</h3>
                  <div class="table-wrap" style="max-height:240px"><table class="data">
                    <thead><tr><th>Captured</th><th>Channel</th><th>Purpose</th><th>Status</th><th>Source</th><th>Recorded by</th></tr></thead>
                    <tbody>@for (h of history(); track $index) { <tr><td class="small nowrap">{{ $any(h)['capturedAt'] | dt }}</td><td>{{ $any(h)['channel'] | channel }}</td><td>{{ $any(h)['purpose'] }}</td>
                      <td><app-chip [value]="$any(h)['status']" /></td><td>{{ $any(h)['source'] }}</td><td class="small">{{ $any(h)['recordedBy'] ?? '' }}</td></tr> }</tbody>
                  </table></div>
                }
              }
            </section>
          } @else { <div class="panel"><app-empty icon="tune" text="Pick a contact" hint="Select a contact on the Contacts tab to view and edit preferences and consent." /></div> }
        </mat-tab>

        <mat-tab label="Suppressions">
          <div class="filters">
            <mat-form-field><mat-label>Channel</mat-label><mat-select [(ngModel)]="supChannel" (selectionChange)="loadSuppressions()"><mat-option value="">All</mat-option>
              @for (ch of channels; track ch) { <mat-option [value]="ch">{{ ch | channel }}</mat-option> }</mat-select></mat-form-field>
            <mat-form-field class="wide"><mat-label>Address contains</mat-label><input matInput [(ngModel)]="supAddress" (keyup.enter)="loadSuppressions()"></mat-form-field>
          </div>
          <div class="table-wrap"><table class="data">
            <thead><tr><th>Channel</th><th>Address</th><th>Reason</th><th>Scope</th><th>Source</th><th>Added</th><th></th></tr></thead>
            <tbody>
              @for (s of suppressions(); track s.id) {
                <tr><td><app-channel [value]="s.channel" /></td><td><code>{{ s.address }}</code></td><td>{{ s.reason | human }}@if (s.promotionalOnly) { <span class="tag">promotional only</span> }</td>
                  <td class="small">{{ s.companyId ? lookups.companyName(s.companyId) : lookups.tenantName(s.tenantId) }}</td><td class="small">{{ s.source ?? '—' }}</td><td class="small nowrap">{{ s.createdAt | dt }}</td>
                  <td>@if (canManage()) { <button mat-icon-button (click)="removeSuppression(s)" aria-label="Remove suppression"><mat-icon>delete</mat-icon></button> }</td></tr>
              } @empty { <tr><td colspan="7" class="muted">No suppressions</td></tr> }
            </tbody>
          </table></div>
          @if (canManage()) {
            <h3 style="margin:12px 0 4px">Add suppression</h3>
            <div class="form-grid">
              <mat-form-field><mat-label>Tenant</mat-label><mat-select [(ngModel)]="sup.tenantId">@for (t of lookups.tenants(); track t.id) { <mat-option [value]="t.id">{{ t.name }}</mat-option> }</mat-select></mat-form-field>
              <mat-form-field><mat-label>Company (optional)</mat-label><mat-select [(ngModel)]="sup.companyId"><mat-option value="">Whole tenant</mat-option>@for (c of lookups.companiesOf(sup.tenantId); track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }</mat-select></mat-form-field>
              <mat-form-field><mat-label>Channel</mat-label><mat-select [(ngModel)]="sup.channel">@for (ch of channels; track ch) { <mat-option [value]="ch">{{ ch | channel }}</mat-option> }</mat-select></mat-form-field>
              <mat-form-field><mat-label>Address</mat-label><input matInput [(ngModel)]="sup.address"></mat-form-field>
              <mat-form-field><mat-label>Reason</mat-label><mat-select [(ngModel)]="sup.reason">@for (r of reasons; track r) { <mat-option [value]="r">{{ r | human }}</mat-option> }</mat-select></mat-form-field>
              <mat-form-field><mat-label>Note</mat-label><input matInput [(ngModel)]="sup.note"></mat-form-field>
            </div>
            <button mat-stroked-button (click)="addSuppression()" [disabled]="!sup.tenantId || !sup.address.trim()"><mat-icon>block</mat-icon>Suppress address</button>
          }
        </mat-tab>

        <mat-tab label="Groups">
          <div class="grid-2">
            <section class="panel">
              <div class="table-wrap"><table class="data">
                <thead><tr><th>Group</th><th>Company</th><th>Type</th><th class="num">Members</th></tr></thead>
                <tbody>@for (g of groups(); track g.id) {
                  <tr class="clickable" [class.selected]="g.id === groupSel()?.id" (click)="openGroup(g)"><td><b>{{ g.name }}</b><div class="small muted">{{ g.description ?? '' }}</div></td>
                    <td class="small">{{ lookups.companyName(g.companyId) }}</td><td>{{ g.dynamic ? 'Dynamic' : 'Static' }}</td><td class="num">{{ g.members }}</td></tr>
                } @empty { <tr><td colspan="4" class="muted">No groups</td></tr> }</tbody>
              </table></div>
              @if (groupSel(); as g) {
                <h3 style="margin-top:10px">{{ g.name }} members</h3>
                @if (g.dynamic) { <div class="small muted">Filter: {{ describeFilter(g) }}</div> }
                <div class="table-wrap" style="max-height:260px"><table class="data">
                  <thead><tr><th>Name</th><th>External id</th><th>Email</th><th>Mobile</th></tr></thead>
                  <tbody>@for (m of members(); track m.id) { <tr><td>{{ m.name }}</td><td><code>{{ m.externalId }}</code></td><td>{{ m.email ?? '—' }}</td><td>{{ m.mobile ?? '—' }}</td></tr> }
                    @empty { <tr><td colspan="4" class="muted">No members</td></tr> }</tbody>
                </table></div>
              }
            </section>
            @if (canManage()) {
              <section class="panel">
                <h2>New group</h2>
                <div class="form-grid">
                  <mat-form-field><mat-label>Company</mat-label><mat-select [(ngModel)]="grp.companyId" (selectionChange)="loadGroupCandidates()">@for (c of lookups.companies(); track c.id) { <mat-option [value]="c.id">{{ c.name }}</mat-option> }</mat-select></mat-form-field>
                  <mat-form-field><mat-label>Name</mat-label><input matInput [(ngModel)]="grp.name"></mat-form-field>
                  <mat-form-field class="span-2"><mat-label>Description</mat-label><input matInput [(ngModel)]="grp.description"></mat-form-field>
                </div>
                <mat-slide-toggle [(ngModel)]="grp.dynamic">Dynamic (membership from a contact filter)</mat-slide-toggle>
                @if (grp.dynamic) {
                  <app-condition-builder [root]="grpFilter" [fields]="contactFields" hint="Contact fields: kind, externalId, name, email, mobile, country, locale, roles, attributes.<name>." />
                } @else {
                  <mat-form-field class="full" style="margin-top:8px"><mat-label>Members</mat-label>
                    <mat-select [(ngModel)]="grp.contactIds" multiple>@for (c of candidates(); track c.id) { <mat-option [value]="c.id">{{ c.name ?? c.externalId }} ({{ c.kind }})</mat-option> }</mat-select></mat-form-field>
                }
                <button mat-flat-button (click)="createGroup()" [disabled]="!grp.companyId || !grp.name.trim()"><mat-icon>group_add</mat-icon>Create group</button>
              </section>
            }
          </div>
        </mat-tab>
      </mat-tab-group>
    </div>`,
})
export class Recipients implements OnInit {
  private api = inject(ApiService);
  private dialog = inject(MatDialog);
  private snack = inject(MatSnackBar);
  auth = inject(AuthService);
  lookups = inject(LookupsService);
  channels = CHANNELS;
  prefChannels: Channel[] = ['email', 'sms', 'whatsapp', 'inapp', 'push', 'webpush'];
  reasons = ['Manual', 'HardBounce', 'Complaint', 'Unsubscribe', 'InvalidAddress', 'OptOut'];
  contactFields = ['kind', 'externalId', 'name', 'email', 'mobile', 'country', 'locale', 'roles', 'attributes.tier'];
  tab = 0;
  q = ''; companyId = ''; kind = ''; page = 1;
  contacts = signal<Contact[]>([]);
  total = signal(0);
  error = signal<unknown>(null);
  prefContact = signal<Contact | null>(null);
  prefs = signal<Preferences | null>(null);
  prefsLoading = signal(false);
  prefsDirty = signal(false);
  history = signal<Record<string, unknown>[]>([]);
  extraCategories = signal<string[]>([]);
  newCategory = '';
  consent = { channel: 'whatsapp' as Channel, purpose: 'ChannelOptIn', status: 'Granted', source: 'admin-console', evidence: '' };
  suppressions = signal<Suppression[]>([]);
  supChannel = ''; supAddress = '';
  sup = { tenantId: '', companyId: '', channel: 'email' as Channel, address: '', reason: 'Manual', note: '' };
  groups = signal<Group[]>([]);
  groupSel = signal<Group | null>(null);
  members = signal<Contact[]>([]);
  candidates = signal<Contact[]>([]);
  grp = { companyId: '', name: '', description: '', dynamic: false, contactIds: [] as string[] };
  grpFilter: EdGroup = fromApi(null);

  canManage = computed(() => this.auth.can('ManageContacts'));
  categories = computed(() => {
    const p = this.prefs();
    const cats = new Set(['*', ...(p?.preferences ?? []).map(x => x.category), ...this.extraCategories()]);
    return [...cats];
  });

  ngOnInit() {
    this.loadContacts();
    this.loadSuppressions();
    this.loadGroups();
    this.sup.tenantId = this.lookups.tenants()[0]?.id ?? '';
  }

  loadContacts() {
    this.api.contacts({ q: this.q.trim() || undefined, companyId: this.companyId || undefined, kind: this.kind || undefined, page: this.page, pageSize: 50 }).subscribe({
      next: r => { this.contacts.set(r.items); this.total.set(r.total); }, error: e => this.error.set(e),
    });
  }
  onPage(e: PageEvent) { this.page = e.pageIndex + 1; this.loadContacts(); }

  editContact(c?: Contact) {
    this.dialog.open(ContactForm, { data: c ?? null, width: '760px', maxWidth: '96vw' }).afterClosed().subscribe(r => { if (r) { this.snack.open('Contact saved', 'OK', { duration: 2500 }); this.loadContacts(); } });
  }

  erase(c: Contact) {
    confirmDialog(this.dialog, { title: 'Erase personal data', danger: true, confirmText: 'Erase',
      message: `Erase ${c.name ?? c.externalId}? Name, email, phone numbers and attributes are removed, addresses in delivery history are replaced by hashes, message bodies are purged and devices are unregistered. This cannot be undone.`,
      acknowledge: 'I understand this is permanent (right to erasure).' }).subscribe(r => {
      if (r) this.api.eraseContact(c.id).subscribe({ next: () => { this.snack.open('Personal data erased', 'OK', { duration: 3000 }); this.loadContacts(); }, error: e => this.error.set(e) });
    });
  }

  openPrefs(c: Contact) {
    this.prefContact.set(c);
    this.tab = 1;
    this.prefs.set(null); this.prefsDirty.set(false); this.extraCategories.set([]); this.history.set([]);
    this.prefsLoading.set(true);
    this.api.preferences(c.companyId, c.externalId, c.kind).subscribe({
      next: p => { this.prefs.set(p); this.prefsLoading.set(false); },
      error: e => { this.error.set(e); this.prefsLoading.set(false); },
    });
    this.api.consents(c.id).subscribe({ next: h => this.history.set(h), error: () => undefined });
  }

  pref(cat: string, ch: Channel): boolean {
    const list = this.prefs()?.preferences ?? [];
    const exact = list.find(p => p.category === cat && p.channel === ch);
    if (exact) return exact.enabled;
    const wild = list.find(p => p.category === '*' && p.channel === ch);
    return wild ? wild.enabled : true;
  }

  setPref(cat: string, ch: Channel, enabled: boolean) {
    const p = this.prefs();
    if (!p) return;
    const list = p.preferences.filter(x => !(x.category === cat && x.channel === ch));
    list.push({ category: cat, channel: ch, enabled });
    this.prefs.set({ ...p, preferences: list });
    this.prefsDirty.set(true);
  }

  addCategory() { this.extraCategories.update(c => [...c, this.newCategory.trim()]); this.newCategory = ''; }

  savePrefs(c: Contact) {
    const p = this.prefs();
    if (!p) return;
    this.api.savePreferences({ companyId: c.companyId, externalId: c.externalId, kind: c.kind, preferences: p.preferences }).subscribe({
      next: r => { this.prefs.set(r); this.prefsDirty.set(false); this.snack.open('Preferences saved', 'OK', { duration: 2500 }); }, error: e => this.error.set(e),
    });
  }

  recordConsent(c: Contact) {
    this.api.recordConsent({ companyId: c.companyId, externalId: c.externalId, channel: this.consent.channel, purpose: this.consent.purpose, status: this.consent.status,
      source: this.consent.source, evidence: this.consent.evidence || null }).subscribe({ next: () => { this.snack.open('Consent recorded', 'OK', { duration: 2500 }); this.openPrefs(c); }, error: e => this.error.set(e) });
  }

  loadSuppressions() {
    this.api.suppressions({ channel: this.supChannel || undefined, address: this.supAddress.trim() || undefined }).subscribe({ next: s => this.suppressions.set(s), error: e => this.error.set(e) });
  }

  addSuppression() {
    this.api.addSuppression({ tenantId: this.sup.tenantId, companyId: this.sup.companyId || null, channel: this.sup.channel, address: this.sup.address.trim(), reason: this.sup.reason, note: this.sup.note || null })
      .subscribe({ next: () => { this.sup.address = ''; this.loadSuppressions(); this.snack.open('Address suppressed', 'OK', { duration: 2500 }); }, error: e => this.error.set(e) });
  }

  removeSuppression(s: Suppression) {
    confirmDialog(this.dialog, { title: 'Remove suppression', message: `Allow messages to ${s.address} on ${s.channel} again?`, confirmText: 'Remove' }).subscribe(r => {
      if (r) this.api.removeSuppression(s.id).subscribe({ next: () => this.loadSuppressions(), error: e => this.error.set(e) });
    });
  }

  loadGroups() { this.api.groups().subscribe({ next: g => this.groups.set(g), error: e => this.error.set(e) }); }
  openGroup(g: Group) { this.groupSel.set(g); this.api.groupMembers(g.id).subscribe({ next: m => this.members.set(m), error: e => this.error.set(e) }); }
  describeFilter(g: Group) { return describe(g.filter ?? null); }
  loadGroupCandidates() { this.api.contacts({ companyId: this.grp.companyId, pageSize: 200 }).subscribe({ next: r => this.candidates.set(r.items), error: () => undefined }); }

  createGroup() {
    if (this.grp.dynamic) { const errs = validateTree(this.grpFilter); if (errs.length) { this.error.set(new Error(errs.join(' '))); return; } }
    this.api.createGroup({ companyId: this.grp.companyId, name: this.grp.name.trim(), description: this.grp.description || null, dynamic: this.grp.dynamic,
      filter: this.grp.dynamic ? toApi(this.grpFilter) : null, contactIds: this.grp.dynamic ? null : this.grp.contactIds })
      .subscribe({ next: () => { this.snack.open('Group created', 'OK', { duration: 2500 }); this.grp = { ...this.grp, name: '', description: '', contactIds: [] }; this.loadGroups(); }, error: e => this.error.set(e) });
  }
}
