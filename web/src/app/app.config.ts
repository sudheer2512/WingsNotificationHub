import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, inject, provideAppInitializer, provideBrowserGlobalErrorListeners, provideZoneChangeDetection } from '@angular/core';
import { MAT_FORM_FIELD_DEFAULT_OPTIONS } from '@angular/material/form-field';
import { MAT_TABS_CONFIG } from '@angular/material/tabs';
import { MAT_DIALOG_DEFAULT_OPTIONS, MatDialogConfig } from '@angular/material/dialog';
import { provideRouter, withComponentInputBinding, withInMemoryScrolling } from '@angular/router';
import { routes } from './app.routes';
import { AuthService, authInterceptor } from './core/auth.service';
import { errorInterceptor } from './core/errors';
import { RealtimeService } from './core/realtime.service';
import { LookupsService } from './core/lookups.service';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(routes, withComponentInputBinding(), withInMemoryScrolling({ scrollPositionRestoration: 'top' })),
    provideHttpClient(withInterceptors([authInterceptor, errorInterceptor])),
    provideAppInitializer(async () => {
      const auth = inject(AuthService);
      const lookups = inject(LookupsService);
      inject(RealtimeService); // starts/stops the SignalR connection with the sign-in state
      await auth.init();
      if (auth.signedIn()) await lookups.load();
    }),
    { provide: MAT_FORM_FIELD_DEFAULT_OPTIONS, useValue: { appearance: 'outline', subscriptSizing: 'dynamic' } },
    // No slide animation: outlined form fields inside tabs measure their label notch correctly and tabs switch instantly.
    { provide: MAT_TABS_CONFIG, useValue: { animationDuration: '0ms' } },
    // The scale-in animation makes outlined fields measure their label notch while scaled down (clipped labels).
    { provide: MAT_DIALOG_DEFAULT_OPTIONS, useValue: { ...new MatDialogConfig(), enterAnimationDuration: '0ms', exitAnimationDuration: '100ms' } },
  ],
};
