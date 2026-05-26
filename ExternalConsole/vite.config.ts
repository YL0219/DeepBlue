import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// External console — runs separately from the Aleph backend.
// Backend URL is read at build/dev time from VITE_BACKEND_URL,
// defaulting to http://localhost:5000 inside src/App.tsx.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: false,
  },
});
