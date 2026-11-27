import { useEffect, useRef, useState } from 'react';
import { apiMessage, type BookDetails, type LibraryApi } from './api';
import { RatingDialog, Stars } from './BookRating';

export type BookCollection = 'library' | 'read books';

export function BookDetailsDialog({ editionId, title, api, collection, onClose, onRemove, onReviewChange }: {
  editionId: string;
  title: string;
  api: LibraryApi;
  collection: BookCollection;
  onClose: () => void;
  onRemove: () => void;
  onReviewChange: (rating: number) => void;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const closeButton = useRef<HTMLButtonElement>(null);
  const removeButton = useRef<HTMLButtonElement>(null);
  const [details, setDetails] = useState<BookDetails>();
  const [error, setError] = useState('');
  const [attempt, setAttempt] = useState(0);
  const [summary, setSummary] = useState<'work' | 'edition'>('work');
  const [editing, setEditing] = useState(false);
  const [removeFrom, setRemoveFrom] = useState<BookCollection>();
  const [removing, setRemoving] = useState(false);
  const [removeError, setRemoveError] = useState('');

  useEffect(() => {
    const element = dialog.current!;
    element.showModal();
    return () => element.close();
  }, []);
  useEffect(() => {
    const controller = new AbortController();
    setError('');
    api.getBookDetails(editionId, controller.signal).then(setDetails).catch(error => {
      if (!controller.signal.aborted) setError(apiMessage(error, 'Couldn’t load book details. Please try again.'));
    });
    return () => controller.abort();
  }, [api, editionId, attempt]);

  const value = (item: string | number | null | undefined | string[]) =>
    Array.isArray(item) ? item.join(', ') || 'Not available' : item ?? 'Not available';
  function close() {
    dialog.current?.close();
    onClose();
  }
  function cancelRemoval() {
    setRemoveFrom(undefined);
    removeButton.current?.focus();
  }
  async function remove() {
    if (!details || !removeFrom || removing) return;
    setRemoving(true);
    setRemoveError('');
    try {
      if (removeFrom === 'library') await api.removeLibraryBook(editionId);
      else await api.removeReadBook(editionId);
      if (removeFrom === collection) {
        close();
        onRemove();
      } else {
        setDetails(removeFrom === 'library' ? { ...details, inLibrary: false }
          : { ...details, isRead: false, rating: null, reviewComment: null });
        setRemoveFrom(undefined);
        closeButton.current?.focus();
      }
    } catch (error) {
      setRemoveError(apiMessage(error, 'Couldn’t remove this book. Please try again.'));
    } finally {
      setRemoving(false);
    }
  }
  function confirmRemoval(target: BookCollection, button: HTMLButtonElement) {
    removeButton.current = button;
    setRemoveError('');
    setRemoveFrom(target);
  }

  return <><dialog ref={dialog} className="rating-dialog book-details-dialog" aria-labelledby="book-details-title" onCancel={event => {
    event.preventDefault();
    if (editing || removing) return;
    if (removeFrom) cancelRemoval();
    else close();
  }}>
    <h2 id="book-details-title">{title}</h2>
    {!details && !error && <p role="status">Loading book details…</p>}
    {error && <div role="alert"><p className="form-message">{error}</p><button className="button button-secondary" type="button" onClick={() => setAttempt(current => current + 1)}>Retry</button></div>}
    {details && <>
      <p className="book-details-author">{details.work.authors.join(', ') || 'Unknown author'}</p>
      <p className="book-details-pages">{details.edition.pageCount == null ? 'Page count not available' : `${details.edition.pageCount} pages`}</p>
      <div className="summary-heading">
        <button className="button button-secondary summary-toggle" type="button" onClick={() => setSummary(current => current === 'work' ? 'edition' : 'work')} aria-label={`Show ${summary === 'work' ? 'edition' : 'book'} summary`} aria-controls="book-summary">↔</button>
        <h3>Summary: <span>{summary === 'work' ? 'Book' : 'Edition'}</span></h3>
      </div>
      <p id="book-summary" className="book-description" aria-live="polite">{summary === 'work'
        ? details.work.description || 'No book description available.'
        : details.edition.description || 'No edition description available.'}</p>
      <dl className="book-details-list">
        <dt>Series</dt><dd>{value(details.work.series)}</dd>
        {!!details.work.genres?.length && <><dt>Genre</dt><dd>{value(details.work.genres)}</dd></>}
        <dt>First publication year</dt><dd>{value(details.work.firstPublicationYear)}</dd>
        <dt>Publishers</dt><dd>{value(details.edition.publishers)}</dd>
        <dt>Publication date</dt><dd>{value(details.edition.publicationDate)}</dd>
        <dt>ISBN-10</dt><dd>{value(details.edition.isbn10)}</dd>
        <dt>ISBN-13</dt><dd>{value(details.edition.isbn13)}</dd>
      </dl>
      <dl className="book-details-list">
        <dt>In your library</dt><dd>{details.inLibrary ? 'Yes' : 'No'}</dd>
        <dt>Read</dt><dd>{details.isRead ? 'Yes' : 'No'}</dd>
        <dt>Rating</dt><dd>{details.rating == null ? 'Not rated' : <><Stars rating={details.rating} /> {details.rating} out of 5</>}</dd>
        {details.reviewComment && <><dt>Your review</dt><dd className="review-text">{details.reviewComment}</dd></>}
      </dl>
    </>}
    <div className="rating-actions book-details-actions">
      {details?.isRead && <button className="button button-secondary" type="button" disabled={removing} onClick={() => { setRemoveFrom(undefined); setEditing(true); }}>Edit review</button>}
      {details?.inLibrary && <button className="button button-secondary" type="button" disabled={removing} onClick={event => confirmRemoval('library', event.currentTarget)}>Remove from library</button>}
      {details?.isRead && <button className="button button-secondary" type="button" disabled={removing} onClick={event => confirmRemoval('read books', event.currentTarget)}>Remove from read books</button>}
      <button ref={closeButton} className="button button-secondary" type="button" aria-disabled={removing} onClick={() => { if (!removing) close(); }}>Close</button>
    </div>
    {removeFrom && <section key={removeFrom} className="remove-confirmation" aria-labelledby="remove-book-title">
      <h3 id="remove-book-title">Remove from {removeFrom}?</h3>
      <p>{removeFrom === 'read books'
        ? 'This removes your rating and review comment. It won’t remove the book from your library.'
        : 'This won’t remove the book from your read books or delete your review.'}</p>
      {removeError && <p className="form-message" role="alert">{removeError}</p>}
      <div className="rating-actions">
        <button autoFocus className="button button-secondary" type="button" disabled={removing} onClick={cancelRemoval}>Cancel</button>
        <button className="button button-danger" type="button" disabled={removing} onClick={() => void remove()}>{removing ? 'Removing…' : 'Confirm removal'}</button>
      </div>
    </section>}
  </dialog>
  {editing && details && <RatingDialog title={title} initialReview={details} onClose={() => setEditing(false)} onSave={async (rating, reviewComment) => {
    await api.updateBookReview(editionId, rating, reviewComment);
    setDetails({ ...details, rating, reviewComment: reviewComment ?? null });
    onReviewChange(rating);
  }} />}</>;
}
