import { Auth0Provider, useAuth0 } from '@auth0/auth0-react';
import { useMemo, useState } from 'react';
import App from './App';
import { createLibraryApi } from './api';

function SignedInApp() {
  const { isLoading, isAuthenticated, error, user, loginWithRedirect, logout, getAccessTokenSilently } = useAuth0();
  const [actionFailed, setActionFailed] = useState(false);
  const api = useMemo(() => createLibraryApi(async () => {
    const token = await getAccessTokenSilently();
    if (!token) throw new Error('No API access token is available.');
    return token;
  }), [getAccessTokenSilently]);
  if (isLoading) return <p role="status">Checking your session…</p>;

  const signOut = () => {
    setActionFailed(false);
    void logout({ logoutParams: { returnTo: window.location.origin } }).catch(() => setActionFailed(true));
  };

  if (isAuthenticated) return <>
    {actionFailed && <p role="alert">Couldn’t sign out. Try again.</p>}
    <App key={user?.sub} api={api} signOut={signOut} />
  </>;

  return <main className="page">
    <h1>My Little Library</h1>
    {(error || actionFailed) && <p role="alert">Couldn’t sign in. Try again.</p>}
    <button type="button" className="button button-primary" onClick={() => {
      setActionFailed(false);
      void loginWithRedirect({ appState: { returnTo: window.location.pathname } }).catch(() => setActionFailed(true));
    }}>Sign in</button>
  </main>;
}

export default function HostedApp() {
  const domain = import.meta.env.VITE_AUTH0_DOMAIN?.trim();
  const clientId = import.meta.env.VITE_AUTH0_CLIENT_ID?.trim();
  const audience = import.meta.env.VITE_AUTH0_AUDIENCE?.trim();
  if (!domain || !clientId || !audience) return <p role="alert">Hosted sign-in is not configured.</p>;

  return <Auth0Provider domain={domain} clientId={clientId}
    authorizationParams={{ redirect_uri: window.location.origin, audience }}
    onRedirectCallback={(state) => window.history.replaceState({}, '', state?.returnTo === '/add' ? '/add' : '/')}>
    <SignedInApp />
  </Auth0Provider>;
}