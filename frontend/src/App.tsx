import { useCallback, useEffect, useRef, useState } from 'react';
import type { FormEvent, ReactNode } from 'react';
import {
  apiMessage,
  type CatalogEdition,
  type CatalogWork,
  type GalleryBook,
  type LibraryApi,
  type ReadGalleryBook,
} from './api';
import { RatingDialog, Stars } from './BookRating';

const paths = { library: '/', read: '/read', add: '/add' } as const;
type Route = keyof typeof paths;

function routeFromLocation(): Route {
  const path = window.location.pathname;
  return path === paths.add ? 'add' : path === paths.read ? 'read' : 'library';
}

function Cover({ src, title, authors, className = '', lazy = false }: { src?: string | null; title: string; authors: string[]; className?: string; lazy?: boolean }) {
  const [broken, setBroken] = useState(!src);
  useEffect(() => setBroken(!src), [src]);
  return (
    <div className={`cover ${className}`}>
      {!broken && <img src={src!} loading={lazy ? 'lazy' : undefined} alt={`Cover of ${title}`} onError={() => setBroken(true)} />}
      {broken && <div className="cover-fallback" aria-label={`No cover available for ${title}`}><span className="fallback-mark">ML</span><strong>{title}</strong><small>{authors.join(', ') || 'Unknown author'}</small></div>}
    </div>
  );
}

function Shell({ route, navigate, signOut, children }: { route: Route; navigate: (route: Route) => void; signOut?: () => void; children: ReactNode }) {
  return (
    <div className="app-shell">
      <header className="site-header">
        <a className="brand" href="/" onClick={(event) => { event.preventDefault(); navigate('library'); }} aria-label="My Little Library home">
          <span className="brand-mark">▰</span><span>My Little Library</span>
        </a>
        <nav aria-label="Primary navigation">
          <a className={route === 'library' ? 'active' : ''} aria-current={route === 'library' ? 'page' : undefined} href={paths.library} onClick={(event) => { event.preventDefault(); navigate('library'); }}>My Library</a>
          <a className={route === 'read' ? 'active' : ''} aria-current={route === 'read' ? 'page' : undefined} href={paths.read} onClick={(event) => { event.preventDefault(); navigate('read'); }}>Read Books</a>
          <a className={route === 'add' ? 'active' : ''} aria-current={route === 'add' ? 'page' : undefined} href={paths.add} onClick={(event) => { event.preventDefault(); navigate('add'); }}>Add Books</a>
          {signOut && <button className="button button-secondary" type="button" onClick={signOut}>Sign out</button>}
        </nav>
      </header>
      <main>{children}</main>
      <footer><span>Build a shelf of stories worth keeping.</span><span>My Little Library</span></footer>
    </div>
  );
}

function Gallery({ navigate, api, readingHistory }: { navigate: (route: Route) => void; api: LibraryApi; readingHistory: boolean }) {
  const [books, setBooks] = useState<(GalleryBook | ReadGalleryBook)[]>([]);
  const [state, setState] = useState<'loading' | 'ready' | 'error'>('loading');
  const collection = readingHistory ? 'read books' : 'library';

  const load = useCallback(async (signal?: AbortSignal) => {
    setState('loading');
    try { setBooks(await (readingHistory ? api.getReadBooks(signal) : api.getLibraryBooks(signal))); setState('ready'); }
    catch (error) { if ((error as Error).name !== 'AbortError') setState('error'); }
  }, [api, readingHistory]);
  useEffect(() => { const controller = new AbortController(); void load(controller.signal); return () => controller.abort(); }, [load]);

  return <section className="page library-page">
    <div className="page-heading"><div><p className="eyebrow">{readingHistory ? 'Your reading history' : 'Your collection'}</p><h1>{readingHistory ? 'Read Books' : 'My Library'}</h1><p className="lede">{readingHistory ? 'The books you’ve finished and how you rated them.' : 'A home for the books you want to remember.'}</p></div><button className="button button-primary" type="button" onClick={() => navigate('add')}>＋ Add Books</button></div>
    {state === 'loading' && <div className="status-panel" role="status"><span className="spinner" />Loading your {collection}…</div>}
    {state === 'error' && <div className="status-panel error" role="alert"><strong>We couldn’t load your {collection}.</strong><span>Check your connection and try again.</span><button className="button button-secondary" type="button" onClick={() => void load()}>Retry</button></div>}
    {state === 'ready' && books.length === 0 && <div className="empty-state"><div className="empty-art"><span>✦</span><span>▱</span><span>✧</span></div><h2>{readingHistory ? 'No read books yet' : 'Your shelf is waiting'}</h2><p>{readingHistory ? 'Find a book you’ve finished in the catalog and mark it as read.' : 'Search the catalog to find your next favorite book, then add it here.'}</p><button className="button button-primary" type="button" onClick={() => navigate('add')}>Find a book</button></div>}
    {state === 'ready' && books.length > 0 && <div className="book-grid">{books.map(book => <article className="book-card" key={book.editionId}>
      <Cover src={book.coverUrl} title={book.title} authors={book.authors} />
      <div className="book-info"><h2>{book.title}</h2>{'addedAt' in book
        ? <p>{book.authors.join(', ') || 'Unknown author'}</p>
        : book.rating == null ? <p>Not rated</p> : <Stars rating={book.rating} />}</div>
    </article>)}</div>}
  </section>;
}

