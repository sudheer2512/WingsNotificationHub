import { Component, ElementRef, ViewChild, effect, inject, input, output, untracked } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { MatTooltipModule } from '@angular/material/tooltip';
import { MatDialog } from '@angular/material/dialog';
import { promptDialog } from '../shared/widgets';

/** Minimal contenteditable rich-text editor (no third-party editor). Emits HTML on every edit. */
@Component({
  selector: 'app-rich-text',
  imports: [MatButtonModule, MatIconModule, MatTooltipModule],
  template: `
    <div class="toolbar-row" role="toolbar" aria-label="Formatting">
      @for (b of buttons; track b.cmd + (b.arg ?? '')) {
        <button mat-icon-button type="button" (mousedown)="$event.preventDefault()" (click)="exec(b.cmd, b.arg)" [matTooltip]="b.label" [attr.aria-label]="b.label" [disabled]="readonly()">
          <mat-icon>{{ b.icon }}</mat-icon>
        </button>
      }
      <button mat-icon-button type="button" (mousedown)="$event.preventDefault()" (click)="link()" matTooltip="Link" aria-label="Link" [disabled]="readonly()"><mat-icon>link</mat-icon></button>
    </div>
    <div #ed class="rte" [attr.contenteditable]="!readonly()" (input)="emit()" (blur)="emit()" role="textbox" aria-multiline="true" aria-label="Rich text body"></div>`,
})
export class RichText {
  value = input('');
  readonly = input(false);
  changed = output<string>();
  @ViewChild('ed', { static: true }) ed!: ElementRef<HTMLDivElement>;
  private last = '';
  private dialog = inject(MatDialog);

  buttons = [
    { cmd: 'bold', icon: 'format_bold', label: 'Bold' }, { cmd: 'italic', icon: 'format_italic', label: 'Italic' },
    { cmd: 'underline', icon: 'format_underlined', label: 'Underline' }, { cmd: 'formatBlock', arg: 'h2', icon: 'title', label: 'Heading' },
    { cmd: 'formatBlock', arg: 'p', icon: 'notes', label: 'Paragraph' }, { cmd: 'insertUnorderedList', icon: 'format_list_bulleted', label: 'Bulleted list' },
    { cmd: 'insertOrderedList', icon: 'format_list_numbered', label: 'Numbered list' }, { cmd: 'removeFormat', icon: 'format_clear', label: 'Clear formatting' },
  ];

  constructor() {
    effect(() => {
      const v = this.value() ?? '';
      untracked(() => { if (v !== this.last) { this.ed.nativeElement.innerHTML = v; this.last = v; } });
    });
  }

  exec(cmd: string, arg?: string) {
    this.ed.nativeElement.focus();
    document.execCommand(cmd, false, arg);
    this.emit();
  }

  link() {
    const sel = window.getSelection();
    const range = sel && sel.rangeCount ? sel.getRangeAt(0).cloneRange() : null;
    promptDialog(this.dialog, 'Insert link', 'Link URL', 'Use https://… or a variable such as {{PaymentLink | url}}.', 'https://').subscribe(url => {
      if (!url) return;
      if (range) { const s = window.getSelection(); s?.removeAllRanges(); s?.addRange(range); }
      this.exec('createLink', url);
    });
  }

  /** Inserts text at the caret (used by the variable chips). */
  insert(text: string) {
    const el = this.ed.nativeElement;
    el.focus();
    const sel = window.getSelection();
    if (!sel || !sel.rangeCount || !el.contains(sel.anchorNode)) {
      const r = document.createRange(); r.selectNodeContents(el); r.collapse(false); sel?.removeAllRanges(); sel?.addRange(r);
    }
    document.execCommand('insertText', false, text);
    this.emit();
  }

  emit() {
    const html = this.ed.nativeElement.innerHTML;
    if (html === this.last) return;
    this.last = html;
    this.changed.emit(html);
  }
}
