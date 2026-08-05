import crypto from 'node:crypto';
import fs from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';

const api = 'http://127.0.0.1:9978';
const username = 'miruplay-smoke';
const password = crypto.randomBytes(18).toString('base64url');
const expectedAuthorization = `Basic ${Buffer.from(`${username}:${password}`).toString('base64')}`;
const databasePath = path.join(os.tmpdir(), `miruplay-webdav-play-${process.pid}.db`);
const sqlite = 'C:\\ProgramData\\miniconda3\\Library\\bin\\sqlite3.exe';
const schema = `
PRAGMA user_version=3;
CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
CREATE TABLE series(id INTEGER PRIMARY KEY,uuid TEXT UNIQUE NOT NULL,title TEXT NOT NULL,original_title TEXT,sort_title TEXT,summary TEXT,year INTEGER,series_type INTEGER NOT NULL DEFAULT 1);
CREATE TABLE series_release_date(series_id INTEGER PRIMARY KEY,air_date TEXT NOT NULL);
CREATE TABLE episode(id INTEGER PRIMARY KEY,uuid TEXT UNIQUE NOT NULL,series_id INTEGER NOT NULL,season INTEGER NOT NULL,episode REAL NOT NULL,sort_order REAL NOT NULL,title TEXT,summary TEXT,runtime INTEGER);
CREATE TABLE media_file(id INTEGER PRIMARY KEY,episode_id INTEGER NOT NULL,path TEXT NOT NULL UNIQUE,size INTEGER,modified_time INTEGER);
CREATE TABLE media_subtitle(id INTEGER PRIMARY KEY,media_file_id INTEGER NOT NULL,path TEXT NOT NULL,language TEXT,title TEXT,sort_order INTEGER NOT NULL DEFAULT 0);
CREATE TABLE media_extra(id INTEGER PRIMARY KEY,uuid TEXT UNIQUE NOT NULL,series_id INTEGER NOT NULL,extra_kind INTEGER NOT NULL,ordinal INTEGER NOT NULL,sort_order INTEGER NOT NULL,title TEXT NOT NULL,path TEXT NOT NULL UNIQUE,size INTEGER,modified_time INTEGER,runtime INTEGER);
CREATE TABLE series_artwork(id INTEGER PRIMARY KEY,series_id INTEGER NOT NULL,artwork_kind INTEGER NOT NULL,path TEXT NOT NULL);
CREATE TABLE episode_artwork(id INTEGER PRIMARY KEY,episode_id INTEGER NOT NULL,artwork_kind INTEGER NOT NULL,path TEXT NOT NULL);
CREATE TABLE genre(id INTEGER PRIMARY KEY,name TEXT UNIQUE NOT NULL);
CREATE TABLE series_genre(series_id INTEGER NOT NULL,genre_id INTEGER NOT NULL,PRIMARY KEY(series_id,genre_id));
CREATE TABLE series_external_id(series_id INTEGER NOT NULL,provider INTEGER NOT NULL,value TEXT NOT NULL,PRIMARY KEY(series_id,provider,value));
CREATE TABLE episode_external_id(episode_id INTEGER NOT NULL,provider INTEGER NOT NULL,value TEXT NOT NULL,PRIMARY KEY(episode_id,provider,value));
CREATE TABLE capability(name TEXT PRIMARY KEY,enabled INTEGER NOT NULL);
INSERT INTO meta VALUES('protocol','MLIP'),('schema','3');
INSERT INTO capability VALUES('extra',1),('subtitle',1);
INSERT INTO series VALUES(1,'remote-series','Remote Smoke',NULL,NULL,'',2026,1);
INSERT INTO episode VALUES(1,'remote-episode',1,1,1,1,'Remote Episode',NULL,18);
INSERT INTO media_file VALUES(1,1,'/ipc-smoke.mp4',0,0);
INSERT INTO series_artwork VALUES(1,1,1,'/poster.png');
`;

const sqliteResult = spawnSync(sqlite, [databasePath], { input: schema, encoding: 'utf8' });
if (sqliteResult.status !== 0) throw new Error('Could not create the MLIP smoke database.');

const tokenScript = `
Add-Type -AssemblyName System.Security
$p=Join-Path $env:LOCALAPPDATA 'MiruPlay\\web-control-token.bin'
$e=[IO.File]::ReadAllBytes($p)
$x=[Text.Encoding]::UTF8.GetBytes('MiruPlay.Windows.WebControl.v1')
[Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect($e,$x,[Security.Cryptography.DataProtectionScope]::CurrentUser))
`;
const tokenResult = spawnSync('powershell.exe', ['-NoProfile', '-Command', tokenScript], { encoding: 'utf8' });
if (tokenResult.status !== 0) throw new Error('Could not decrypt the local WebControl token.');
const token = tokenResult.stdout.trim();
const database = fs.readFileSync(databasePath);
const media = fs.readFileSync('tests/fixtures/mpv-smoke.mp4');
const poster = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', 'base64');
const requestStats = { database: 0, poster: 0, media: 0, unauthorized: 0, ranges: [] };

