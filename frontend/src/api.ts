import { createClient } from './api/generated/client';
import { addLibraryBook as add, getCatalogEditions as editions, getLibraryBooks as list, searchCatalog as search } from './api/generated/sdk.gen';
export type { CatalogEdition, CatalogWork, GalleryBook } from './api/generated/types.gen';

export function createLibraryApi(getAccessToken?: () => Promise<string>) {
  const client = createClient({ baseUrl: window.location.origin, auth: getAccessToken, cache: 'no-store', redirect: 'error' });
  return {
    async searchCatalog(q: string, signal?: AbortSignal) {
      return (await search({ client, query: { q }, signal, throwOnError: true })).data;
    },
    async getCatalogEditions(workId: string, offset: number, signal: AbortSignal) {
      return (await editions({ client, path: { workId }, query: { offset }, signal, throwOnError: true })).data;
    },
    async getLibraryBooks(signal?: AbortSignal) {
      return (await list({ client, signal, throwOnError: true })).data;
    },
    async addLibraryBook(workId: string, editionId: string) {
      return (await add({ client, body: { workId, editionId }, throwOnError: true })).data;
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
