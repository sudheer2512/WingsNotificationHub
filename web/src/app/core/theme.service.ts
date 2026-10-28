import { Injectable, signal } from '@angular/core';

export type ThemeMode = 'system' | 'light' | 'dark';
const KEY = 'wnh.theme';

/** Light/dark/system theme. The palette uses light-dark(), so pinning <html data-theme> (color-scheme) switches everything. */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  readonly mode = signal<ThemeMode>(this.read());

  set(mode: ThemeMode) {
    this.mode.set(mode);
    try { if (mode === 'system') localStorage.removeItem(KEY); else localStorage.setItem(KEY, mode); } catch { /* per-viewer convenience only */ }
    const root = document.documentElement;
    if (mode === 'system') root.removeAttribute('data-theme'); else root.setAttribute('data-theme', mode);
  }

  cycle() { this.set(this.mode() === 'system' ? 'light' : this.mode() === 'light' ? 'dark' : 'system'); }

  private read(): ThemeMode {
    try { const v = localStorage.getItem(KEY); return v === 'light' || v === 'dark' ? v : 'system'; } catch { return 'system'; }
  }
}
