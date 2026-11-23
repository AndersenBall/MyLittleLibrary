import { useCallback, useEffect, useRef, useState } from 'react';
import type { ChangeEvent, FormEvent, ReactNode } from 'react';
import {
  apiMessage,
  type CatalogEdition,
  type GalleryBook,
  type ScannedBook,
  type LibraryApi,
} from './api';

type Route = 'library' | 'add' | 'scan';

function routeFromLocation(): Route {
  if (window.location.pathname === '/add') return 'add';
  if (window.location.pathname === '/scan') return 'scan';
  return 'library';
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

function GalleryCover({ book, api }: { book: GalleryBook; api: LibraryApi }) {
  const { editionId, title, authors, coverUrl } = book;
  const isPrivate = coverUrl === `/api/library/books/${editionId}/cover`;
  const [imageUrl, setImageUrl] = useState<string>();

  useEffect(() => {
    if (!isPrivate) return;
    const controller = new AbortController();
    let objectUrl: string | undefined;
    setImageUrl(undefined);
    void api.getLibraryCover(editionId, controller.signal).then((image) => {
      if (controller.signal.aborted) return;
      objectUrl = URL.createObjectURL(image);
      setImageUrl(objectUrl);
    }).catch(() => { /* Use the existing cover fallback. */ });
    return () => {
      controller.abort();
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [api, editionId, isPrivate]);

  return <Cover src={isPrivate ? imageUrl : coverUrl} title={title} authors={authors} />;
}

function Shell({ route, navigate, signOut, children }: { route: Route; navigate: (route: Route) => void; signOut?: () => void; children: ReactNode }) {
  return (
    <div className="app-shell">
      <header className="site-header">
        <a className="brand" href="/" onClick={(event) => { event.preventDefault(); navigate('library'); }} aria-label="My Little Library home">
          <span className="brand-mark">▰</span><span>My Little Library</span>
        </a>
        <nav aria-label="Primary navigation">
          <a className={route === 'library' ? 'active' : ''} href="/" onClick={(event) => { event.preventDefault(); navigate('library'); }}>My Library</a>
          <a className={route === 'scan' ? 'active' : ''} href="/scan" onClick={(event) => { event.preventDefault(); navigate('scan'); }}>Scan Books</a>
          <a className={route === 'add' ? 'active' : ''} href="/add" onClick={(event) => { event.preventDefault(); navigate('add'); }}>Add Books</a>
          {signOut && <button className="button button-secondary" type="button" onClick={signOut}>Sign out</button>}
        </nav>
      </header>
      <main>{children}</main>
      <footer><span>Build a shelf of stories worth keeping.</span><span>My Little Library</span></footer>
    </div>
  );
}

function Library({ navigate, api }: { navigate: (route: Route) => void; api: LibraryApi }) {
  const [books, setBooks] = useState<GalleryBook[]>([]);
  const [state, setState] = useState<'loading' | 'ready' | 'error'>('loading');

  const load = useCallback(async (signal?: AbortSignal) => {
    setState('loading');
    try { setBooks(await api.getLibraryBooks(signal)); setState('ready'); }
    catch (error) { if ((error as Error).name !== 'AbortError') setState('error'); }
  }, [api]);
  useEffect(() => { const controller = new AbortController(); void load(controller.signal); return () => controller.abort(); }, [load]);

  return <section className="page library-page">
    <div className="page-heading"><div><p className="eyebrow">Your collection</p><h1>My Library</h1><p className="lede">A home for the books you want to remember.</p></div><button className="button button-primary" type="button" onClick={() => navigate('add')}>＋ Add Books</button></div>
    {state === 'loading' && <div className="status-panel" role="status"><span className="spinner" />Loading your library…</div>}
    {state === 'error' && <div className="status-panel error" role="alert"><strong>We couldn’t load your library.</strong><span>Check your connection and try again.</span><button className="button button-secondary" type="button" onClick={() => void load()}>Retry</button></div>}
    {state === 'ready' && books.length === 0 && <div className="empty-state"><div className="empty-art"><span>✦</span><span>▱</span><span>✧</span></div><h2>Your shelf is waiting</h2><p>Search the catalog to find your next favorite book, then add it here.</p><button className="button button-primary" type="button" onClick={() => navigate('add')}>Find a book</button></div>}
    {state === 'ready' && books.length > 0 && <div className="book-grid">{books.map((book) => <article className="book-card" key={book.editionId}><GalleryCover book={book} api={api} /><div className="book-info"><h2>{book.title}</h2><p>{book.authors.join(', ') || 'Unknown author'}</p></div></article>)}</div>}
  </section>;
}

function AddBooks({ navigate, api }: { navigate: (route: Route) => void; api: LibraryApi }) {
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
    try { const found = await api.searchCatalog(value, request.signal); if (!request.signal.aborted) { setResults(found); setSearchState('ready'); } }
    catch (error) { if (!request.signal.aborted) { setSearchState('error'); setMessage(apiMessage(error, 'Catalog search is unavailable right now. Please try again.')); } }
  }
  async function add(result: CatalogEdition, file?: File) {
    setAdding(result.editionId); setMessage('');
    try { await api.addLibraryBook(result.editionId, file); setAdded((current) => new Set(current).add(result.editionId)); }
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

function ScanBooks({ api }: { api: LibraryApi }) {
  const [photo, setPhoto] = useState<File | null>(null);
  const [preview, setPreview] = useState<string>();
  const [books, setBooks] = useState<ScannedBook[] | null>(null);
  const [error, setError] = useState('');
  const [scanning, setScanning] = useState(false);
  const inputRef = useRef<HTMLInputElement>(null);
  const cameraRef = useRef<HTMLInputElement>(null);

  function selectPhoto(event: ChangeEvent<HTMLInputElement>) {
    setPhoto(event.target.files?.[0] ?? null);
    setBooks(null);
    setError('');
    event.currentTarget.value = '';
  }

  useEffect(() => {
    if (!photo) { setPreview(undefined); return; }
    const url = URL.createObjectURL(photo);
    setPreview(url);
    return () => URL.revokeObjectURL(url);
  }, [photo]);

  async function scan() {
    if (!photo || scanning) return;
    setScanning(true);
    setError('');
    setBooks(null);
    try { setBooks(await api.scanBooks(photo)); }
    catch (failure) {
      const status = failure && typeof failure === 'object' && 'status' in failure ? Number(failure.status) : 0;
      setError(status === 429
        ? 'The scanner is busy. Your photo is still selected; retry in a moment.'
        : status === 503
          ? 'Book recognition is unavailable. Your photo is still selected; please retry shortly.'
          : status === 504
            ? 'The scan took too long. Try a smaller or clearer photo, or retry this one.'
            : apiMessage(failure, 'We couldn’t scan this photo. Check it and try again.'));
    } finally { setScanning(false); }
  }

  return <section className="page scan-page">
    <div className="page-heading compact"><div><p className="eyebrow">Read your shelf</p><h1>Scan Books</h1><p className="lede">Photograph a cover, a few books, or a shelf to read visible titles and authors.</p></div></div>
    <div className="scan-layout">
      <div className="scan-panel">
        <button className="button button-secondary" type="button" disabled={scanning} onClick={() => cameraRef.current?.click()}>Take a photo</button>
        <input ref={cameraRef} className="scan-file-input" type="file" accept="image/jpeg,image/png,image/webp" capture="environment" disabled={scanning} onChange={selectPhoto} />
        <button className="button button-secondary" type="button" disabled={scanning} onClick={() => inputRef.current?.click()}>Choose a photo</button>
        <input ref={inputRef} className="scan-file-input" type="file" accept="image/jpeg,image/png,image/webp" disabled={scanning} onChange={selectPhoto} />
        <p className="scan-hint">JPEG, PNG, or WebP · up to 10 MiB</p>
        {preview && <div className="scan-preview"><img src={preview} alt="Selected book photo preview" /><p>{photo?.name}</p></div>}
        <button className="button button-primary" type="button" disabled={!photo || scanning} onClick={() => void scan()}>
          {scanning ? 'Scanning…' : 'Scan photo'}
        </button>
        {scanning && <p className="scan-progress" role="status"><span className="spinner" />Scanning…</p>}
        {error && <p className="scan-error" role="alert">{error}</p>}
      </div>
      {books !== null && <section className="scan-results" aria-live="polite" aria-label="Scan results">
        <h2>Scan results</h2>
        {books.length === 0
          ? <div className="scan-empty"><strong>No readable book text found.</strong><p>Move closer, improve the lighting, or retake the photo with the covers and spines in focus.</p><button className="button button-secondary" type="button" onClick={() => inputRef.current?.click()}>Choose another photo</button></div>
          : <ol className="scan-book-list">{books.map((book, index) => <li key={`${index}-${book.title ?? ''}-${book.author ?? ''}`}><strong>{book.title ?? 'Title unreadable'}</strong><span>{book.author ?? 'Author unreadable'}</span></li>)}</ol>}
      </section>}
    </div>
  </section>;
}

export default function App({ api, signOut }: { api: LibraryApi; signOut?: () => void }) {
  const [route, setRoute] = useState<Route>(routeFromLocation);
  useEffect(() => { const onPop = () => setRoute(routeFromLocation()); window.addEventListener('popstate', onPop); return () => window.removeEventListener('popstate', onPop); }, []);
  const navigate = useCallback((next: Route) => { const path = next === 'add' ? '/add' : next === 'scan' ? '/scan' : '/'; if (window.location.pathname !== path) window.history.pushState({}, '', path); setRoute(next); window.scrollTo({ top: 0, behavior: 'smooth' }); }, []);
  return <Shell route={route} navigate={navigate} signOut={signOut}>{route === 'add' ? <AddBooks navigate={navigate} api={api} /> : route === 'scan' ? <ScanBooks api={api} /> : <Library navigate={navigate} api={api} />}</Shell>;
}
