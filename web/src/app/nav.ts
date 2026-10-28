export interface NavItem { path: string; label: string; icon: string; perms: string[]; }
export interface NavSection { title: string | null; items: NavItem[]; }

/** Navigation entries and the permissions (from GET /auth/me) that reveal them. Routes use the same lists as guards. */
export const NAV: NavSection[] = [
  {
    title: null, items: [
      { path: '/dashboard', label: 'Dashboard', icon: 'space_dashboard', perms: ['ViewDashboard'] },
      { path: '/explorer', label: 'Notification explorer', icon: 'manage_search', perms: ['ViewNotifications'] },
      { path: '/templates', label: 'Templates', icon: 'description', perms: ['ViewTemplates'] },
      { path: '/rules', label: 'Rule designer', icon: 'rule', perms: ['ViewRules'] },
      { path: '/campaigns', label: 'Campaigns', icon: 'campaign', perms: ['ViewCampaigns'] },
      { path: '/inbox', label: 'Inbox', icon: 'forum', perms: ['ViewInbox'] },
      { path: '/approvals', label: 'Approvals', icon: 'fact_check', perms: ['ApproveTemplates', 'ApproveCampaigns', 'ApproveProviderChanges'] },
      { path: '/recipients', label: 'Recipients', icon: 'contacts', perms: ['ViewContacts'] },
    ],
  },
  {
    title: 'Operations', items: [
      { path: '/providers', label: 'Providers', icon: 'hub', perms: ['ViewProviders'] },
      { path: '/monitoring', label: 'System monitoring', icon: 'monitor_heart', perms: ['ViewMonitoring'] },
      { path: '/usage', label: 'Usage & billing', icon: 'payments', perms: ['ViewUsage'] },
      { path: '/reports', label: 'Reports', icon: 'summarize', perms: ['ViewUsage'] },
    ],
  },
  {
    title: 'Administration', items: [
      { path: '/admin/tenancy', label: 'Tenants & companies', icon: 'domain', perms: ['ViewTenancy'] },
      { path: '/admin/applications', label: 'Applications', icon: 'apps', perms: ['ViewTenancy'] },
      { path: '/admin/users', label: 'Users & roles', icon: 'group', perms: ['ManageUsers'] },
      { path: '/admin/webhooks', label: 'Webhooks', icon: 'webhook', perms: ['ManageWebhooks'] },
      { path: '/admin/schedules', label: 'Schedules', icon: 'schedule', perms: ['ViewNotifications'] },
      { path: '/admin/holidays', label: 'Holiday calendars', icon: 'event', perms: ['ViewTenancy'] },
      { path: '/admin/suspension', label: 'Emergency suspension', icon: 'block', perms: ['EmergencySuspend'] },
      { path: '/admin/audit', label: 'Audit log', icon: 'receipt_long', perms: ['ViewAudit'] },
    ],
  },
];

export function permsFor(path: string): string[] {
  for (const s of NAV) for (const i of s.items) if (i.path === path) return i.perms;
  return [];
}
