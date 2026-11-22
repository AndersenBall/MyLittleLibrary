#!/usr/bin/env python3
"""Run against a fresh, disposable MyLittleLibrary API on an alternate port.

seed: live catalog, add concurrency, invalid/valid cover, attachment bytes.
retained: after RavenDB recreation, with catalog access intentionally blocked.
"""
import argparse
import concurrent.futures
import json
import struct
import urllib.error
import urllib.parse
import urllib.request
import zlib

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('phase', choices=['seed', 'retained'])
parser.add_argument('port', type=int, choices=[18082, 18083])
args = parser.parse_args()
base = f'http://127.0.0.1:{args.port}'
books = '/api/library/books'


def chunk(kind, data):
    return struct.pack('!I', len(data)) + kind + data + struct.pack('!I', zlib.crc32(kind + data))


png = (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('!2I5B', 2, 3, 8, 2, 0, 0, 0))
       + chunk(b'IDAT', zlib.compress(b'\0\x40\x90\xd0\x40\x90\xd0' * 3)) + chunk(b'IEND', b''))


def req(path, expected, payload=None, headers=None):
    request = urllib.request.Request(base + path, data=payload, headers=headers or {})
    try:
        response = urllib.request.urlopen(request, timeout=90)
    except urllib.error.HTTPError as exc:
        response = exc
    with response:
        data = response.read()
        assert response.status == expected or response.status in (expected if isinstance(expected, tuple) else ()), (
            path, response.status, data[:500])
        return response.status, response.headers.get_content_type(), data


def get_json(path, status=200):
    return json.loads(req(path, status)[2])


def search(query, status=200):
    return get_json('/api/catalog/search?' + urllib.parse.urlencode({'q': query}), status)


def add(edition, status, image=None):
    boundary = 'mll-smoke-cover-boundary'
    parts = [f'--{boundary}\r\nContent-Disposition: form-data; name="editionId"\r\n\r\n{edition}\r\n'.encode()]
    if image is not None:
        parts.extend([f'--{boundary}\r\nContent-Disposition: form-data; name="cover"; filename="cover.png"\r\nContent-Type: image/png\r\n\r\n'.encode(), image, b'\r\n'])
    parts.append(f'--{boundary}--\r\n'.encode())
    code, _, result = req(books, status, b''.join(parts), {'Content-Type': f'multipart/form-data; boundary={boundary}'})
    return code, json.loads(result)


def check_attachment(shelf):
    item = next(b for b in shelf if (b.get('coverUrl') or '').startswith(books))
    _, media_type, data = req(item['coverUrl'], 200)
    assert media_type == 'image/png' and data == png
    return item


req('/health', 200)
if args.phase == 'seed':
    assert get_json(books) == []
    search('  ', 400)
    assert search('xqzwyxqzwyxqzwy-no-matching-book') == []
    title = search('The Hobbit')
    match = search('978-0-547-92822-7')
    assert len(title) <= 12 and match
    edition = match[0]['editionId']
    assert 'Hobbit' in match[0]['title'] and any('Tolkien' in a for a in match[0]['authors'])
    assert '9780547928227' in match[0]['isbn13']
    assert match[0]['alreadyAdded'] is False
    add('OL27482W', 400)
    add('OL999999999999999M', 404)
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as executor:
        first, second = list(executor.map(lambda _: add(edition, (200, 201)), range(2)))
    assert {first[0], second[0]} == {200, 201} and first[1] == second[1]
    assert add(edition, 200)[1] == first[1]
    assert get_json(books) == [first[1]]
    req(f'{books}/{edition}/cover', 404)
    assert any(m['editionId'] == edition and m['alreadyAdded'] for m in search('9780547928227'))
    other = next((b for b in title if b['editionId'] != edition), None) or search('Pride and Prejudice')[0]
    add(other['editionId'], 400, png[:20])
    assert get_json(books) == [first[1]]
    assert add(other['editionId'], 201, png)[1] == check_attachment(get_json(books))
    shelf = get_json(books)
    assert len(shelf) == 2 and shelf[1] == first[1]
    print('PASS: title/ISBN search, atomic duplicate adds, invalid cover rejection, and byte-identical stored PNG')
else:
    shelf = get_json(books)
    assert len(shelf) == 2
    check_attachment(shelf)
    search('The Hobbit', 503)
    assert get_json(books) == shelf
    assert add(shelf[-1]['editionId'], 200)[1] == shelf[-1]
    print('PASS: two records and PNG survive RavenDB recreation; search fails separately; duplicate needs no upstream')
