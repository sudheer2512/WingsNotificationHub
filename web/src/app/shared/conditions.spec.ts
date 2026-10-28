import { ConditionNode } from '../core/models';
import { describe as describeTree, formatValue, fromApi, newGroup, newLeaf, parseValue, payloadPaths, toApi, validateTree } from './conditions';

describe('condition builder model', () => {
  const seeded: ConditionNode = {
    op: 'and', isGroup: true, children: [
      { field: 'payload.DaysOverdue', comparison: 'gte', value: 7, isGroup: false },
      { op: 'or', isGroup: true, children: [
        { field: 'payload.AmountDue', comparison: 'gt', value: 10000 },
        { field: 'payload.customer.tier', comparison: 'eq', value: 'gold' },
      ] },
    ],
  };

  it('round-trips a nested API tree without changing values or types', () => {
    expect(toApi(fromApi(seeded))).toEqual({
      op: 'and', children: [
        { field: 'payload.DaysOverdue', comparison: 'gte', value: 7 },
        { op: 'or', children: [{ field: 'payload.AmountDue', comparison: 'gt', value: 10000 }, { field: 'payload.customer.tier', comparison: 'eq', value: 'gold' }] },
      ],
    });
  });

  it('wraps a single leaf in an AND root and unwraps it again', () => {
    const ed = fromApi({ field: 'payload.Quantity', comparison: 'lt', value: 50 });
    expect(ed.kind).toBe('group');
    expect(ed.op).toBe('and');
    expect(ed.children.length).toBe(1);
    expect(toApi(ed)).toEqual({ field: 'payload.Quantity', comparison: 'lt', value: 50 });
  });

  it('returns null (always matches) for empty trees and prunes blank leaves and empty groups', () => {
    expect(toApi(fromApi(null))).toBeNull();
    const g = newGroup('or', [newLeaf(''), newGroup('and', [newLeaf('  ')]), newLeaf('payload.x', 'exists')]);
    expect(toApi(g)).toEqual({ field: 'payload.x', comparison: 'exists' });
  });

  it('types values: numbers, booleans, null, quoted strings and lists', () => {
    expect(parseValue('10000', 'gt')).toBe(10000);
    expect(parseValue('-2.5', 'eq')).toBe(-2.5);
    expect(parseValue('true', 'eq')).toBe(true);
    expect(parseValue('null', 'eq')).toBeNull();
    expect(parseValue('"007"', 'eq')).toBe('007');
    expect(parseValue('gold', 'eq')).toBe('gold');
    expect(parseValue('anything', 'exists')).toBeUndefined();
    expect(parseValue('gold, silver , 3, "a,b"', 'in')).toEqual(['gold', 'silver', 3, 'a,b']);
    expect(parseValue('', 'notin')).toEqual([]);
  });

  it('formats values so that strings keep their type on the way back', () => {
    for (const v of ['007', 'true', 'gold', 'a,b', 42, false, null, ['x', 1, '2']]) {
      const cmp = Array.isArray(v) ? 'in' : 'eq';
      expect(parseValue(formatValue(v), cmp)).toEqual(v);
    }
  });

  it('validates like the server and flags list comparisons without values', () => {
    const g = newGroup('and', [newLeaf('payload.tier', 'in', ''), newLeaf('payload.a', 'bogus', '1'), newLeaf('', 'eq', 'orphan value')]);
    const errors = validateTree(g);
    expect(errors.some(e => e.includes('needs at least one value'))).toBe(true);
    expect(errors.some(e => e.includes("Unknown comparison 'bogus'"))).toBe(true);
    expect(errors.some(e => e.includes('no field'))).toBe(true);
    expect(validateTree(fromApi(seeded))).toEqual([]);
  });

  it('rejects trees nested deeper than the evaluator allows', () => {
    let g = newGroup('and', [newLeaf('payload.x', 'exists')]);
    for (let i = 0; i < 11; i++) g = newGroup('and', [g]);
    expect(validateTree(g)).toContain('Conditions are nested too deeply (max 10).');
  });

  it('describes trees in readable form', () => {
    expect(describeTree(seeded)).toBe('payload.DaysOverdue ≥ 7 AND (payload.AmountDue > 10000 OR payload.customer.tier = gold)');
    expect(describeTree(null)).toBe('Always (no conditions)');
    expect(describeTree({ field: 'payload.tags', comparison: 'in', value: ['a', 'b'] })).toBe('payload.tags in [a, b]');
  });

  it('suggests dotted payload paths from a sample', () => {
    expect(payloadPaths({ a: 1, customer: { tier: 'gold', address: { city: 'X' } }, items: [1] }))
      .toEqual(['payload.a', 'payload.customer', 'payload.customer.tier', 'payload.customer.address', 'payload.customer.address.city', 'payload.items']);
  });
});
