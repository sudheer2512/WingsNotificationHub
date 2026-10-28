import { Component, computed, input, output, signal } from '@angular/core';
import { MatIconModule } from '@angular/material/icon';
import { TreeNode } from '../core/models';
import { StateChip } from '../shared/ui';

export interface TreeCtx { tenantId?: string; companyId?: string; applicationId?: string; category?: string; }
interface Row { key: string; node: TreeNode; depth: number; ctx: TreeCtx; hasChildren: boolean; count: number; }

const EMPTY = '00000000-0000-0000-0000-000000000000';
const ICON: Record<string, string> = { tenant: 'corporate_fare', company: 'domain', application: 'apps', category: 'folder', template: 'description' };

function countTemplates(n: TreeNode): number { return n.type === 'template' ? 1 : (n.children ?? []).reduce((s, c) => s + countTemplates(c), 0); }

/** Tenant → company → application → category → template tree, flattened for rendering, with search and expand/collapse. */
@Component({
  selector: 'app-template-tree',
  imports: [MatIconModule, StateChip],
  template: `
    <ul class="tree" role="tree">
      @for (r of rows(); track r.key) {
        <li role="treeitem" [attr.aria-expanded]="r.hasChildren ? isOpen(r.key) : null" [style.padding-left.px]="r.depth * 14">
          <div class="node" [class.selected]="r.node.type === 'template' ? r.node.id === selectedId() : r.key === selectedKey()" (click)="click(r)" tabindex="0" (keyup.enter)="click(r)">
            @if (r.hasChildren) { <mat-icon>{{ isOpen(r.key) ? 'expand_more' : 'chevron_right' }}</mat-icon> } @else { <mat-icon style="visibility:hidden">chevron_right</mat-icon> }
            <mat-icon>{{ icon(r.node) }}</mat-icon>
            @if (r.node.type === 'template') {
              <span class="lbl" [title]="r.node.name"><code>{{ r.node.code }}</code> · {{ r.node.channel }}@if (r.node.locale && r.node.locale !== 'en') { · {{ r.node.locale }} }</span>
              <span class="spacer"></span><app-chip [value]="r.node.status" />
            } @else {
              <span class="lbl">{{ r.node.name }}</span><span class="spacer"></span><span class="small muted">{{ r.count }}</span>
            }
          </div>
        </li>
      } @empty { <li class="muted small" style="padding:8px">No templates match</li> }
    </ul>`,
})
export class TemplateTree {
  nodes = input<TreeNode[]>([]);
  search = input('');
  selectedId = input<string | null>(null);
  hideEmpty = input(true);
  selectTemplate = output<string>();
  selectContainer = output<TreeCtx>();
  private open = signal<Set<string>>(new Set());
  private closed = signal<Set<string>>(new Set());
  selectedKey = signal<string | null>(null);

  rows = computed<Row[]>(() => {
    const q = this.search().trim().toLowerCase();
    const out: Row[] = [];
    const matches = (n: TreeNode): boolean => n.type === 'template'
      ? !q || (n.code ?? '').toLowerCase().includes(q) || n.name.toLowerCase().includes(q) || (n.channel ?? '').includes(q)
      : (n.children ?? []).some(matches);
    const walk = (nodes: TreeNode[], depth: number, parentKey: string, ctx: TreeCtx) => {
      for (const n of nodes) {
        if ((q || this.hideEmpty()) && n.type !== 'template' && n.type !== 'tenant' && !matches(n)) continue;
        if (q && n.type === 'template' && !matches(n)) continue;
        const key = `${parentKey}/${n.type}:${n.type === 'category' ? n.name : n.id}`;
        const c: TreeCtx = { ...ctx };
        if (n.type === 'tenant') c.tenantId = n.id;
        if (n.type === 'company') c.companyId = n.id === EMPTY ? undefined : n.id;
        if (n.type === 'application') c.applicationId = n.id === EMPTY ? undefined : n.id;
        if (n.type === 'category') c.category = n.name;
        const kids = n.children ?? [];
        out.push({ key, node: n, depth, ctx: c, hasChildren: kids.length > 0, count: countTemplates(n) });
        if (kids.length && (q ? true : this.isOpen(key))) walk(kids, depth + 1, key, c);
      }
    };
    walk(this.nodes(), 0, '', {});
    return out;
  });

  /** Tenants and companies start expanded; deeper levels start collapsed unless they contain the selection. */
  isOpen(key: string): boolean {
    if (this.closed().has(key)) return false;
    if (this.open().has(key)) return true;
    const depth = key.split('/').length - 1;
    return depth <= 2 || this.containsSelected(key);
  }

  private containsSelected(key: string): boolean {
    const id = this.selectedId();
    if (!id) return false;
    const find = (nodes: TreeNode[], parentKey: string): boolean => nodes.some(n => {
      const k = `${parentKey}/${n.type}:${n.type === 'category' ? n.name : n.id}`;
      if (n.type === 'template') return n.id === id && parentKey.startsWith(key);
      return (k === key || key.startsWith(k) || k.startsWith(key)) && find(n.children ?? [], k);
    });
    return find(this.nodes(), '');
  }

  icon(n: TreeNode) { return ICON[n.type] ?? 'circle'; }

  click(r: Row) {
    if (r.node.type === 'template') { this.selectTemplate.emit(r.node.id!); return; }
    this.selectedKey.set(r.key);
    this.selectContainer.emit(r.ctx);
    const openNow = this.isOpen(r.key);
    this.open.update(s => { const n = new Set(s); if (openNow) n.delete(r.key); else n.add(r.key); return n; });
    this.closed.update(s => { const n = new Set(s); if (openNow) n.add(r.key); else n.delete(r.key); return n; });
  }
}
