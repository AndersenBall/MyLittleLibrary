export const paths = { library: '/', read: '/read', add: '/add' } as const;
export type Route = keyof typeof paths;

export function routeForPath(path: unknown): Route {
  return path === paths.add ? 'add' : path === paths.read ? 'read' : 'library';
}
