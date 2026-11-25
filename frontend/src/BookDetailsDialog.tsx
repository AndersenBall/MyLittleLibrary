import { useEffect, useRef, useState } from 'react';
import { apiMessage, type BookDetails, type LibraryApi } from './api';
import { Stars } from './BookRating';

export function BookDetailsDialog({ editionId, title, api, onClose }: { editionId: string; title: string; api: LibraryApi; onClose: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null);
  const [details, setDetails] = useState<BookDetails>();
  const [error, setError] = useState('');
  const [attempt, setAttempt] = useState(0);

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

  return <dialog ref={dialog} className="rating-dialog book-details-dialog" aria-labelledby="book-details-title" onCancel={event => { event.preventDefault(); close(); }}>
    <h2 id="book-details-title">{title}</h2>
    {!details && !error && <p role="status">Loading book details…</p>}
    {error && <div role="alert"><p className="form-message">{error}</p><button className="button button-secondary" type="button" onClick={() => setAttempt(current => current + 1)}>Retry</button></div>}
    {details && <>
      <dl className="book-details-list">
        <dt>In your library</dt><dd>{details.inLibrary ? 'Yes' : 'No'}</dd>
        <dt>Read</dt><dd>{details.isRead ? 'Yes' : 'No'}</dd>
        <dt>Rating</dt><dd>{details.rating == null ? 'Not rated' : <><Stars rating={details.rating} /> {details.rating} out of 5</>}</dd>
      </dl>
      <h3>Work</h3>
      <p className="book-description">{details.work.description || 'No work description available.'}</p>
      <dl className="book-details-list">
        <dt>Work ID</dt><dd>{details.work.workId}</dd>
        <dt>Title</dt><dd>{details.work.title}</dd>
        <dt>Authors</dt><dd>{value(details.work.authors)}</dd>
        <dt>Subjects</dt><dd>{value(details.work.subjects)}</dd>
        <dt>Series</dt><dd>{value(details.work.series)}</dd>
        <dt>First publication year</dt><dd>{value(details.work.firstPublicationYear)}</dd>
      </dl>
      <h3>Edition</h3>
      <p className="book-description">{details.edition.description || 'No edition description available.'}</p>
      <dl className="book-details-list">
        <dt>Edition ID</dt><dd>{details.edition.editionId}</dd>
        <dt>Work ID</dt><dd>{details.edition.workId}</dd>
        <dt>Title</dt><dd>{details.edition.title}</dd>
        <dt>Publishers</dt><dd>{value(details.edition.publishers)}</dd>
        <dt>Publication date</dt><dd>{value(details.edition.publicationDate)}</dd>
        <dt>Page count</dt><dd>{value(details.edition.pageCount)}</dd>
        <dt>ISBN-10</dt><dd>{value(details.edition.isbn10)}</dd>
        <dt>ISBN-13</dt><dd>{value(details.edition.isbn13)}</dd>
      </dl>
    </>}
    <div className="rating-actions"><button className="button button-secondary" type="button" onClick={close}>Close</button></div>
  </dialog>;
}
