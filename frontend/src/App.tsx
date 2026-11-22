import { useCallback, useEffect, useRef, useState } from 'react';
import type { FormEvent, ReactNode } from 'react';
import {
  addLibraryBook,
  apiMessage,
  getLibraryBooks,
  searchCatalog,
  type CatalogEdition,
  type GalleryBook,
} from './api';

type Route = 'library' | 'add';

function routeFromLocation(): Route {
  return window.location.pathname === '/add' ? 'add' : 'library';
}

function Cover({ src, title, authors, className = '' }: { src?: string | null; title: string; authors: string[]; className?: string }) {
  const [broken, setBroken] = useState(!src);
  useEffect(() => setBroken(!src), [src]);
  return (
    <div className={`cover ${className}`}>
      {!broken && <img src={src!} alt={`Cover of ${title}`} onError={() => setBroken(true)} />}
      {broken && <div className="cover-fallback" aria-label={`No cover available for ${title}`}><span className="fallback-mark">ML</span><strong>{title}</strong><small>{authors.join(', ') || 'Unknown author'}</small></div>}
    </div>
  );
}

function Shell({ route, navigate, children }: { route: Route; navigate: (route: Route) => void; children: ReactNode }) {
  return (
    <div className="app-shell">
      <header className="site-header">
        <a className="brand" href="/" onClick={(event) => { event.preventDefault(); navigate('library'); }} aria-label="My Little Library home">
          <span className="brand-mark">▰</span><span>My Little Library</span>
        </a>
        <nav aria-label="Primary navigation">
          <a className={route === 'library' ? 'active' : ''} href="/" onClick={(event) => { event.preventDefault(); navigate('library'); }}>My Library</a>
          <a className={route === 'add' ? 'active' : ''} href="/add" onClick={(event) => { event.preventDefault(); navigate('add'); }}>Add Books</a>
        </nav>
      </header>
      <main>{children}</main>
      <footer><span>Build a shelf of stories worth keeping.</span><span>My Little Library</span></footer>
    </div>
  );
}

function Library({ navigate }: { navigate: (route: Route) => void }) {
  const [books, setBooks] = useState<GalleryBook[]>([]);
  const [state, setState] = useState<'loading' | 'ready' | 'error'>('loading');

  const load = useCallback(async (signal?: AbortSignal) => {
    setState('loading');
    try { setBooks(await getLibraryBooks(signal)); setState('ready'); }
    catch (error) { if ((error as Error).name !== 'AbortError') setState('error'); }
  }, []);
  useEffect(() => { const controller = new AbortController(); void load(controller.signal); return () => controller.abort(); }, [load]);

  return <section className="page library-page">
    <div className="page-heading"><div><p className="eyebrow">Your collection</p><h1>My Library</h1><p className="lede">A home for the books you want to remember.</p></div><button className="button button-primary" type="button" onClick={() => navigate('add')}>＋ Add Books</button></div>
    {state === 'loading' && <div className="status-panel" role="status"><span className="spinner" />Loading your library…</div>}
    {state === 'error' && <div className="status-panel error" role="alert"><strong>We couldn’t load your library.</strong><span>Check your connection and try again.</span><button className="button button-secondary" type="button" onClick={() => void load()}>Retry</button></div>}
    {state === 'ready' && books.length === 0 && <div className="empty-state"><div className="empty-art"><span>✦</span><span>▱</span><span>✧</span></div><h2>Your shelf is waiting</h2><p>Search the catalog to find your next favorite book, then add it here.</p><button className="button button-primary" type="button" onClick={() => navigate('add')}>Find a book</button></div>}
    {state === 'ready' && books.length > 0 && <div className="book-grid">{books.map((book) => <article className="book-card" key={book.editionId}><Cover src={book.coverUrl} title={book.title} authors={book.authors} /><div className="book-info"><h2>{book.title}</h2><p>{book.authors.join(', ') || 'Unknown author'}</p></div></article>)}</div>}
  </section>;
}

