import { Component, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTooltipModule } from '@angular/material/tooltip';
import { COMPARISONS, EdGroup, EdNode, LIST_VALUE, NO_VALUE, newGroup, newLeaf } from './conditions';

/** Recursive AND/OR condition group editor. Mutates the given editor tree in place and emits `changed`. */
@Component({
  selector: 'app-condition-group',
  imports: [FormsModule, MatButtonModule, MatButtonToggleModule, MatFormFieldModule, MatIconModule, MatInputModule, MatSelectModule, MatTooltipModule],
  template: `
    <div class="cond-group">
      <div class="row wrap-row">
        <mat-button-toggle-group [value]="group().op" (change)="group().op = $event.value; changed.emit()" [disabled]="readonly()" aria-label="Group operator" hideSingleSelectionIndicator>
          <mat-button-toggle value="and">ALL (AND)</mat-button-toggle>
          <mat-button-toggle value="or">ANY (OR)</mat-button-toggle>
        </mat-button-toggle-group>
        <span class="small muted">{{ group().children.length ? '' : 'No conditions: always matches' }}</span>
        <span class="spacer"></span>
        @if (!readonly()) {
          <button mat-button type="button" (click)="addLeaf()"><mat-icon>add</mat-icon>Condition</button>
          @if (depth() < 9) { <button mat-button type="button" (click)="addGroup()"><mat-icon>account_tree</mat-icon>Group</button> }
          @if (depth() > 0) { <button mat-icon-button type="button" (click)="remove.emit()" matTooltip="Remove group" aria-label="Remove group"><mat-icon>delete</mat-icon></button> }
        }
      </div>
      @for (c of group().children; track c.id; let i = $index) {
        @if (c.kind === 'group') {
          <app-condition-group [group]="c" [depth]="depth() + 1" [fields]="fields()" [readonly]="readonly()" [listId]="listId()" (changed)="changed.emit()" (remove)="removeAt(i)" />
        } @else {
          <div class="cond-leaf">
            @if (i > 0) { <span class="tag">{{ group().op.toUpperCase() }}</span> }
            <mat-form-field class="fld">
              <mat-label>Field</mat-label>
              <input matInput [(ngModel)]="c.field" (ngModelChange)="changed.emit()" [attr.list]="listId()" placeholder="payload.amount" [readonly]="readonly()">
            </mat-form-field>
            <mat-form-field>
              <mat-label>Comparison</mat-label>
              <mat-select [(ngModel)]="c.comparison" (selectionChange)="changed.emit()" [disabled]="readonly()">
                @for (o of comparisons; track o.value) { <mat-option [value]="o.value">{{ o.label }}</mat-option> }
              </mat-select>
            </mat-form-field>
            @if (!noValue.has(c.comparison)) {
              <mat-form-field class="val">
                <mat-label>{{ listValue.has(c.comparison) ? 'Values (comma-separated)' : 'Value' }}</mat-label>
                <input matInput [(ngModel)]="c.value" (ngModelChange)="changed.emit()" [readonly]="readonly()" placeholder='10000, gold or "123"'>
              </mat-form-field>
            }
            @if (!readonly()) { <button mat-icon-button type="button" (click)="removeAt(i)" matTooltip="Remove condition" aria-label="Remove condition"><mat-icon>close</mat-icon></button> }
          </div>
        }
      }
    </div>`,
})
export class ConditionGroup {
  group = input.required<EdGroup>();
  depth = input(0);
  fields = input<string[]>([]);
  readonly = input(false);
  listId = input('cond-fields');
  changed = output<void>();
  remove = output<void>();
  comparisons = COMPARISONS;
  noValue = NO_VALUE;
  listValue = LIST_VALUE;

  addLeaf() { this.group().children.push(newLeaf()); this.changed.emit(); }
  addGroup() { this.group().children.push(newGroup(this.group().op === 'and' ? 'or' : 'and', [newLeaf()])); this.changed.emit(); }
  removeAt(i: number) { this.group().children.splice(i, 1); this.changed.emit(); }
}

/** Root wrapper: owns the datalist of suggested fields. */
@Component({
  selector: 'app-condition-builder',
  imports: [ConditionGroup],
  template: `
    <datalist [id]="listId">@for (f of fields(); track f) { <option [value]="f"></option> }</datalist>
    <app-condition-group [group]="root()" [fields]="fields()" [readonly]="readonly()" [listId]="listId" (changed)="changed.emit()" />
    <div class="small muted" style="margin-top:4px">{{ hint() }} Values: numbers and true/false are typed; quote to force text (<code>"007"</code>); lists are comma-separated.</div>`,
})
export class ConditionBuilder {
  root = input.required<EdGroup>();
  fields = input<string[]>([]);
  readonly = input(false);
  hint = input('Field paths are dot-separated and case-insensitive, e.g. payload.customer.tier or payload.items.0.sku.');
  changed = output<void>();
  listId = 'cond-fields-' + Math.random().toString(36).slice(2, 8);
}

export type { EdNode };
