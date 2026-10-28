import { inject } from '@angular/core';
import { CanActivateFn, Router, Routes } from '@angular/router';
import { AuthService, authGuard, permissionGuard } from './core/auth.service';
import { NAV, permsFor } from './nav';
import { Shell } from './shell';

const T = ' · Wings Notification Hub';

/** Sends the user to the first screen they are allowed to see. */
const homeRedirect: CanActivateFn = () => {
  const auth = inject(AuthService);
  for (const s of NAV) for (const i of s.items) if (auth.can(...i.perms)) return inject(Router).createUrlTree([i.path]);
  return inject(Router).createUrlTree(['/no-access']);
};

const guarded = (path: string) => [permissionGuard(...permsFor('/' + path))];

export const routes: Routes = [
  { path: 'sign-in', loadComponent: () => import('./pages/sign-in').then(m => m.SignIn), title: 'Sign in' + T },
  {
    path: '', component: Shell, canActivate: [authGuard], canActivateChild: [authGuard],
    children: [
      { path: '', pathMatch: 'full', canActivate: [homeRedirect], children: [] },
      { path: 'no-access', loadComponent: () => import('./pages/no-access').then(m => m.NoAccess), title: 'No access' + T },
      { path: 'dashboard', canActivate: guarded('dashboard'), loadComponent: () => import('./pages/dashboard').then(m => m.DashboardPage), title: 'Dashboard' + T },
      { path: 'explorer', canActivate: guarded('explorer'), loadComponent: () => import('./pages/explorer').then(m => m.Explorer), title: 'Notification explorer' + T },
      { path: 'templates', canActivate: guarded('templates'), loadComponent: () => import('./templates/template-manager').then(m => m.TemplateManager), title: 'Templates' + T },
      { path: 'rules', canActivate: guarded('rules'), loadComponent: () => import('./pages/rules').then(m => m.RuleDesigner), title: 'Rule designer' + T },
      { path: 'campaigns', canActivate: guarded('campaigns'), loadComponent: () => import('./pages/campaigns').then(m => m.Campaigns), title: 'Campaigns' + T },
      { path: 'inbox', canActivate: guarded('inbox'), loadComponent: () => import('./pages/inbox').then(m => m.Inbox), title: 'Inbox' + T },
      { path: 'approvals', canActivate: guarded('approvals'), loadComponent: () => import('./pages/approvals').then(m => m.Approvals), title: 'Approvals' + T },
      { path: 'recipients', canActivate: guarded('recipients'), loadComponent: () => import('./pages/recipients').then(m => m.Recipients), title: 'Recipients' + T },
      { path: 'providers', canActivate: guarded('providers'), loadComponent: () => import('./pages/providers').then(m => m.Providers), title: 'Providers' + T },
      { path: 'monitoring', canActivate: guarded('monitoring'), loadComponent: () => import('./pages/monitoring').then(m => m.MonitoringPage), title: 'System monitoring' + T },
      { path: 'usage', canActivate: guarded('usage'), loadComponent: () => import('./pages/usage').then(m => m.Usage), title: 'Usage & billing' + T },
      { path: 'reports', canActivate: guarded('reports'), loadComponent: () => import('./pages/reports').then(m => m.Reports), title: 'Reports' + T },
      { path: 'admin/tenancy', canActivate: guarded('admin/tenancy'), loadComponent: () => import('./admin/tenancy').then(m => m.Tenancy), title: 'Tenants & companies' + T },
      { path: 'admin/applications', canActivate: guarded('admin/applications'), loadComponent: () => import('./admin/applications').then(m => m.Applications), title: 'Applications' + T },
      { path: 'admin/users', canActivate: guarded('admin/users'), loadComponent: () => import('./admin/users').then(m => m.Users), title: 'Users & roles' + T },
      { path: 'admin/webhooks', canActivate: guarded('admin/webhooks'), loadComponent: () => import('./admin/webhooks').then(m => m.Webhooks), title: 'Webhooks' + T },
      { path: 'admin/schedules', canActivate: guarded('admin/schedules'), loadComponent: () => import('./admin/schedules').then(m => m.Schedules), title: 'Schedules' + T },
      { path: 'admin/holidays', canActivate: guarded('admin/holidays'), loadComponent: () => import('./admin/holidays').then(m => m.Holidays), title: 'Holiday calendars' + T },
      { path: 'admin/suspension', canActivate: guarded('admin/suspension'), loadComponent: () => import('./admin/suspension').then(m => m.Suspension), title: 'Emergency suspension' + T },
      { path: 'admin/audit', canActivate: guarded('admin/audit'), loadComponent: () => import('./admin/audit').then(m => m.Audit), title: 'Audit log' + T },
    ],
  },
  { path: '**', redirectTo: '' },
];
