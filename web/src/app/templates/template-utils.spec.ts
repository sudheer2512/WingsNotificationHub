import { extractVariables, sampleData, smsSegments } from './template-utils';
import { parseHolidayLines } from '../admin/holidays';
import { fillHours } from '../shared/widgets';

describe('extractVariables', () => {
  it('finds root variables through filters, blocks and nesting, case-insensitively de-duplicated', () => {
    const subject = 'Invoice {{InvoiceNumber}} for {{ Customer.Name | upper }}';
    const body = '{{#if Discount}}Save {{Discount | default:0}}{{else}}{{invoicenumber}}{{/if}}{{! comment }} {{#unless Paid}}due {{DueDate | date:dd-MMM-yyyy}}{{/unless}}';
    expect(extractVariables(subject, body)).toEqual(['InvoiceNumber', 'Customer', 'Discount', 'Paid', 'DueDate']);
  });

  it('ignores item-relative names inside #each blocks, like the server', () => {
    expect(extractVariables('{{#each Items}}{{@index}}. {{this.Name}} {{Sku}}{{/each}} Total {{Total | currency:INR}}')).toEqual(['Items', 'Total']);
  });

  it('handles null input and malformed tags', () => {
    expect(extractVariables(null, undefined, '{{ }} {{1abc}} {{ok}}')).toEqual(['ok']);
  });
});

describe('smsSegments', () => {
  it('counts GSM-7 single and concatenated messages', () => {
    expect(smsSegments('')).toMatchObject({ segments: 0 });
    expect(smsSegments('a'.repeat(160))).toEqual({ encoding: 'GSM-7', characters: 160, segments: 1, perSegment: 160 });
    expect(smsSegments('a'.repeat(161))).toEqual({ encoding: 'GSM-7', characters: 161, segments: 2, perSegment: 153 });
  });

  it('counts extension characters as two septets', () => {
    expect(smsSegments('€'.repeat(80)).characters).toBe(160);
    expect(smsSegments('€'.repeat(81)).segments).toBe(2);
  });

  it('switches to UCS-2 for characters outside GSM-7', () => {
    expect(smsSegments('नमस्ते')).toMatchObject({ encoding: 'UCS-2', segments: 1, perSegment: 70 });
    expect(smsSegments('₹'.repeat(71))).toMatchObject({ encoding: 'UCS-2', characters: 71, segments: 2, perSegment: 67 });
  });
});

describe('helpers', () => {
  it('builds typed sample data from declared variables', () => {
    expect(sampleData([{ name: 'Amount', sampleValue: '1250.5' }, { name: 'Name', sampleValue: 'Ravi' }, { name: 'Empty', sampleValue: null }])).toEqual({ Amount: 1250.5, Name: 'Ravi' });
  });

  it('parses holiday lines and reports bad ones', () => {
    expect(parseHolidayLines('2026-11-08 Diwali\n\n2026-12-25, Christmas\nnot a date')).toEqual({
      dates: [{ date: '2026-11-08', name: 'Diwali' }, { date: '2026-12-25', name: 'Christmas' }], errors: ['not a date'],
    });
  });

  it('fills missing hours with zeros for the trend chart', () => {
    const now = new Date('2026-10-28T10:30:00Z');
    const pts = fillHours([{ hour: '2026-10-28T09:00:00Z', total: 5, failed: 1 }], 3, now);
    expect(pts.map(p => [p.at, p.total, p.failed])).toEqual([
      ['2026-10-28T08:00:00.000Z', 0, 0], ['2026-10-28T09:00:00.000Z', 5, 1], ['2026-10-28T10:00:00.000Z', 0, 0],
    ]);
  });
});
