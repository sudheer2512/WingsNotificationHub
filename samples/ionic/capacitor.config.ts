import type { CapacitorConfig } from '@capacitor/cli';

const config: CapacitorConfig = {
  appId: 'com.wings.erp.sample',
  appName: 'Wings ERP',
  webDir: 'dist/browser',
  plugins: {
    PushNotifications: {
      // How notifications are shown while the app is in the foreground (iOS).
      presentationOptions: ['badge', 'sound', 'alert'],
    },
  },
};

export default config;
