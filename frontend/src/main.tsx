import { lazy, Suspense } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import { createLibraryApi } from './api';
import './style.css';

const HostedApp = lazy(() => import('./HostedApp'));

createRoot(document.getElementById('root')!).render(
  import.meta.env.DEV && import.meta.env.VITE_LOCAL_DEV_AUTH === 'true'
    ? <App api={createLibraryApi()} />
    : <Suspense fallback={<p role="status">Loading sign-in…</p>}><HostedApp /></Suspense>,
);
