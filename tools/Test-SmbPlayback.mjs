import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';

const api = 'http://127.0.0.1:9978';
const drive = (process.env.MIRUPLAY_SMB_SMOKE_DRIVE ?? 'Z').replace(/:$/, '');
const driveResult = spawnSync('powershell.exe', [
  '-NoProfile',
  '-Command',
  `(Get-PSDrive '${drive}' -ErrorAction Stop).DisplayRoot`,
], { encoding: 'utf8' });
if (driveResult.status !== 0 || !driveResult.stdout.trim().startsWith('\\\\')) {
  throw new Error(`SMB smoke drive ${drive}: is unavailable or is not a network mapping.`);
}
const shareRoot = driveResult.stdout.trim().replace(/[\\/]+$/, '');
const fixtureName = `miruplay-windows-smb-${process.pid}`;
const mappedDirectory = `${drive}:/Download/${fixtureName}`;
const uncDirectory = `${shareRoot}\\Download\\${fixtureName}`;
const databasePath = path.join(os.tmpdir(), `${fixtureName}.db`);
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
INSERT INTO series VALUES(1,'smb-series','SMB Smoke',NULL,NULL,'',2026,1);
INSERT INTO episode VALUES(1,'smb-episode',1,1,1,1,'SMB Episode',NULL,18);
INSERT INTO media_file VALUES(1,1,'/ipc-smoke.mp4',0,0);
INSERT INTO series_artwork VALUES(1,1,1,'/poster.png');
`;

const sqliteResult = spawnSync(sqlite, [databasePath], { input: schema, encoding: 'utf8' });
if (sqliteResult.status !== 0) throw new Error('Could not create the SMB MLIP smoke database.');
fs.mkdirSync(mappedDirectory, { recursive: true });
fs.copyFileSync(databasePath, path.join(mappedDirectory, 'library.db'));
fs.copyFileSync('tests/fixtures/mpv-smoke.mp4', path.join(mappedDirectory, 'ipc-smoke.mp4'));
fs.writeFileSync(
  path.join(mappedDirectory, 'poster.png'),
  Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=', 'base64'));
const initialDatabaseMtime = fs.statSync(path.join(mappedDirectory, 'library.db')).mtimeMs;

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
const authHeaders = { 'X-MiruPlay-Token': token };
const jsonHeaders = { ...authHeaders, 'Content-Type': 'application/json' };
let sourceId = null;
const result = {};
try {
  let response = await fetch(`${api}/api/sources`, {
    method: 'POST',
    headers: jsonHeaders,
    body: JSON.stringify({
      name: 'SMB playback smoke',
      type: 'SMB',
      location: uncDirectory,
      contentMode: 'ANIME',
      recognitionMode: 'MLIP',
    }),
  });
  result.addStatus = response.status;
  const added = await response.json();
  if (!response.ok) throw new Error(added.error);
  sourceId = added.data.id;
  result.persistedConnectionKeys = Object.keys(added.data.connectionInfo).sort();

  let library = await (await fetch(`${api}/api/library`, { headers: authHeaders })).json();
  result.remoteSeries = library.data.allAnime.length;
  const animeId = library.data.allAnime[0].id;
  const detail = await (await fetch(`${api}/api/anime/${encodeURIComponent(animeId)}`, { headers: authHeaders })).json();
  const episodeId = detail.data.episodes[0].episode.id;

  response = await fetch(`${api}/api/sources/${sourceId}/scan`, { method: 'POST', headers: authHeaders });
  const scan = await response.json();
  result.scanStatus = response.status;
  result.scannedEpisodes = scan.data.episodesFound;

  response = await fetch(`${api}/api/playback/play`, {
    method: 'POST',
    headers: jsonHeaders,
    body: JSON.stringify({ episodeId }),
  });
  result.playStatus = response.status;
  result.initialStatus = (await response.json()).data;
  await new Promise(resolve => setTimeout(resolve, 2000));
  result.statusAfterTwoSeconds = (await (await fetch(`${api}/api/playback/status`, { headers: authHeaders })).json()).data;

  response = await fetch(`${api}/api/playback/command`, {
    method: 'POST',
    headers: jsonHeaders,
    body: JSON.stringify({ command: 'stop' }),
  });
  result.stopStatus = response.status;
  await response.text();

  response = await fetch(`${api}/api/sources/${sourceId}`, { method: 'DELETE', headers: authHeaders });
  result.deleteStatus = response.status;
  const deletion = await response.json();
  if (!response.ok) result.deleteError = deletion.error;
  else sourceId = null;
  await new Promise(resolve => setTimeout(resolve, 1500));
  library = await (await fetch(`${api}/api/library`, { headers: authHeaders })).json();
  result.restoredSeries = library.data.allAnime.length;
  result.databaseRemainedReadOnly = fs.statSync(path.join(mappedDirectory, 'library.db')).mtimeMs === initialDatabaseMtime;
} finally {
  if (sourceId !== null) {
    await fetch(`${api}/api/playback/command`, {
      method: 'POST',
      headers: jsonHeaders,
      body: JSON.stringify({ command: 'stop' }),
    }).catch(() => {});
    await fetch(`${api}/api/sources/${sourceId}`, { method: 'DELETE', headers: authHeaders }).catch(() => {});
  }
  if (fs.existsSync(mappedDirectory)) fs.rmSync(mappedDirectory, { recursive: true, force: true });
  if (fs.existsSync(databasePath)) fs.unlinkSync(databasePath);
}

if (result.initialStatus) delete result.initialStatus.uri;
if (result.statusAfterTwoSeconds) delete result.statusAfterTwoSeconds.uri;
result.fixtureRemoved = !fs.existsSync(mappedDirectory);
const credentialDirectory = path.join(process.env.LOCALAPPDATA, 'MiruPlay', 'source-credentials');
result.credentialFiles = fs.existsSync(credentialDirectory)
  ? fs.readdirSync(credentialDirectory).filter(file => file.endsWith('.bin')).length
  : 0;
console.log(JSON.stringify(result, null, 2));
