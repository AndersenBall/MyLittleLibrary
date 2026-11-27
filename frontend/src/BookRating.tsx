import { useEffect, useRef, useState } from 'react';
import { apiMessage } from './api';

const ratings = [1, 1.5, 2, 2.5, 3, 3.5, 4, 4.5, 5];
const star = '12,2 15,8.6 22.2,9.5 16.9,14.5 18.3,21.8 12,18.1 5.7,21.8 7.1,14.5 1.8,9.5 9,8.6';

export function Stars({ rating }: { rating: number }) {
  return <span className="stars" role="img" aria-label={`${rating} out of 5 stars`}>
    {[0, 1, 2, 3, 4].map(index => <svg key={index} viewBox="0 0 24 24" aria-hidden="true">
      <polygon points={star} className="star-empty" />
      {rating > index && <polygon points={star} className="star-filled" style={{ clipPath: rating < index + 1 ? 'inset(0 50% 0 0)' : undefined }} />}
    </svg>)}
  </span>;
}

export function RatingDialog({ title, onClose, onSave }: { title: string; onClose: () => void; onSave: (rating: number, reviewComment?: string) => Promise<void> }) {
  const dialog = useRef<HTMLDialogElement>(null);
  const [rating, setRating] = useState<number>();
  const [reviewComment, setReviewComment] = useState('');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    const element = dialog.current!;
    element.showModal();
    return () => element.close();
  }, []);

  function close() {
    dialog.current?.close();
    onClose();
  }

  return <dialog ref={dialog} className="rating-dialog" aria-labelledby="rating-title" onCancel={event => {
    event.preventDefault();
    if (!saving) close();
  }}>
    <form onSubmit={async event => {
      event.preventDefault();
      if (rating === undefined || saving) return;
      setSaving(true); setError('');
      try { await onSave(rating, reviewComment.trim() || undefined); close(); }
      catch (error) { setError(apiMessage(error, 'Couldn’t mark this book as read. Please try again.')); setSaving(false); }
    }}>
      <h2 id="rating-title">Rate this book</h2>
      <p className="rating-book-title">{title}</p>
      <fieldset className="rating-field" disabled={saving}>
        <legend>Choose 1–5 stars. Half stars are allowed.</legend>
        <div className="rating-picker">
          <div aria-hidden="true"><Stars rating={rating ?? 0} /></div>
          <div className="rating-options">
            {ratings.map(value => <label key={value} title={`${value} out of 5 stars`}>
              <input type="radio" name="rating" value={value} aria-label={`${value} out of 5 stars`} checked={rating === value} onChange={() => setRating(value)} />
            </label>)}
          </div>
        </div>
        <p className="rating-selection">{rating === undefined ? 'No rating selected' : `${rating} out of 5 stars`}</p>
      </fieldset>
      <label className="review-comment">
        Review comment (optional)
        <textarea rows={3} maxLength={2000} value={reviewComment} disabled={saving} onChange={event => setReviewComment(event.target.value)} />
      </label>
      {error && <p className="form-message" role="alert">{error}</p>}
      <div className="rating-actions">
        <button className="button button-secondary" type="button" disabled={saving} onClick={close}>Cancel</button>
        <button className="button button-primary" type="submit" disabled={rating === undefined || saving}>{saving ? 'Saving…' : 'Mark as read'}</button>
      </div>
    </form>
  </dialog>;
}
