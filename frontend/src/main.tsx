import { createRoot } from 'react-dom/client';
import App from './App';
import { client } from './api/generated/client.gen';
import './style.css';

client.setConfig({ baseUrl: window.location.origin });

createRoot(document.getElementById('root')!).render(<App />);