const server = http.createServer((request, response) => {
  if (request.headers.authorization !== expectedAuthorization) {
    requestStats.unauthorized += 1;
    response.writeHead(401, { 'Content-Length': 0 });
    response.end();
    return;
  }
  const isDatabase = request.url.toLowerCase().endsWith('/library.db');
  const isPoster = request.url.toLowerCase().endsWith('/poster.png');
  const body = isDatabase ? database : isPoster ? poster : media;
  if (isDatabase) requestStats.database += 1;
  else if (isPoster) requestStats.poster += 1;
  else requestStats.media += 1;
  const match = /bytes=(\d+)-/i.exec(request.headers.range ?? '');
  const hasRange = match !== null;
  const start = hasRange ? Math.min(Number(match[1]), body.length - 1) : 0;
  if (!isDatabase && !isPoster) requestStats.ranges.push(request.headers.range ?? 'none');
  const chunk = body.subarray(start);
  const headers = {
    'Content-Type': isDatabase ? 'application/octet-stream' : isPoster ? 'image/png' : 'video/mp4',
    'Content-Length': chunk.length,
    'Accept-Ranges': 'bytes',
    Connection: 'close',
  };
  if (hasRange) headers['Content-Range'] = `bytes ${start}-${body.length - 1}/${body.length}`;
  response.writeHead(hasRange ? 206 : 200, headers);
  if (request.method === 'HEAD') response.end();
  else response.end(chunk);
});
await new Promise((resolve, reject) => {
  server.once('error', reject);
  server.listen(0, '127.0.0.1', resolve);
});

const root = `http://127.0.0.1:${server.address().port}/dav`;
const authHeaders = { 'X-MiruPlay-Token': token };
const jsonHeaders = { ...authHeaders, 'Content-Type': 'application/json' };
let sourceId = null;
const result = {};
try {
  let response = await fetch(`${api}/api/sources`, {
    method: 'POST',
    headers: jsonHeaders,
    body: JSON.stringify({
      name: 'WebDAV playback smoke',
      type: 'WEBDAV',
      location: root,
      username,
      password,
      contentMode: 'ANIME',
      recognitionMode: 'MLIP',
    }),
  });
  result.addStatus = response.status;
  const added = await response.json();
  if (!response.ok) throw new Error(added.error);
  sourceId = added.data.id;

  const library = await (await fetch(`${api}/api/library`, { headers: authHeaders })).json();
  result.remoteSeries = library.data.allAnime.length;
  const animeId = library.data.allAnime[0].id;
  const detail = await (await fetch(`${api}/api/anime/${encodeURIComponent(animeId)}`, { headers: authHeaders })).json();
  const episodeId = detail.data.episodes[0].episode.id;

  response = await fetch(`${api}/api/playback/play`, {
    method: 'POST',
    headers: jsonHeaders,
    body: JSON.stringify({ episodeId }),
  });
  result.playStatus = response.status;
  result.initialStatus = (await response.json()).data;
  await new Promise(resolve => setTimeout(resolve, 2000));
  result.statusAfterTwoSeconds = (await (await fetch(`${api}/api/playback/status`, { headers: authHeaders })).json()).data;

  if (result.statusAfterTwoSeconds.state !== 'IDLE') {
    response = await fetch(`${api}/api/playback/command`, {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({ command: 'stop' }),
    });
    result.stopStatus = response.status;
    await response.text();
  }

  response = await fetch(`${api}/api/sources/${sourceId}`, { method: 'DELETE', headers: authHeaders });
  result.deleteStatus = response.status;
  await response.text();
  sourceId = null;
  await new Promise(resolve => setTimeout(resolve, 1500));
  result.restoredSeries = (await (await fetch(`${api}/api/library`, { headers: authHeaders })).json()).data.allAnime.length;
} finally {
  if (sourceId !== null) {
    await fetch(`${api}/api/playback/command`, {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({ command: 'stop' }),
    }).catch(() => {});
    await fetch(`${api}/api/sources/${sourceId}`, { method: 'DELETE', headers: authHeaders }).catch(() => {});
  }
  await new Promise(resolve => server.close(resolve));
  if (fs.existsSync(databasePath)) fs.unlinkSync(databasePath);
}

const secretsDirectory = path.join(process.env.LOCALAPPDATA, 'MiruPlay', 'runtime-secrets');
result.requestStats = requestStats;
const normalizedRoot = `${root}/`;
const sourceCacheKey = crypto.createHash('sha256').update(normalizedRoot).digest('hex').toUpperCase().slice(0, 24);
result.sourceCacheRemoved = !fs.existsSync(path.join(process.env.LOCALAPPDATA, 'MiruPlay', 'source-cache', sourceCacheKey));
result.temporaryAuthFiles = fs.existsSync(secretsDirectory)
  ? fs.readdirSync(secretsDirectory).filter(file => file.startsWith('mpv-auth-')).length
  : 0;
if (result.initialStatus) delete result.initialStatus.uri;
if (result.statusAfterTwoSeconds) delete result.statusAfterTwoSeconds.uri;
console.log(JSON.stringify(result, null, 2));
