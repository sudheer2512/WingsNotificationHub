import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { LookupsService } from '../core/lookups.service';
import { Monitoring } from '../core/models';
import { MonitoringPage } from './monitoring';

const shared: Monitoring = { transport: 'database', retries: 0, awaitingOperator: 0, providerErrors24h: [], circuits: [], alerts: [] };
const letter = (id: string, canRequeue: boolean) => ({ id, queue: 'wnh.dispatch', type: 'delivery.dispatch', dequeueCount: 10, deadLetteredAt: '2026-11-07T00:00:00', enqueuedAt: '2026-11-07T00:00:00', canRequeue });

function render(data: Monitoring): HTMLElement {
  TestBed.configureTestingModule({
    imports: [MonitoringPage],
    providers: [
      provideRouter([]),
      { provide: ApiService, useValue: { monitoring: () => of(data) } },
      { provide: AuthService, useValue: { can: () => false } },
      { provide: LookupsService, useValue: { providerName: (id: string) => id, tenantName: (id: string) => id } },
    ],
  });
  const fixture = TestBed.createComponent(MonitoringPage);
  fixture.detectChanges();
  fixture.detectChanges();
  return fixture.nativeElement;
}

const cards = (page: HTMLElement) => Array.from(page.querySelectorAll('.card .l')).map(c => c.textContent?.trim());

describe('MonitoringPage', () => {
  it('shows platform administrators the shared infrastructure', () => {
    const page = render({ ...shared, queues: { 'wnh.dispatch': { ready: 1, inFlight: 0, deadLettered: 0 } }, outboxBacklog: 0, workers: [], deadLetters: [] });
    expect(page.querySelector('h2')?.textContent).toBe('Queues');
    expect(page.textContent).toContain('Workers');
    expect(cards(page)).toContain('Queued messages');
    expect(cards(page)).toContain('Unresponsive workers');
    expect(page.textContent).not.toContain('only platform administrators');
  });

  it('explains the missing panels to a tenant-wide role', () => {
    const page = render({ ...shared, deadLetters: [] });
    expect(cards(page)).not.toContain('Queued messages');
    expect(cards(page)).not.toContain('Unresponsive workers');
    expect(cards(page)).toContain('Dead letters');
    expect(page.textContent).toContain('only platform administrators');
    expect(page.textContent).toContain('No dead letters');
    expect(page.textContent).not.toContain('Dead letters need access');
  });

  it('does not tell a company role there are no dead letters when it cannot see them', () => {
    const page = render(shared);
    expect(cards(page)).not.toContain('Dead letters');
    expect(page.textContent).not.toContain('No dead letters');
    expect(page.textContent).toContain('Dead letters need access to a whole tenant.');
  });

  it('offers Requeue only on the rows the caller can requeue', () => {
    const page = render({ ...shared, deadLetters: [letter('a', true), letter('b', false)] });
    const rows = Array.from(page.querySelectorAll('tbody tr')).filter(r => r.textContent?.includes('delivery.dispatch'));
    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain('Requeue');
    expect(rows[1].textContent).not.toContain('Requeue');
  });
});
