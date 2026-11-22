import { addLibraryBook as add, getLibraryBooks as list, searchCatalog as search } from './api/generated/sdk.gen';
export type { CatalogEdition, GalleryBook } from './api/generated/types.gen';

export async function searchCatalog(q: string, signal?: AbortSignal) {
  return (await search({ query: { q }, signal, cache: 'no-store', throwOnError: true })).data;
}

export async function getLibraryBooks(signal?: AbortSignal) {
  return (await list({ signal, cache: 'no-store', throwOnError: true })).data;
}

export async function addLibraryBook(editionId: string, cover?: File) {
  return (await add({ body: { editionId, cover }, throwOnError: true })).data;
}

export function apiMessage(error: unknown, fallback: string) {
  if (!error || typeof error !== 'object') return fallback;
  if ('detail' in error && typeof error.detail === 'string') return error.detail;
  if ('title' in error && typeof error.title === 'string') return error.title;
  return fallback;
}
