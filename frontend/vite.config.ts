import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [react()],
  server: {
    host: '0.0.0.0',
    port: 5173,
    strictPort: true,
    allowedHosts: ['realtornow-jeb.tail69be98.ts.net'],
    proxy: {
      '/health': 'http://api:8080',
      '/api': 'http://api:8080',
    },
  },
});
