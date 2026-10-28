import { Pipe, type PipeTransform } from '@angular/core';
import { relativeTime } from './utils';

/** `{{ message.createdAt | wingsRelativeTime }}` → "5 minutes ago". */
@Pipe({ name: 'wingsRelativeTime' })
export class WingsRelativeTimePipe implements PipeTransform {
  transform(iso: string | null | undefined, locale?: string): string {
    return relativeTime(iso, Date.now(), locale);
  }
}