function WorkResult({ work, api, initiallyOpen, renderEdition }: { work: CatalogWork; api: LibraryApi; initiallyOpen: boolean; renderEdition: (edition: CatalogEdition, authors: string[]) => ReactNode }) {
  const request = useRef<AbortController | null>(null);
  const loadMore = useRef<HTMLButtonElement>(null);
  const [open, setOpen] = useState(initiallyOpen);
  const [editions, setEditions] = useState(work.editions);
  const [nextOffset, setNextOffset] = useState<number | null>(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  useEffect(() => () => request.current?.abort(), []);

  const loadEditions = useCallback(async () => {
    if (request.current || nextOffset === null) return;
    const controller = new AbortController();
    request.current = controller;
    setLoading(true); setError('');
    try {
      const page = await api.getCatalogEditions(work.workId, nextOffset, controller.signal);
      if (controller.signal.aborted) return;
      setEditions(current => {
        const byId = new Map(current.map(edition => [edition.editionId, edition]));
        for (const edition of page.editions) byId.set(edition.editionId, edition);
        return [...byId.values()];
      });
      setNextOffset(page.nextOffset ?? null);
    } catch (error) {
      if (!controller.signal.aborted) setError(apiMessage(error, 'Couldn’t load more editions.'));
    } finally {
      if (!controller.signal.aborted) { request.current = null; setLoading(false); }
    }
  }, [api, nextOffset, work.workId]);

  useEffect(() => {
    const button = loadMore.current;
    if (!open || loading || error || !button) return;
    const observer = new IntersectionObserver(([entry]) => {
      if (entry.isIntersecting) void loadEditions();
    });
    observer.observe(button);
    return () => observer.disconnect();
  }, [open, loading, error, loadEditions]);

  return <details className="work-group" open={open} onToggle={(event) => {
    const expanded = event.currentTarget.open;
    setOpen(expanded);
    if (expanded && nextOffset === 0 && !error) void loadEditions();
  }}>
    <summary>
      <span><strong>{work.title}</strong><small>{[work.authors.join(', ') || 'Unknown author', work.firstPublicationYear,
        nextOffset === null ? `${editions.length} edition${editions.length === 1 ? '' : 's'}` : 'View editions'].filter(Boolean).join(' · ')}</small></span>
      <span className="work-chevron" aria-hidden="true">⌄</span>
    </summary>
    <div className="work-editions">
      {nextOffset === null && editions.length === 0 && <p className="edition-loading">Open Library has no editions for this work.</p>}
      {editions.map(edition => renderEdition(edition, work.authors))}
      {loading && <p className="edition-loading" role="status"><span className="spinner" />Loading editions…</p>}
      {nextOffset !== null && !loading && <div className="edition-loading">
        {error && <span role="alert">{error}</span>}
        <button ref={loadMore} className="button button-secondary" type="button" onClick={() => void loadEditions()}>{error ? 'Retry' : 'Load more editions'}</button>
      </div>}
    </div>
  </details>;
}

function AddBooks({ navigate, api }: { navigate: (route: Route) => void; api: LibraryApi }) {
  const [query, setQuery] = useState('');
  const [submittedIsbn, setSubmittedIsbn] = useState('');
  const [results, setResults] = useState<CatalogWork[]>([]);
  const [searchState, setSearchState] = useState<'idle' | 'loading' | 'ready' | 'error'>('idle');
  const [message, setMessage] = useState('');
  const searchRequest = useRef<AbortController | null>(null);
  const [adding, setAdding] = useState<string | null>(null);
  const [added, setAdded] = useState<Set<string>>(new Set());
  const [ratingTarget, setRatingTarget] = useState<{ workId: string; editionId: string; title: string } | null>(null);
  const [read, setRead] = useState<Set<string>>(new Set());

  useEffect(() => () => searchRequest.current?.abort(), []);

  async function submit(event?: FormEvent) {
    event?.preventDefault();
    const value = query.trim();
    if (!value) { setMessage('Enter a title, author, or ISBN to search.'); return; }
    searchRequest.current?.abort();
    const request = new AbortController();
    searchRequest.current = request;
    setSubmittedIsbn(value.replace(/[\s-]/g, '').toUpperCase());
    setSearchState('loading'); setMessage('');
    try { const found = await api.searchCatalog(value, request.signal); if (!request.signal.aborted) { setResults(found); setSearchState('ready'); } }
    catch (error) { if (!request.signal.aborted) { setSearchState('error'); setMessage(apiMessage(error, 'Catalog search is unavailable right now. Please try again.')); } }
  }
  async function add(workId: string, result: CatalogEdition) {
    setAdding(result.editionId); setMessage('');
    try { await api.addLibraryBook(workId, result.editionId); setAdded((current) => new Set(current).add(result.editionId)); }
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
      {results.map(work => <WorkResult key={work.workId} work={work} api={api}
        initiallyOpen={work.editions.some(edition => edition.isbn10.includes(submittedIsbn) || edition.isbn13.includes(submittedIsbn))}
        renderEdition={(result, authors) => {
        const id = result.editionId;
        const isAdded = result.alreadyAdded || added.has(id);
        const isRead = result.alreadyRead || read.has(id);
        const isbn = result.isbn13?.[0] ?? result.isbn10?.[0];
        return <article className="edition-row" key={id}>
          <Cover src={result.coverUrl} title={result.title} authors={authors} className="result-cover" lazy />
          <div className="result-details">
            <h3>{result.title}</h3>
            <p className="edition-details">
              {[result.publicationDate, result.publishers[0], result.pageCount && `${result.pageCount} pages`, isbn && `ISBN ${isbn}`].filter(Boolean).join(' · ')}{' '}
              <a href={result.editionUrl} target="_blank" rel="noreferrer">Open Library edition</a>
            </p>
            <div className="edition-actions">
              <button className={`button ${isAdded ? 'button-added' : 'button-primary'}`} type="button" disabled={isAdded || adding !== null} onClick={() => void add(work.workId, result)}>
                {adding === id ? 'Adding…' : isAdded ? '✓ Added' : 'Add to Library'}
              </button>
              <button className={`button ${isRead ? 'button-added' : 'button-secondary'}`} type="button" disabled={isRead} onClick={() => setRatingTarget({ workId: work.workId, editionId: id, title: result.title })}>
                {isRead ? '✓ Read' : 'Mark As Read'}
              </button>
            </div>
          </div>
        </article>;
      }} />)}
    </div>}
    {ratingTarget && <RatingDialog title={ratingTarget.title} onClose={() => setRatingTarget(null)} onSave={async rating => {
      const { workId, editionId } = ratingTarget;
      await api.markBookAsRead(workId, editionId, rating);
      setRead(current => new Set(current).add(editionId));
    }} />}
  </section>;
}

export default function App({ api, signOut }: { api: LibraryApi; signOut?: () => void }) {
  const [route, setRoute] = useState<Route>(routeFromLocation);
  useEffect(() => { const onPop = () => setRoute(routeFromLocation()); window.addEventListener('popstate', onPop); return () => window.removeEventListener('popstate', onPop); }, []);
  const navigate = useCallback((next: Route) => { const path = paths[next]; if (window.location.pathname !== path) window.history.pushState({}, '', path); setRoute(next); window.scrollTo({ top: 0, behavior: 'smooth' }); }, []);
  return <Shell route={route} navigate={navigate} signOut={signOut}>{route === 'add' ? <AddBooks navigate={navigate} api={api} /> : <Gallery key={route} readingHistory={route === 'read'} navigate={navigate} api={api} />}</Shell>;
}
