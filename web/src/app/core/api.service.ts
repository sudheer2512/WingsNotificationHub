import { HttpClient, HttpParams, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';
import * as M from './models';

export type Q = Record<string, string | number | boolean | null | undefined>;

/**
 * Query-string Channel values. JSON bodies accept the lower-case wire names, but the API binds `?channel=` with the
 * default (case-sensitive) enum parser, so `?channel=sms` is rejected with 400 while `?channel=Sms` works.
 * Workaround for that backend bug: send the enum member name in query strings.
 */
const CHANNEL_QUERY: Record<string, string> = { email: 'Email', sms: 'Sms', whatsapp: 'WhatsApp', inapp: 'InApp', push: 'Push', webpush: 'WebPush', webhook: 'Webhook' };

export function toParams(q?: Q): HttpParams {
  let p = new HttpParams();
  for (const [k, v] of Object.entries(q ?? {})) {
    if (v === null || v === undefined || v === '') continue;
    p = p.set(k, k === 'channel' ? CHANNEL_QUERY[String(v)] ?? String(v) : String(v));
  }
  return p;
}

const BASE = '/api/v1';

export interface ReportFilter { tenantId?: string; companyId?: string; applicationId?: string; channel?: string; providerAccountId?: string; from?: string; to?: string; }
export interface DeliveryQuery extends ReportFilter {
  status?: string; notificationType?: string; recipient?: string; notificationId?: string; campaignId?: string; awaitingOperator?: boolean;
  page?: number; pageSize?: number; sort?: string;
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  private http = inject(HttpClient);

  get<T>(url: string, q?: Q) { return this.http.get<T>(BASE + url, { params: toParams(q) }); }
  post<T>(url: string, body: unknown = {}, q?: Q) { return this.http.post<T>(BASE + url, body, { params: toParams(q) }); }
  put<T>(url: string, body: unknown) { return this.http.put<T>(BASE + url, body); }
  del<T>(url: string) { return this.http.delete<T>(BASE + url); }

  /** Downloads a file endpoint (auth header included) and saves it via a temporary object URL. */
  download(url: string, q: Q, fallbackName: string): Observable<string> {
    return this.http.get(BASE + url, { params: toParams(q), responseType: 'blob', observe: 'response' }).pipe(map((r: HttpResponse<Blob>) => {
      const cd = r.headers.get('Content-Disposition') ?? '';
      const m = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(cd);
      const name = m ? decodeURIComponent(m[1]) : fallbackName;
      const href = URL.createObjectURL(r.body!);
      const a = document.createElement('a');
      a.href = href; a.download = name; document.body.appendChild(a); a.click(); a.remove();
      setTimeout(() => URL.revokeObjectURL(href), 2000);
      return name;
    }));
  }

  // ---- dashboard / monitoring / reports ----
  dashboard(f: ReportFilter) { return this.get<M.Dashboard>('/dashboard', { ...f }); }
  monitoring() { return this.get<M.Monitoring>('/monitoring'); }
  requeueDeadLetter(id: string) { return this.post<void>(`/monitoring/dead-letters/${id}/requeue`); }
  ackAlert(id: string) { return this.post<void>(`/monitoring/alerts/${id}/ack`); }
  reports() { return this.get<M.ReportInfo[]>('/reports'); }
  report(code: string, f: ReportFilter) { return this.get<M.ReportResult>(`/reports/${code}`, { ...f }); }
  usage(groupBy: string, f: ReportFilter) { return this.get<M.UsageRow[]>('/usage', { groupBy, ...f }); }
  audit(q: { tenantId?: string; entityType?: string; entityId?: string; action?: string; from?: string; page?: number }) { return this.get<{ total: number; items: M.AuditEntry[] }>('/audit', q); }

  // ---- deliveries ----
  deliveries(q: DeliveryQuery) { return this.get<M.Paged<M.ExplorerRow>>('/deliveries', { ...q }); }
  delivery(id: string) { return this.get<M.DeliveryDetail>(`/deliveries/${id}`); }
  retryDelivery(id: string) { return this.post<{ status: string }>(`/deliveries/${id}/retry`); }
  resolveDelivery(id: string, action: 'resend' | 'mark_sent' | 'mark_failed', comment?: string) { return this.post<{ status: string }>(`/deliveries/${id}/resolve`, { action, comment }); }
  history(notificationId: string) { return this.get<M.Timeline>(`/notifications/${notificationId}/history`); }
  cancelNotification(notificationId: string) { return this.post<{ cancelled: number; inFlight: number; note?: string }>(`/notifications/${notificationId}/cancel`); }
  preview(body: Record<string, unknown>) { return this.post<M.PreviewResponse>('/notifications/preview', body); }

  // ---- templates ----
  templates(q: Q = {}) { return this.get<M.TemplateSummary[]>('/templates', q); }
  templateTree() { return this.get<M.TreeNode[]>('/templates/tree'); }
  template(id: string) { return this.get<M.TemplateDetail>(`/templates/${id}`); }
  createTemplate(template: M.TemplateUpsertRequest, version: M.TemplateVersionRequest) { return this.post<{ id: string; code: string; version: number }>('/templates', { template, version }); }
  saveDraft(id: string, v: M.TemplateVersionRequest) { return this.put<{ id: string; versionNumber: number; status: string }>(`/templates/${id}/draft`, v); }
  submitVersion(id: string, n: number) { return this.post<{ status: string }>(`/templates/${id}/versions/${n}/submit`); }
  reviewVersion(id: string, n: number, approve: boolean, comment?: string, activate = true) { return this.post<{ status: string }>(`/templates/${id}/versions/${n}/review`, { approve, comment, activate }); }
  activateVersion(id: string, n: number) { return this.post<void>(`/templates/${id}/versions/${n}/activate`); }
  retireTemplate(id: string) { return this.post<void>(`/templates/${id}/retire`); }
  cloneTemplate(id: string, target: M.TemplateUpsertRequest) { return this.post<{ id: string }>(`/templates/${id}/clone`, target); }
  testSend(id: string, body: { companyId: string; applicationId?: string | null; recipient: Record<string, unknown>; variables?: Record<string, unknown>; version?: number }) {
    return this.post<{ delivered: boolean; note?: string; resp?: M.SendResponse; preview?: M.PreviewResponse }>(`/templates/${id}/test-send`, body);
  }
  whatsAppAccounts(id: string) { return this.get<M.WhatsAppAccount[]>(`/templates/${id}/whatsapp-accounts`); }
  whatsAppSubmit(id: string, providerAccountId: string, version: number) { return this.post<{ status: string }>(`/templates/${id}/whatsapp-submit`, { providerAccountId, version }); }
  notificationTypes(q: Q = {}) { return this.get<M.NotificationType[]>('/notification-types', q); }
  saveNotificationType(body: Partial<M.NotificationType>) { return this.post<M.NotificationType>('/notification-types', body); }

  // ---- providers ----
  providerTypes() { return this.get<M.ProviderType[]>('/providers/types'); }
  providers() { return this.get<M.ProviderAccount[]>('/providers'); }
  provider(id: string) { return this.get<M.ProviderDetail>(`/providers/${id}`); }
  createProvider(body: Record<string, unknown>) { return this.post<M.ProviderAccount>('/providers', body); }
  updateProvider(id: string, body: Record<string, unknown>) { return this.put<M.ProviderAccount | { account: M.ProviderAccount; pendingApprovalId: string; note: string }>(`/providers/${id}`, body); }
  suspendProvider(id: string, suspended: boolean, reason?: string) { return this.post<M.ProviderAccount>(`/providers/${id}/suspend`, { suspended, reason }); }
  testProvider(id: string, to?: string) { return this.post<{ health: Record<string, unknown>; send?: Record<string, unknown> | null }>(`/providers/${id}/test`, to ? { to } : {}); }
  addSender(id: string, body: Record<string, unknown>) { return this.post<M.SenderIdentity>(`/providers/${id}/senders`, body); }
  suspendSender(id: string, suspended: boolean, reason?: string) { return this.post<M.SenderIdentity>(`/providers/senders/${id}/suspend`, { suspended, reason }); }
  senderGuidance(domain: string) { return this.get<Record<string, unknown>>('/providers/sender-guidance', { domain }); }
  routingRules() { return this.get<M.RoutingRule[]>('/routing-rules'); }
  createRoutingRule(body: Record<string, unknown>) { return this.post<M.RoutingRule>('/routing-rules', body); }
  deleteRoutingRule(id: string) { return this.del<void>(`/routing-rules/${id}`); }

  // ---- rules & events ----
  rules(q: Q = {}) { return this.get<M.Rule[]>('/rules', q); }
  rule(id: string) { return this.get<M.Rule>(`/rules/${id}`); }
  createRule(body: M.RuleRequest) { return this.post<M.Rule>('/rules', body); }
  updateRule(id: string, body: M.RuleRequest) { return this.put<M.Rule>(`/rules/${id}`, body); }
  deleteRule(id: string) { return this.del<void>(`/rules/${id}`); }
  simulate(event: Record<string, unknown>, ruleId?: string | null) { return this.post<M.SimulationResult>('/rules/simulate', { event, ruleId: ruleId || null }); }
  events(q: Q = {}) { return this.get<{ total: number; items: Record<string, unknown>[] }>('/events', q); }

  // ---- campaigns ----
  campaigns(status?: string) { return this.get<M.Campaign[]>('/campaigns', { status }); }
  campaign(id: string) { return this.get<M.CampaignDraft>(`/campaigns/${id}`); }
  createCampaign(body: Record<string, unknown>) { return this.post<{ id: string }>('/campaigns', body); }
  updateCampaign(id: string, body: Record<string, unknown>) { return this.put<{ id: string }>(`/campaigns/${id}`, body); }
  campaignRecipients(id: string, status?: string, page = 1) { return this.get<M.CampaignRecipient[]>(`/campaigns/${id}/recipients`, { status, page }); }
  importCampaign(id: string, file: File) {
    const fd = new FormData(); fd.append('file', file, file.name);
    return this.http.post<Record<string, unknown>>(`${BASE}/campaigns/${id}/import`, fd);
  }
  campaignTestSend(id: string, recipient: Record<string, unknown>) { return this.post<M.SendResponse>(`/campaigns/${id}/test-send`, { recipient }); }
  campaignAction(id: string, action: 'submit' | 'approve' | 'pause' | 'resume' | 'cancel') { return this.post<{ status: string }>(`/campaigns/${id}/${action}`); }

  // ---- inbox ----
  conversations(q: Q = {}) { return this.get<M.ConversationSummary[]>('/conversations', q); }
  conversation(id: string) { return this.get<{ thread: Record<string, unknown>; messages: M.ConversationMessage[] }>(`/conversations/${id}`); }
  markRead(id: string) { return this.post<void>(`/conversations/${id}/read`); }
  reply(id: string, body: string) { return this.post<{ notificationId: string; deliveryId: string }>(`/conversations/${id}/reply`, { body }); }
  simulateInbound(body: { providerAccountId: string; from: string; body: string }) { return this.post<{ providerMessageId: string; accepted: number }>('/dev/simulate-inbound', body); }

  // ---- usage & billing ----
  quotas() { return this.get<M.Quota[]>('/quotas'); }
  saveQuota(body: Record<string, unknown>) { return this.post<M.Quota>('/quotas', body); }
  deleteQuota(id: string) { return this.del<void>(`/quotas/${id}`); }
  credits() { return this.get<M.CreditAccount[]>('/credits'); }
  topUp(body: Record<string, unknown>) { return this.post<{ id: string; balance: number }>('/credits/top-up', body); }
  billingRates() { return this.get<M.BillingRate[]>('/billing-rates'); }
  saveBillingRate(body: Record<string, unknown>) { return this.post<M.BillingRate>('/billing-rates', body); }
  costRates() { return this.get<M.CostRate[]>('/cost-rates'); }
  addCostRate(body: Record<string, unknown>) { return this.post<M.CostRate>('/cost-rates', body); }

  // ---- approvals ----
  approvals(status = 'Pending') { return this.get<M.Approval[]>('/approvals', { status }); }
  decide(id: string, approve: boolean, comment?: string) { return this.post<{ decided: boolean; status?: string }>(`/approvals/${id}/decide`, { approve, comment }); }

  // ---- recipients ----
  contacts(q: Q) { return this.get<M.Paged<M.Contact>>('/contacts', q); }
  upsertContact(body: Record<string, unknown>) { return this.put<M.Contact>('/contacts', body); }
  eraseContact(id: string) { return this.del<void>(`/contacts/${id}`); }
  preferences(companyId: string, externalId: string, kind?: string) { return this.get<M.Preferences>('/preferences', { companyId, externalId, kind }); }
  savePreferences(body: Record<string, unknown>) { return this.put<M.Preferences>('/preferences', body); }
  consents(contactId: string) { return this.get<Record<string, unknown>[]>('/consents', { contactId }); }
  recordConsent(body: Record<string, unknown>) { return this.post<{ id: string }>('/consents', body); }
  suppressions(q: Q = {}) { return this.get<M.Suppression[]>('/suppressions', q); }
  addSuppression(body: Record<string, unknown>) { return this.post<void>('/suppressions', body); }
  removeSuppression(id: string) { return this.del<void>(`/suppressions/${id}`); }
  groups(companyId?: string) { return this.get<M.Group[]>('/groups', { companyId }); }
  createGroup(body: Record<string, unknown>) { return this.post<{ id: string }>('/groups', body); }
  groupMembers(id: string) { return this.get<M.Contact[]>(`/groups/${id}/members`); }

  // ---- tenancy & admin ----
  tenants() { return this.get<M.Tenant[]>('/tenants'); }
  createTenant(body: Record<string, unknown>) { return this.post<{ id: string }>('/tenants', body); }
  updateTenant(id: string, body: Record<string, unknown>) { return this.put<{ id: string }>(`/tenants/${id}`, body); }
  companies(tenantId?: string) { return this.get<M.Company[]>('/companies', { tenantId }); }
  createCompany(body: Record<string, unknown>) { return this.post<{ id: string }>('/companies', body); }
  updateCompany(id: string, body: Record<string, unknown>) { return this.put<{ id: string }>(`/companies/${id}`, body); }
  applications(tenantId?: string) { return this.get<M.Application[]>('/applications', { tenantId }); }
  createApplication(body: Record<string, unknown>) { return this.post<{ id: string }>('/applications', body); }
  updateApplication(id: string, body: Record<string, unknown>) { return this.put<{ id: string }>(`/applications/${id}`, body); }
  linkCompany(appId: string, body: { companyId: string; enabled: boolean; defaultSenders?: Record<string, string> | null }) { return this.put<{ id: string; enabled: boolean }>(`/applications/${appId}/companies`, body); }
  credentials(appId: string) { return this.get<M.Credential[]>(`/applications/${appId}/credentials`); }
  createCredential(appId: string, body: Record<string, unknown>) { return this.post<{ id: string; clientId: string; clientSecret: string; note: string }>(`/applications/${appId}/credentials`, body); }
  revokeCredential(id: string) { return this.post<void>(`/credentials/${id}/revoke`); }
  users() { return this.get<M.AdminUser[]>('/users'); }
  saveUser(body: Record<string, unknown>) { return this.post<{ id: string }>('/users', body); }
  grantRole(id: string, role: Record<string, unknown>) { return this.post<{ id: string }>(`/users/${id}/roles`, role); }
  updateUser(id: string, body: { displayName?: string; password?: string | null; enabled?: boolean }) { return this.put<{ id: string; enabled: boolean }>(`/users/${id}`, body); }
  removeRole(id: string) { return this.del<void>(`/users/roles/${id}`); }
  suspend(kind: 'tenant' | 'company' | 'application' | 'campaign', id: string, suspended: boolean, reason?: string) {
    return this.post<{ kind: string; id: string; suspended: boolean }>(`/suspend/${kind}/${id}`, { suspended, reason });
  }
  webhooks() { return this.get<M.Webhook[]>('/webhooks'); }
  createWebhook(body: Record<string, unknown>) { return this.post<{ id: string; signingSecret: string; signatureHeader: string; note: string }>('/webhooks', body); }
  deleteWebhook(id: string) { return this.del<void>(`/webhooks/${id}`); }
  enableWebhook(id: string) { return this.post<void>(`/webhooks/${id}/enable`); }
  webhookAttempts(id: string) { return this.get<M.WebhookAttempt[]>(`/webhooks/${id}/attempts`); }
  testWebhook(id: string) { return this.post<{ eventId: string }>(`/webhooks/${id}/test`); }
  schedules() { return this.get<M.Schedule[]>('/schedules'); }
  createSchedule(body: Record<string, unknown>) { return this.post<{ id: string; nextRunAt?: string }>('/schedules', body); }
  updateSchedule(id: string, body: Record<string, unknown>) { return this.put<{ id: string; nextRunAt?: string }>(`/schedules/${id}`, body); }
  setScheduleEnabled(id: string, enabled: boolean) { return this.post<{ enabled: boolean; nextRunAt?: string }>(`/schedules/${id}/enabled/${enabled}`); }
  deleteSchedule(id: string) { return this.del<void>(`/schedules/${id}`); }
  holidayCalendars() { return this.get<M.HolidayCalendar[]>('/holiday-calendars'); }
  createHolidayCalendar(body: Record<string, unknown>) { return this.post<{ id: string }>('/holiday-calendars', body); }
  updateHolidayCalendar(id: string, body: Record<string, unknown>) { return this.put<{ id: string }>(`/holiday-calendars/${id}`, body); }
  deleteHolidayCalendar(id: string) { return this.del<void>(`/holiday-calendars/${id}`); }
}
