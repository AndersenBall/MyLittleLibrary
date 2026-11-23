import { createClient } from './api/generated/client';
import { addLibraryBook as add, getLibraryBooks as list, getLibraryCover as cover, searchCatalog as search } from './api/generated/sdk.gen';
export type { CatalogEdition, GalleryBook } from './api/generated/types.gen';

export function createLibraryApi(getAccessToken?: () => Promise<string>) {
  const client = createClient({ baseUrl: window.location.origin, auth: getAccessToken, cache: 'no-store', redirect: 'error' });
  return {
    async searchCatalog(q: string, signal?: AbortSignal) {
      return (await search({ client, query: { q }, signal, throwOnError: true })).data;
    },
    async getLibraryBooks(signal?: AbortSignal) {
      return (await list({ client, signal, throwOnError: true })).data;
    },
    async addLibraryBook(editionId: string, cover?: File) {
      return (await add({ client, body: { editionId, cover }, throwOnError: true })).data;
    },
    async getLibraryCover(editionId: string, signal: AbortSignal) {
      const { data } = await cover({ client, path: { editionId }, signal, parseAs: 'blob', throwOnError: true });
      if (!(data instanceof Blob)) throw new Error('Expected a cover image.');
      return data;
    },
  };
}

export type LibraryApi = ReturnType<typeof createLibraryApi>;

export function apiMessage(error: unknown, fallback: string) {
  if (!error || typeof error !== 'object') return fallback;
  if ('detail' in error && typeof error.detail === 'string') return error.detail;
  if ('title' in error && typeof error.title === 'string') return error.title;
  return fallback;
}