function AddBooks({ navigate }: { navigate: (route: Route) => void }) {
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<CatalogEdition[]>([]);
  const [searchState, setSearchState] = useState<'idle' | 'loading' | 'ready' | 'error'>('idle');
  const [message, setMessage] = useState('');
  const searchRequest = useRef<AbortController | null>(null);
  const [adding, setAdding] = useState<string | null>(null);
  const [added, setAdded] = useState<Set<string>>(new Set());
  const [pendingCovers, setPendingCovers] = useState<Record<string, File | undefined>>({});
  useEffect(() => () => searchRequest.current?.abort(), []);

  async function submit(event?: FormEvent) {
    event?.preventDefault();
    const value = query.trim();
    if (!value) { setMessage('Enter a title, author, or ISBN to search.'); return; }
    searchRequest.current?.abort();
    const request = new AbortController();
    searchRequest.current = request;
    setSearchState('loading'); setMessage('');
    try { const found = await searchCatalog(value, request.signal); if (!request.signal.aborted) { setResults(found); setSearchState('ready'); } }
    catch (error) { if (!request.signal.aborted) { setSearchState('error'); setMessage(apiMessage(error, 'Catalog search is unavailable right now. Please try again.')); } }
  }
  async function add(result: CatalogEdition, file?: File) {
    setAdding(result.editionId); setMessage('');
    try { await addLibraryBook(result.editionId, file); setAdded((current) => new Set(current).add(result.editionId)); }
    catch (error) { setMessage(apiMessage(error, `Couldn’t add “${result.title}”. Please try again.`)); }
    finally { setAdding(null); }
  }
  return <section className="page add-page">
    <div className="page-heading compact"><div><p className="eyebrow">Grow your collection</p><h1>Add Books</h1><p className="lede">Find an edition by title, author, or ISBN.</p></div><button className="button button-secondary" type="button" onClick={() => navigate('library')}>View Library →</button></div>
    <form className="search-form" onSubmit={submit}><label htmlFor="catalog-search">Search the catalog</label><div className="search-row"><input id="catalog-search" value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Try “The Hobbit” or an ISBN" maxLength={200} autoComplete="off" /><button className="button button-primary" type="submit">Search</button></div></form>
    {searchState === 'loading' && <p role="status">Searching the catalog…</p>}
    {message && searchState !== 'error' && <p className="form-message" role="alert">{message}</p>}
    {searchState === 'idle' && <div className="search-hint"><span className="hint-icon">⌕</span><h2>What will you read next?</h2><p>Search millions of books to find the right edition for your shelf.</p></div>}
    {searchState === 'ready' && results.length === 0 && <div className="status-panel"><strong>No books found.</strong><span>Try a different title, author, or ISBN.</span></div>}
    {searchState === 'error' && <div className="status-panel error" role="alert"><strong>Search failed.</strong><span>{message}</span><button className="button button-secondary" type="button" onClick={() => void submit()}>Retry</button></div>}
    {searchState === 'ready' && results.length > 0 && <div className="results-list">
      {results.map((result) => {
        const id = result.editionId;
        const isAdded = result.alreadyAdded || added.has(id);
        const cover = pendingCovers[id];
        const isbn = result.isbn13?.[0] ?? result.isbn10?.[0];
        return <article className="result-card" key={id}>
          <Cover src={result.coverUrl} title={result.title} authors={result.authors} className="result-cover" />
          <div className="result-details">
            <h2>{result.title}</h2>
            <p className="authors">{result.authors.join(', ') || 'Unknown author'}</p>
            <p className="edition-details">
              {[result.publicationDate, result.publishers[0], isbn && `ISBN ${isbn}`].filter(Boolean).join(' · ')}{' '}
              <a href={result.editionUrl} target="_blank" rel="noreferrer">Open Library edition</a>
            </p>
            <div className="result-actions">
              <button className={`button ${isAdded ? 'button-added' : 'button-primary'}`} type="button" disabled={isAdded || adding !== null} onClick={() => void add(result, cover)}>
                {adding === id ? 'Adding…' : isAdded ? '✓ Added' : 'Add to Library'}
              </button>
              {!isAdded && <label className="file-label">Use my own cover<input type="file" accept="image/jpeg,image/png,image/webp" disabled={adding !== null} onChange={(event) => {
                const file = event.target.files?.[0];
                setPendingCovers((current) => ({ ...current, [id]: file }));
              }} /></label>}
            </div>
            {!isAdded && cover && <p className="edition-details">{cover.name} <button type="button" disabled={adding !== null} onClick={() => setPendingCovers((current) => ({ ...current, [id]: undefined }))}>Remove cover</button></p>}
          </div>
        </article>;
      })}
    </div>}
  </section>;
}

export default function App() {
  const [route, setRoute] = useState<Route>(routeFromLocation);
  useEffect(() => { const onPop = () => setRoute(routeFromLocation()); window.addEventListener('popstate', onPop); return () => window.removeEventListener('popstate', onPop); }, []);
  const navigate = useCallback((next: Route) => { const path = next === 'add' ? '/add' : '/'; if (window.location.pathname !== path) window.history.pushState({}, '', path); setRoute(next); window.scrollTo({ top: 0, behavior: 'smooth' }); }, []);
  return <Shell route={route} navigate={navigate}>{route === 'add' ? <AddBooks navigate={navigate} /> : <Library navigate={navigate} />}</Shell>;
}
