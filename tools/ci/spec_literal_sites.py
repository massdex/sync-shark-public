"""Validate the literals owner's artifact in the ratchet's temporary SQLite store."""
import collections
import hashlib
import json


def load(connection, artifact):
    connection.execute('CREATE TABLE literal_files (tree TEXT, path TEXT, hash TEXT, sites TEXT, '
                       'PRIMARY KEY(tree,path))')
    connection.execute('CREATE TABLE literal_permissions (tree TEXT, path TEXT, start INTEGER, '
                       'PRIMARY KEY(tree,path,start))')
    with artifact.open(encoding='utf-8-sig') as stream:
        for line in stream:
            row = json.loads(line)
            if row['tree'] not in ('candidate', 'release'):
                raise ValueError('literal artifact: unknown tree')
            connection.execute('INSERT INTO literal_files VALUES (?, ?, ?, ?)',
                               (row['tree'], row['path'], row['hash'], json.dumps(row['sites'])))


def consume(connection, tree, path, source, spans):
    row = connection.execute('SELECT hash, sites FROM literal_files WHERE tree=? AND path=?',
                             (tree, path)).fetchone()
    if row is None:
        raise ValueError('literal artifact: missing file row: ' + tree + ' ' + path)
    if row[0] != hashlib.sha256(source.encode('utf-8')).hexdigest():
        raise ValueError('literal artifact: source hash mismatch: ' + tree + ' ' + path)
    sites = {}
    for site in json.loads(row[1]):
        key = (site['line'], site['text'], site['ordinal'])
        if (type(key[0]) is not int or type(key[2]) is not int or
                not isinstance(key[1], str) or type(site['permitted']) is not bool or key in sites):
            raise ValueError('literal artifact: invalid or duplicate site row')
        sites[key] = site['permitted']
    line, previous = 1, 0
    ordinals = collections.Counter()
    permitted = set()
    for start, end in sorted(spans):
        advance = source.count('\n', previous, start)
        if advance:
            line += advance
            ordinals.clear()
        previous = start
        text = source[start:end]
        key = (line, text, ordinals[text])
        ordinals[text] += 1
        if key not in sites:
            raise ValueError('literal artifact: missing site row: ' + tree + ' ' + path)
        if sites.pop(key):
            permitted.add(start)
    if sites:
        raise ValueError('literal artifact: extra site row: ' + tree + ' ' + path)
    connection.executemany('INSERT INTO literal_permissions VALUES (?, ?, ?)',
                           ((tree, path, start) for start in permitted))
    connection.execute('DELETE FROM literal_files WHERE tree=? AND path=?', (tree, path))
    return permitted


def finish(connection):
    if connection.execute('SELECT 1 FROM literal_files LIMIT 1').fetchone():
        raise ValueError('literal artifact: extra file row')


def permitted(connection, tree, path):
    return {row[0] for row in connection.execute(
        'SELECT start FROM literal_permissions WHERE tree=? AND path=?', (tree, path))}
