import { createClient } from './api/generated/client';
import { addLibraryBook as add, getBookDetails as details, getCatalogEditions as editions, getLibraryBooks as list, getReadBooks as listRead, markBookAsRead as markRead, searchCatalog as search } from './api/generated/sdk.gen';
export type { BookDetails, CatalogEdition, CatalogWork, GalleryBook, ReadGalleryBook } from './api/generated/types.gen';

export function createLibraryApi(getAccessToken?: () => Promise<string>) {
  const client = createClient({ baseUrl: window.location.origin, cache: 'no-store', redirect: 'error' });
  // The API's fallback authorization policy applies even without per-route OpenAPI security metadata.
  if (getAccessToken) client.interceptors.request.use(async request => {
    request.headers.set('Authorization', `Bearer ${await getAccessToken()}`);
    return request;
  });
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
    async getReadBooks(signal?: AbortSignal) {
      return (await listRead({ client, signal, throwOnError: true })).data;
    },
    async getBookDetails(editionId: string, signal?: AbortSignal) {
      return (await details({ client, path: { editionId }, signal, throwOnError: true })).data;
    },
    async addLibraryBook(workId: string, editionId: string) {
      return (await add({ client, body: { workId, editionId }, throwOnError: true })).data;
    },
    async markBookAsRead(workId: string, editionId: string, rating: number, reviewComment?: string) {
      await markRead({ client, body: { workId, editionId, rating, reviewComment }, throwOnError: true });
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
