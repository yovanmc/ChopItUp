import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Built output lands in the hub's wwwroot (brief D3), which is gitignored and produced by the
// csproj's npm step. `npm run dev` is a convenience only: the hub still owns /api and /hub, so the
// dev server proxies both (the hub's default port, see HubOptions.DefaultPort).
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
  },
  server: {
    proxy: {
      '/api': 'http://127.0.0.1:8790',
      '/hub': { target: 'http://127.0.0.1:8790', ws: true },
    },
  },
});
