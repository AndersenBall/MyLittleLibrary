import { useCallback, useEffect, useState } from 'react';
import { getHealth } from './api/generated/sdk.gen';

const statusText = {
  checking: 'Checking API…',
  connected: 'API connected',
  unavailable: 'API unavailable',
};

export default function App() {
  const [status, setStatus] = useState<keyof typeof statusText>('checking');

  const checkHealth = useCallback(async () => {
    setStatus('checking');
    try {
      const { response } = await getHealth({
        parseAs: 'text',
        cache: 'no-store',
        signal: AbortSignal.timeout(10_000),
      });
      setStatus(response?.ok ? 'connected' : 'unavailable');
    } catch {
      setStatus('unavailable');
    }
  }, []);

  useEffect(() => {
    void checkHealth();
  }, [checkHealth]);

  return (
    <main>
      <h1>MyLittleLibrary</h1>
      <p role="status">{statusText[status]}</p>
      <button type="button" disabled={status === 'checking'} onClick={checkHealth}>
        Check again
      </button>
    </main>
  );
}
