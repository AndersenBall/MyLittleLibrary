import { writeFile } from 'node:fs/promises';

const url = 'http://api:8080/swagger/v1/swagger.json';
const response = await fetch(url, { signal: AbortSignal.timeout(10_000) });
if (!response.ok) {
  throw new Error(`Could not fetch OpenAPI from ${url}: HTTP ${response.status}`);
}
const document = await response.text();
if (typeof JSON.parse(document)?.openapi !== 'string') {
  throw new Error(`Expected an OpenAPI document from ${url}`);
}
await writeFile(new URL('../openapi.json', import.meta.url), `${document.trimEnd()}\n`);
