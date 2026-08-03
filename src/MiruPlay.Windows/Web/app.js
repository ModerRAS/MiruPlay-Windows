const TOKEN_KEY = 'miruplay_web_token'
const $ = (selector, root = document) => root.querySelector(selector)
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)]
const access = $('#access')
const app = $('#app')
const content = $('#content')
const toolbarActions = $('#toolbar-actions')
const modal = $('#modal')
const modalContent = $('#modal-content')
const state = { token: '', view: 'library', info: null, sources: [], cloud: null, rssEditId: null, playbackTimer: null, episodeTitles: new Map(), dspConfig: null, dspData: null }
const views = {
  'audio-dsp': ['音频 DSP', '逐声道 PEQ、REW 导入和线性相位 FIR'],
  library: ['片库', '浏览并播放 MLIP 媒体库'],
  sources: ['媒体源', '管理 Local、WebDAV 与 SMB 来源'],
  automation: ['自动化', 'CloudDrive2 入库与 RSS 提交'],
  metadata: ['元数据', '管理凭据并搜索补充元数据'],
  playback: ['播放设置', '设置结束动作和字幕语言优先级'],
  operations: ['运维', '查看本地日志、后台任务和更新状态'],
  access: ['WebUI 访问', '查看地址并轮换访问令牌'],
}

function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>'"]/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' })[char])
}
function formatTime(ms) {
  const seconds = Math.max(0, Math.floor(Number(ms || 0) / 1000))
  const h = Math.floor(seconds / 3600), m = Math.floor(seconds % 3600 / 60), s = seconds % 60
  return h ? `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}` : `${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`
}
function formatDate(value) {
  if (!value) return '尚未执行'
  const ms = Number(value) < 10_000_000_000 ? Number(value) * 1000 : Number(value)
  return Number.isFinite(ms) ? new Date(ms).toLocaleString() : '未知'
}
function toast(message, error = false) {
  const node = $('#toast')
  node.textContent = message
  node.classList.toggle('error', error)
  node.classList.add('show')
  clearTimeout(toast.timer)
  toast.timer = setTimeout(() => node.classList.remove('show'), 3000)
}
async function api(path, options = {}) {
  const response = await fetch(path, {
    ...options,
    headers: {
      ...(options.body ? { 'Content-Type': 'application/json; charset=utf-8' } : {}),
      'X-MiruPlay-Token': state.token,
      ...(options.headers || {}),
    },
  })
  const envelope = await response.json().catch(() => ({ ok: false, error: `HTTP ${response.status}` }))
  if (!response.ok || !envelope.ok) {
    if (response.status === 401) showAccess('访问令牌无效或已轮换。')
    throw new Error(envelope.error || `HTTP ${response.status}`)
  }
  return envelope.data
}
function showAccess(message = '') {
  app.hidden = true
  access.hidden = false
  $('#access-error').textContent = message
  $('#access-token').focus()
  clearInterval(state.playbackTimer)
}
async function connect(token) {
  state.token = token.trim()
  if (!state.token) return
  try {
    state.info = await api('/api/info')
    localStorage.setItem(TOKEN_KEY, state.token)
    access.hidden = true
    app.hidden = false
    $('#device-name').textContent = state.info.deviceName || 'Windows'
    $('#server-url').textContent = location.origin
    await switchView(state.view)
    await refreshPlayback()
    clearInterval(state.playbackTimer)
    state.playbackTimer = setInterval(() => refreshPlayback().catch(() => {}), 2500)
  } catch (error) {
    showAccess(error.message)
  }
}
async function switchView(view) {
  state.view = view
  const [title, subtitle] = views[view]
  $('#view-title').textContent = title
  $('#view-subtitle').textContent = subtitle
  $$('#nav button').forEach(button => button.setAttribute('aria-current', button.dataset.view === view ? 'page' : 'false'))
  toolbarActions.innerHTML = view === 'library'
    ? '<input id="library-search" type="search" placeholder="搜索标题" aria-label="搜索片库"><button id="refresh-library" class="icon-button" title="刷新" aria-label="刷新">↻</button>'
    : '<button id="refresh-view" class="icon-button" title="刷新" aria-label="刷新">↻</button>'
  if (view === 'library') {
    let timer
    $('#library-search').addEventListener('input', event => { clearTimeout(timer); timer = setTimeout(() => loadLibrary(event.target.value), 250) })
    $('#refresh-library').onclick = () => loadLibrary($('#library-search').value)
  } else $('#refresh-view').onclick = () => renderView(view)
  await renderView(view)
  content.focus()
}
async function renderView(view) {
  content.innerHTML = '<div class="empty">正在读取…</div>'
  try {
    if (view === 'library') await loadLibrary()
    if (view === 'sources') await loadSources()
    if (view === 'automation') await loadAutomation()
    if (view === 'metadata') await loadMetadata()
    if (view === 'playback') await loadPlaybackSettings()
    if (view === 'audio-dsp') await loadAudioDsp()
    if (view === 'operations') await loadOperations()
    if (view === 'access') await loadAccess()
  } catch (error) {
    content.innerHTML = `<div class="panel error">${escapeHtml(error.message)}</div>`
  }
}

async function loadLibrary(query = '') {
  const data = await api(`/api/library${query ? `?query=${encodeURIComponent(query)}` : ''}`)
  const continuing = data.continueWatching || []
  const all = data.allAnime || []
  continuing.forEach(item => state.episodeTitles.set(item.episode.id, `第 ${item.episode.episodeNumber} 集 · ${item.episode.title || item.anime.titleCn || item.anime.title}`))
  content.innerHTML = `<div class="stack">
    ${continuing.length ? `<section><div class="section-head"><h2>继续观看</h2><span class="muted">${continuing.length} 项</span></div><div class="continue-grid">${continuing.map(item => `<button class="continue-card" data-play="${escapeHtml(item.episode.id)}" data-position="${Number(item.positionMs || 0)}"><strong>${escapeHtml(item.anime.titleCn || item.anime.title)}</strong><span class="muted">第 ${item.episode.episodeNumber} 集 · ${formatTime(item.positionMs)}</span><progress max="${Math.max(1, Number(item.episode.duration || 1))}" value="${Number(item.positionMs || 0)}"></progress></button>`).join('')}</div></section>` : ''}
    <section><div class="section-head"><h2>全部内容</h2><span class="muted">${all.length} 部</span></div>
    ${all.length ? `<div class="poster-grid">${all.map(anime => posterCard(anime)).join('')}</div>` : '<div class="empty panel">当前媒体源没有可显示内容。</div>'}</section>
  </div>`
  $$('[data-anime]').forEach(button => button.onclick = () => openAnime(button.dataset.anime))
  $$('[data-play]').forEach(button => button.onclick = () => playEpisode(button.dataset.play, Number(button.dataset.position)))
}
function posterCard(anime) {
  const title = anime.titleCn || anime.title || anime.id
  const poster = anime.posterUrl ? `<img class="poster" src="${escapeHtml(withToken(anime.posterUrl))}" alt="${escapeHtml(title)} 海报" loading="lazy">` : `<div class="poster">${escapeHtml(title.slice(0, 1))}</div>`
  return `<button class="poster-card" data-anime="${escapeHtml(anime.id)}">${poster}<span class="poster-copy"><strong>${escapeHtml(title)}</strong>${anime.titleCn && anime.title !== anime.titleCn ? `<span class="muted">${escapeHtml(anime.title)}</span>` : ''}<small>${Number(anime.episodeCount || 0)} 集</small></span></button>`
}
function withToken(url) {
  if (!url?.startsWith('/api/')) return url
  return `${url}${url.includes('?') ? '&' : '?'}token=${encodeURIComponent(state.token)}`
}
async function openAnime(id) {
  try {
    const detail = await api(`/api/anime/${encodeURIComponent(id)}`)
    const anime = detail.anime
    detail.episodes.forEach(item => state.episodeTitles.set(item.episode.id, `第 ${item.episode.episodeNumber} 集 · ${item.episode.title || anime.titleCn || anime.title}`))
    modalContent.innerHTML = `<section class="modal-hero"><h2>${escapeHtml(anime.titleCn || anime.title)}</h2>${anime.titleCn && anime.title !== anime.titleCn ? `<p class="muted">${escapeHtml(anime.title)}</p>` : ''}<p>${escapeHtml(anime.summary || '暂无简介')}</p><div class="actions">${(anime.genres || []).map(item => `<span class="badge">${escapeHtml(item)}</span>`).join('')}</div></section><section class="episode-list">${detail.episodes.map(item => `<article class="episode panel"><div><strong>第 ${item.episode.episodeNumber} 集 · ${escapeHtml(item.episode.title || '')}</strong><p class="muted">${item.progressMs ? `已观看 ${formatTime(item.progressMs)}` : '未观看'} · ${formatTime(item.episode.duration)}</p></div><button class="primary" data-modal-play="${escapeHtml(item.episode.id)}" data-position="${Number(item.progressMs || 0)}">播放</button></article>`).join('')}</section>`
    $$('[data-modal-play]', modalContent).forEach(button => button.onclick = () => { modal.close(); playEpisode(button.dataset.modalPlay, Number(button.dataset.position)) })
    modal.showModal()
  } catch (error) { toast(error.message, true) }
}
async function playEpisode(episodeId, startPositionMs = 0) {
  try {
    await api('/api/playback/play', { method: 'POST', body: JSON.stringify({ episodeId, startPositionMs }) })
    toast('已开始播放')
    await refreshPlayback()
  } catch (error) { toast(error.message, true) }
}
async function playbackCommand(command, extra = {}) {
  try {
    const status = await api('/api/playback/command', { method: 'POST', body: JSON.stringify({ command, ...extra }) })
    renderPlayer(status)
  } catch (error) { toast(error.message, true) }
}
async function refreshPlayback() {
  if (app.hidden) return
  renderPlayer(await api('/api/playback/status'))
}
function playbackTitle(episodeId) {
  return state.episodeTitles.get(episodeId) || episodeId || '当前媒体'
}
function renderPlayer(status) {
  const bar = $('#player-bar')
  if (!status || status.state === 'IDLE') { bar.hidden = true; return }
  if (!bar.dataset.ready) {
    bar.innerHTML = `<div><strong data-player-title></strong><small data-player-state></small></div><div class="player-time"><progress></progress><small data-player-time></small></div><div class="player-controls"><select data-audio aria-label="音频轨道" title="音频轨道"></select><select data-subtitle aria-label="字幕轨道" title="字幕轨道"></select><button data-command="seek_relative" data-delta="-10000" title="后退 10 秒" aria-label="后退 10 秒">−10</button><button data-command="toggle" title="播放或暂停" aria-label="播放或暂停"></button><button data-command="seek_relative" data-delta="30000" title="前进 30 秒" aria-label="前进 30 秒">+30</button><button data-command="stop" title="停止" aria-label="停止">■</button></div>`
    $$('[data-command]', bar).forEach(button => button.onclick = () => playbackCommand(button.dataset.command, button.dataset.delta ? { deltaMs: Number(button.dataset.delta) } : {}))
    $('[data-audio]', bar).onchange = event => playbackCommand('audio', { audioTrackId: event.target.value === '' ? null : Number(event.target.value) })
    $('[data-subtitle]', bar).onchange = event => playbackCommand('subtitle', { subtitleTrackId: event.target.value === '' ? null : Number(event.target.value) })
    bar.dataset.ready = 'true'
  }
  const audioTracks = status.audioTracks || []
  const audio = $('[data-audio]', bar)
  const audioOptions = audioTracks.map(track => ({ id: track.id, label: track.displayLabel }))
  const audioSignature = JSON.stringify(audioOptions)
  if (audio.dataset.options !== audioSignature) {
    audio.innerHTML = audioOptions.map(option => `<option value="${option.id}">${escapeHtml(option.label)}</option>`).join('')
    audio.dataset.options = audioSignature
  }
  audio.value = status.selectedAudioTrackId == null ? '' : String(status.selectedAudioTrackId)
  const tracks = status.subtitleTracks || []
  const subtitle = $('[data-subtitle]', bar)
  const subtitleOptions = [{ id: null, label: '关闭字幕' }, ...tracks.map(track => ({ id: track.id, label: track.displayLabel }))]
  const optionSignature = JSON.stringify(subtitleOptions)
  if (subtitle.dataset.options !== optionSignature) {
    subtitle.innerHTML = subtitleOptions.map(option => `<option value="${option.id ?? ''}">${escapeHtml(option.label)}</option>`).join('')
    subtitle.dataset.options = optionSignature
  }
  subtitle.value = status.selectedSubtitleTrackId == null ? '' : String(status.selectedSubtitleTrackId)
  $('[data-player-title]', bar).textContent = status.title || playbackTitle(status.episodeId)
  $('[data-player-state]', bar).textContent = status.state
  const progress = $('progress', bar)
  progress.max = Math.max(1, Number(status.durationMs || 1))
  progress.value = Number(status.positionMs || 0)
  $('[data-player-time]', bar).textContent = `${formatTime(status.positionMs)} / ${formatTime(status.durationMs)}`
  $('[data-command="toggle"]', bar).textContent = status.isPlaying ? 'Ⅱ' : '▶'
  bar.hidden = false
}

function sourceLocation(source) { return source.connectionInfo?.path || source.connectionInfo?.url || source.connectionInfo?.uncPath || '' }
async function loadSources() {
  state.sources = await api('/api/sources')
  content.innerHTML = `<div class="grid"><section class="panel stack"><div class="card-head"><h2>媒体源</h2><button id="scan-all">扫描全部</button></div>${state.sources.length ? `<div class="list">${state.sources.map(source => `<article class="list-item"><div class="card-head"><div><span class="badge ${source.isConnected ? 'ok' : 'warn'}">${escapeHtml(source.type)}</span> <strong>${escapeHtml(source.name)}</strong></div><span class="muted">${escapeHtml(source.contentMode)}</span></div><p class="break">${escapeHtml(sourceLocation(source))}</p><div class="actions"><button data-source-edit="${source.id}">编辑</button><button data-source-scan="${source.id}">扫描</button><button class="danger" data-source-delete="${source.id}">删除</button></div></article>`).join('')}</div>` : '<p class="muted">尚未添加媒体源。</p>'}</section><section class="panel"><h2 id="source-form-title">添加媒体源</h2>${sourceForm()}</section></div>`
  bindSourceForm()
  $$('[data-source-edit]').forEach(button => button.onclick = () => editSource(Number(button.dataset.sourceEdit)))
  $$('[data-source-scan]').forEach(button => button.onclick = () => scanSource(Number(button.dataset.sourceScan)))
  $$('[data-source-delete]').forEach(button => button.onclick = () => deleteSource(Number(button.dataset.sourceDelete)))
  $('#scan-all').onclick = scanAllSources
}
function sourceForm() {
  return `<form id="source-form" class="form"><input id="source-id" type="hidden"><div class="form-grid"><label>类型<select id="source-type"><option>LOCAL</option><option>WEBDAV</option><option>SMB</option></select></label><label>内容类型<select id="source-mode"><option value="ANIME">动漫</option><option value="DRAMA">电视剧</option></select></label></div><label>显示名称<input id="source-name" required maxlength="100"></label><label>位置<div class="actions"><input id="source-location" required placeholder="本地目录、https:// WebDAV 或 smb://"><button id="browse-local" type="button">浏览</button></div></label><div id="source-credentials" class="form-grid"><label>用户名<input id="source-username" autocomplete="username"></label><label>密码<input id="source-password" type="password" autocomplete="new-password" placeholder="编辑时留空保留"></label><label id="source-domain-label">域（可选）<input id="source-domain"></label></div><label id="source-recognition-label">识别方式<select id="source-recognition"><option value="MLIP">MLIP library.db</option><option value="DIRECTORY">普通目录扫描</option></select></label><div class="actions"><button id="source-test" type="button">测试连接</button><button class="primary" type="submit">保存</button><button id="source-reset" type="button">清空</button></div></form>`
}
function bindSourceForm() {
  $('#source-form').onsubmit = saveSource
  $('#source-type').onchange = updateSourceFields
  $('#source-test').onclick = testSource
  $('#source-reset').onclick = resetSourceForm
  $('#browse-local').onclick = () => browseLocal($('#source-location').value)
  updateSourceFields()
}
function updateSourceFields() {
  const type = $('#source-type').value
  $('#source-credentials').hidden = type === 'LOCAL'
  $('#source-domain-label').hidden = type !== 'SMB'
  $('#browse-local').hidden = type !== 'LOCAL'
  $('#source-recognition-label').hidden = false
}
function sourcePayload() {
  return { name: $('#source-name').value.trim(), type: $('#source-type').value, location: $('#source-location').value.trim(), username: $('#source-username').value.trim() || null, password: $('#source-password').value || null, domain: $('#source-domain').value.trim() || null, contentMode: $('#source-mode').value, recognitionMode: $('#source-recognition').value }
}
function editSource(id) {
  const source = state.sources.find(item => item.id === id)
  if (!source) return
  $('#source-id').value = source.id
  $('#source-type').value = source.type
  $('#source-mode').value = source.contentMode
  $('#source-name').value = source.name
  $('#source-location').value = sourceLocation(source)
  $('#source-recognition').value = source.connectionInfo?.recognitionMode || 'MLIP'
  $('#source-form-title').textContent = '编辑媒体源'
  updateSourceFields()
  $('#source-form').scrollIntoView({ behavior: 'smooth' })
}
function resetSourceForm() { $('#source-form').reset(); $('#source-id').value = ''; $('#source-form-title').textContent = '添加媒体源'; updateSourceFields() }
async function saveSource(event) {
  event.preventDefault()
  const id = $('#source-id').value
  try {
    await api(id ? `/api/sources/${id}` : '/api/sources', { method: id ? 'PUT' : 'POST', body: JSON.stringify(sourcePayload()) })
    toast('媒体源已保存')
    await loadSources()
  } catch (error) { toast(error.message, true) }
}
async function testSource() {
  try { const result = await api('/api/sources/test', { method: 'POST', body: JSON.stringify(sourcePayload()) }); toast(result.message) } catch (error) { toast(error.message, true) }
}
async function scanSource(id) {
  try { const result = await api(`/api/sources/${id}/scan`, { method: 'POST' }); toast(`扫描完成：${result.episodesFound} 集`); await loadSources() } catch (error) { toast(error.message, true) }
}
async function scanAllSources() { for (const source of state.sources) await scanSource(source.id) }
async function deleteSource(id) {
  if (!confirm('删除媒体源及其本机加密凭据？')) return
  try { await api(`/api/sources/${id}`, { method: 'DELETE' }); toast('媒体源已删除'); await loadSources() } catch (error) { toast(error.message, true) }
}
async function browseLocal(path = '') {
  try {
    const data = await api(`/api/local-directories${path ? `?path=${encodeURIComponent(path)}` : ''}`)
    modalContent.innerHTML = `<h2>选择本地目录</h2><p class="break muted">${escapeHtml(data.displayPath)}</p><div class="actions">${data.parentPath !== null ? '<button id="folder-up">上一级</button>' : ''}<button id="folder-select" class="primary">选择当前目录</button></div><div class="folder-list">${data.entries.map(item => `<button data-folder="${escapeHtml(item.path)}" ${item.canRead ? '' : 'disabled'}>${escapeHtml(item.name)}</button>`).join('')}</div>`
    if ($('#folder-up', modalContent)) $('#folder-up', modalContent).onclick = () => browseLocal(data.parentPath || '')
    $('#folder-select', modalContent).onclick = () => { $('#source-location').value = data.path; modal.close() }
    $$('[data-folder]', modalContent).forEach(button => button.onclick = () => browseLocal(button.dataset.folder))
    if (!modal.open) modal.showModal()
  } catch (error) { toast(error.message, true) }
}

async function loadAutomation() {
  const [cloud, run] = await Promise.all([api('/api/cloud-drive'), api('/api/cloud-drive/run')])
  state.cloud = cloud
  state.sources = await api('/api/sources')
  const config = cloud.config
  content.innerHTML = `<div class="stack"><section class="panel"><div class="card-head"><h2>CloudDrive2</h2><span class="badge ${cloud.tokenConfigured ? 'ok' : 'warn'}">${cloud.tokenConfigured ? 'Key 已验证' : '未配置 Key'}</span></div><form id="cloud-form" class="form"><label class="switch"><input id="cloud-enabled" type="checkbox" ${config.enabled ? 'checked' : ''}>启用周期执行</label><div class="form-grid"><label>服务地址<input id="cloud-endpoint" value="${escapeHtml(config.endpointUrl)}" required></label><label>用户名<input id="cloud-username" value="${escapeHtml(config.username)}" autocomplete="username"></label></div><div class="form-grid"><label>密码<input id="cloud-password" type="password" autocomplete="new-password"></label><label>API Token / Key<input id="cloud-token" type="password" autocomplete="off"></label></div><label>入库模式<select id="cloud-mode"><option value="ORGANIZED_LIBRARY" ${config.libraryMode === 'ORGANIZED_LIBRARY' ? 'selected' : ''}>整理入库</option><option value="SINGLE_DIRECTORY" ${config.libraryMode === 'SINGLE_DIRECTORY' ? 'selected' : ''}>单目录入库</option></select></label><label>下载目录 A<div class="actions"><input id="cloud-inbox" value="${escapeHtml(config.inboxPath)}"><button data-cloud-browse="inbox" type="button">浏览</button></div></label><label>媒体库目录 B<div class="actions"><input id="cloud-library" value="${escapeHtml(config.libraryPath)}"><button data-cloud-browse="library" type="button">浏览</button></div></label><div class="form-grid"><label>入库后回扫 WebDAV<select id="cloud-source"><option value="">不回扫</option>${state.sources.filter(item => item.type === 'WEBDAV').map(item => `<option value="${item.id}" ${config.webDavSourceId === item.id ? 'selected' : ''}>${escapeHtml(item.name)}</option>`).join('')}</select></label><label>间隔（分钟）<input id="cloud-interval" type="number" min="5" value="${Number(config.intervalMinutes)}"></label></div><label class="switch"><input id="cloud-proxy-enabled" type="checkbox" ${config.rssProxyEnabled ? 'checked' : ''}>RSS 使用 HTTP 代理</label><div class="form-grid"><label>代理主机<input id="cloud-proxy-host" value="${escapeHtml(config.rssProxyHost)}"></label><label>代理端口<input id="cloud-proxy-port" type="number" min="1" max="65535" value="${Number(config.rssProxyPort)}"></label></div><div class="actions"><button class="primary" type="submit">保存配置</button><button id="cloud-login" type="button">登录并保存</button><button id="cloud-save-token" type="button">验证 Key</button><button id="cloud-run" type="button">立即运行</button></div></form></section><section class="panel"><div class="card-head"><h2>运行状态</h2><span class="badge ${run.status === 'SUCCEEDED' ? 'ok' : run.status === 'FAILED' ? 'warn' : ''}">${escapeHtml(run.status)}</span></div>${run.error ? `<p class="error">${escapeHtml(run.error)}</p>` : ''}<div class="status-tiles">${summaryTiles(run.summary)}</div></section><section class="panel"><div class="card-head"><h2>RSS 订阅</h2><span class="muted">${cloud.subscriptions.length} 个</span></div><form id="rss-form" class="form"><input id="rss-id" type="hidden"><div class="form-grid"><label>名称<input id="rss-name" required></label><label>地址<input id="rss-url" type="url" required></label></div><label>标题过滤正则<input id="rss-filter"></label><label class="switch"><input id="rss-enabled" type="checkbox" checked>启用</label><div class="actions"><button class="primary" type="submit">保存订阅</button><button id="rss-reset" type="button">清空</button></div></form><hr><div class="list">${cloud.subscriptions.map(rssItem).join('') || '<p class="muted">尚未添加 RSS 订阅。</p>'}</div></section></div>`
  $('#cloud-form').onsubmit = saveCloudConfig
  $('#cloud-login').onclick = loginCloud
  $('#cloud-save-token').onclick = saveCloudToken
  $('#cloud-run').onclick = runCloud
  $$('[data-cloud-browse]').forEach(button => button.onclick = () => browseCloud(button.dataset.cloudBrowse))
  $('#rss-form').onsubmit = saveRss
  $('#rss-reset').onclick = resetRss
  $$('[data-rss-edit]').forEach(button => button.onclick = () => editRss(Number(button.dataset.rssEdit)))
  $$('[data-rss-preview]').forEach(button => button.onclick = () => previewRss(Number(button.dataset.rssPreview)))
  $$('[data-rss-toggle]').forEach(button => button.onclick = () => toggleRss(Number(button.dataset.rssToggle)))
  $$('[data-rss-delete]').forEach(button => button.onclick = () => deleteRss(Number(button.dataset.rssDelete)))
}
function summaryTiles(summary) {
  if (!summary) return '<div class="status-tile"><span>结果</span><strong>尚未运行</strong></div>'
  return Object.entries(summary).filter(([, value]) => typeof value === 'number').map(([key, value]) => `<div class="status-tile"><span>${escapeHtml(key)}</span><strong>${value}</strong></div>`).join('')
}
function cloudPayload() {
  return { endpointUrl: $('#cloud-endpoint').value.trim(), username: $('#cloud-username').value.trim(), webDavSourceId: $('#cloud-source').value ? Number($('#cloud-source').value) : null, inboxPath: $('#cloud-inbox').value.trim(), libraryPath: $('#cloud-library').value.trim(), libraryMode: $('#cloud-mode').value, intervalMinutes: Number($('#cloud-interval').value), enabled: $('#cloud-enabled').checked, rssProxyEnabled: $('#cloud-proxy-enabled').checked, rssProxyHost: $('#cloud-proxy-host').value.trim(), rssProxyPort: Number($('#cloud-proxy-port').value) }
}
async function saveCloudConfig(event) { event?.preventDefault(); try { await api('/api/cloud-drive/config', { method: 'PUT', body: JSON.stringify(cloudPayload()) }); toast('CloudDrive 配置已保存'); await loadAutomation(); return true } catch (error) { toast(error.message, true); return false } }
async function loginCloud() { try { const payload = cloudPayload(); await api('/api/cloud-drive/login', { method: 'POST', body: JSON.stringify({ endpointUrl: payload.endpointUrl, username: payload.username, password: $('#cloud-password').value }) }); toast('登录凭据已验证并保存'); await loadAutomation() } catch (error) { toast(error.message, true) } }
async function saveCloudToken() { try { await api('/api/cloud-drive/token', { method: 'POST', body: JSON.stringify({ endpointUrl: $('#cloud-endpoint').value.trim(), token: $('#cloud-token').value }) }); toast('API Token 已验证并保存'); await loadAutomation() } catch (error) { toast(error.message, true) } }
async function runCloud() { try { if (!await saveCloudConfig()) return; const result = await api('/api/cloud-drive/run', { method: 'POST' }); toast(`运行结束：${result.status}`); await loadAutomation() } catch (error) { toast(error.message, true) } }
async function browseCloud(target, path = '') { try { const data = await api(`/api/cloud-drive/directories?endpointUrl=${encodeURIComponent($('#cloud-endpoint').value.trim())}${path ? `&path=${encodeURIComponent(path)}` : ''}`); modalContent.innerHTML = `<h2>选择 CloudDrive2 目录</h2><p class="muted break">${escapeHtml(data.displayPath)}</p><div class="actions">${data.parentPath ? '<button id="cloud-up">上一级</button>' : ''}<button id="cloud-select" class="primary">选择当前目录</button></div><div class="folder-list">${data.entries.map(item => `<button data-cloud-folder="${escapeHtml(item.path)}">${escapeHtml(item.name)}</button>`).join('')}</div>`; if ($('#cloud-up', modalContent)) $('#cloud-up', modalContent).onclick = () => browseCloud(target, data.parentPath); $('#cloud-select', modalContent).onclick = () => { $(`#cloud-${target}`).value = data.path; modal.close() }; $$('[data-cloud-folder]', modalContent).forEach(button => button.onclick = () => browseCloud(target, button.dataset.cloudFolder)); if (!modal.open) modal.showModal() } catch (error) { toast(error.message, true) } }
function rssItem(item) { return `<article class="list-item"><div class="card-head"><strong>${escapeHtml(item.name)}</strong><span class="badge ${item.enabled ? 'ok' : ''}">${item.enabled ? '启用' : '停用'}</span></div><p class="break">${escapeHtml(item.url)}</p>${item.filterRegex ? `<p>过滤：${escapeHtml(item.filterRegex)}</p>` : ''}<small>上次检查：${formatDate(item.lastCheckedAt)}</small><div class="actions"><button data-rss-edit="${item.id}">编辑</button><button data-rss-preview="${item.id}">预览</button><button data-rss-toggle="${item.id}">${item.enabled ? '停用' : '启用'}</button><button class="danger" data-rss-delete="${item.id}">删除</button></div></article>` }
function editRss(id) { const item = state.cloud.subscriptions.find(value => value.id === id); if (!item) return; $('#rss-id').value = item.id; $('#rss-name').value = item.name; $('#rss-url').value = item.url; $('#rss-filter').value = item.filterRegex || ''; $('#rss-enabled').checked = item.enabled; $('#rss-form').scrollIntoView({ behavior: 'smooth' }) }
function resetRss() { $('#rss-form').reset(); $('#rss-id').value = ''; $('#rss-enabled').checked = true }
async function saveRss(event) { event.preventDefault(); const id = $('#rss-id').value; const payload = { id: Number(id || 0), name: $('#rss-name').value.trim(), url: $('#rss-url').value.trim(), filterRegex: $('#rss-filter').value.trim() || null, enabled: $('#rss-enabled').checked }; try { await api(id ? `/api/cloud-drive/rss/${id}` : '/api/cloud-drive/rss', { method: id ? 'PUT' : 'POST', body: JSON.stringify(payload) }); toast('RSS 订阅已保存'); await loadAutomation() } catch (error) { toast(error.message, true) } }
async function toggleRss(id) { const item = state.cloud.subscriptions.find(value => value.id === id); if (!item) return; try { await api(`/api/cloud-drive/rss/${id}`, { method: 'PUT', body: JSON.stringify({ ...item, enabled: !item.enabled }) }); await loadAutomation() } catch (error) { toast(error.message, true) } }
async function deleteRss(id) { if (!confirm('删除这个 RSS 订阅？已处理记录会保留用于去重。')) return; try { await api(`/api/cloud-drive/rss/${id}`, { method: 'DELETE' }); toast('RSS 订阅已删除'); await loadAutomation() } catch (error) { toast(error.message, true) } }
async function previewRss(id) { try { const result = await api(`/api/cloud-drive/rss/${id}/preview`, { method: 'POST' }); modalContent.innerHTML = `<h2>RSS 预览</h2><p>${result.total} 项 · 可提交 ${result.wouldSubmit} · 已跳过 ${result.skipped} · 缺少链接 ${result.missing}</p><table class="preview-table"><thead><tr><th>标题</th><th>链接</th><th>状态</th></tr></thead><tbody>${result.items.map(item => `<tr><td>${escapeHtml(item.title)}</td><td>${escapeHtml(item.submissionUrl || '')}</td><td><span class="badge ${item.processed ? 'ok' : ''}">${item.processed ? '已处理' : escapeHtml(item.status)}</span></td></tr>`).join('')}</tbody></table>`; modal.showModal() } catch (error) { toast(error.message, true) } }

async function loadOperations() {
  const [upload, update, tasks, logs] = await Promise.all([api('/api/log-upload'), api('/api/app-update'), api('/api/tasks'), api('/api/logs?limit=100')])
  content.innerHTML = `<div class="stack"><section class="panel"><div class="card-head"><h2>OpenObserve 日志</h2><span class="badge ${upload.tokenConfigured ? 'ok' : 'warn'}">${upload.tokenConfigured ? '令牌已配置' : '令牌未配置'}</span></div><form id="log-upload-form" class="form"><label class="switch"><input id="log-enabled" type="checkbox" ${upload.config.enabled ? 'checked' : ''}>启用日志上报</label><label>OpenObserve 地址<input id="log-endpoint" value="${escapeHtml(upload.config.endpoint)}" placeholder="https://logs.example.test"></label><label>Stream<input id="log-stream" value="${escapeHtml(upload.config.streamName)}" maxlength="100"></label><label>令牌<input id="log-token" type="password" autocomplete="new-password"></label><div class="actions"><button class="primary" type="submit">保存设置</button><button id="log-run" type="button">立即上报</button><button id="log-clear" class="danger" type="button">清除令牌</button></div></form><p class="muted">待上报 ${upload.status.pendingCount} 条 · ${escapeHtml(upload.status.lastUploadStatus || '尚未上报')}</p></section><section class="panel"><div class="card-head"><h2>Windows 更新</h2><span class="badge ${update.updateAvailable ? 'ok' : ''}">${update.supported ? (update.updateAvailable ? '有可用更新' : '暂无更新') : '未配置清单'}</span></div><p class="break">当前版本 ${escapeHtml(update.currentVersionName)}</p>${update.latest ? `<p class="break">最新版本 ${escapeHtml(update.latest.versionName)} · ${escapeHtml(update.latest.assetName)}</p>` : ''}<div class="actions"><button id="update-check">检查更新</button><button id="update-download" class="primary" ${update.updateAvailable ? '' : 'disabled'}>下载并暂存</button></div>${update.stagedInstallerPath ? `<p class="success break">已暂存：${escapeHtml(update.stagedInstallerPath)}</p>` : ''}${update.lastError ? `<p class="error">${escapeHtml(update.lastError)}</p>` : ''}<p class="muted">安装启动由 Windows 客户端生命周期处理器负责。</p></section><section class="panel"><div class="card-head"><h2>后台任务</h2><span class="muted">${tasks.length} 项</span></div><div class="list">${tasks.length ? tasks.map(task => `<article class="list-item"><div class="card-head"><strong>${escapeHtml(task.title)}</strong><span class="badge ${task.state === 'SUCCEEDED' ? 'ok' : task.state === 'FAILED' ? 'warn' : ''}">${escapeHtml(task.state)}</span></div><small>${escapeHtml(task.message || task.error || task.id)}</small></article>`).join('') : '<p class="muted">尚无后台任务。</p>'}</div></section><section class="panel"><div class="card-head"><h2>本地日志</h2><button id="download-logs">下载 JSONL</button></div><pre class="log-output">${escapeHtml(logs.records.map(record => `[${new Date(record.timestampMs).toLocaleString()}] ${record.level} ${record.message}`).join('\\n'))}</pre></section></div>`
  $('#log-upload-form').onsubmit = saveLogUpload
  $('#log-run').onclick = runLogUpload
  $('#log-clear').onclick = clearLogToken
  $('#update-check').onclick = checkUpdate
  $('#update-download').onclick = downloadUpdate
  $('#download-logs').onclick = downloadLogs
}
async function saveLogUpload(event) { event.preventDefault(); try { await api('/api/log-upload/config', { method: 'PUT', body: JSON.stringify({ enabled: $('#log-enabled').checked, endpoint: $('#log-endpoint').value.trim(), streamName: $('#log-stream').value.trim() }) }); if ($('#log-token').value) await api('/api/log-upload/token', { method: 'POST', body: JSON.stringify({ token: $('#log-token').value }) }); toast('日志上报设置已保存'); await loadOperations() } catch (error) { toast(error.message, true) } }
async function runLogUpload() { try { const result = await api('/api/log-upload/run', { method: 'POST' }); toast(result.result.message); await loadOperations() } catch (error) { toast(error.message, true) } }
async function clearLogToken() { if (!confirm('清除 OpenObserve 令牌？')) return; try { await api('/api/log-upload/token', { method: 'DELETE' }); toast('令牌已清除'); await loadOperations() } catch (error) { toast(error.message, true) } }
async function checkUpdate() { try { await api('/api/app-update/check', { method: 'POST' }); toast('更新检查完成'); await loadOperations() } catch (error) { toast(error.message, true) } }
async function downloadUpdate() { try { const result = await api('/api/app-update/download', { method: 'POST' }); toast(result.stagedInstallerPath ? '更新已暂存' : result.lastError || '更新未下载'); await loadOperations() } catch (error) { toast(error.message, true) } }
async function downloadLogs() { try { const content = await fetch('/api/logs/download', { headers: { 'X-MiruPlay-Token': state.token } }).then(response => response.text()); const link = document.createElement('a'); link.href = URL.createObjectURL(new Blob([content], { type: 'application/x-ndjson' })); link.download = 'miruplay-logs.jsonl'; link.click(); URL.revokeObjectURL(link.href) } catch (error) { toast(error.message, true) } }

async function loadMetadata() {
  const settings = await api('/api/settings/metadata')
  content.innerHTML = `<div class="grid"><section class="panel"><div class="card-head"><h2>Bangumi</h2><span class="badge ${settings.bangumiTokenConfigured ? 'ok' : 'warn'}">${settings.bangumiTokenConfigured ? '已配置' : '未配置'}</span></div><form id="bangumi-token-form" class="form"><input name="username" value="Bangumi" autocomplete="username" hidden><label>Access Token<input id="bangumi-token" type="password" autocomplete="new-password"></label><div class="actions"><button class="primary" type="submit">保存</button><button id="bangumi-clear" class="danger" type="button" ${settings.bangumiTokenConfigured ? '' : 'disabled'}>清除</button></div></form></section><section class="panel"><div class="card-head"><h2>TMDB</h2><span class="badge ${settings.tmdbTokenConfigured ? 'ok' : 'warn'}">${settings.tmdbTokenConfigured ? '已配置' : '未配置'}</span></div><form id="tmdb-token-form" class="form"><input name="username" value="TMDB" autocomplete="username" hidden><label>Read Access Token<input id="tmdb-token" type="password" autocomplete="new-password"></label><div class="actions"><button class="primary" type="submit">保存</button><button id="tmdb-clear" class="danger" type="button" ${settings.tmdbTokenConfigured ? '' : 'disabled'}>清除</button></div></form></section><section class="panel" style="grid-column:1/-1"><h2>在线搜索</h2><form id="metadata-search-form" class="form-grid"><label>提供方<select id="metadata-provider"><option value="bangumi">Bangumi</option><option value="tmdb">TMDB</option></select></label><label>关键词<input id="metadata-query" required></label><label>年份（TMDB，可选）<input id="metadata-year" type="number" min="1900" max="2100"></label><div class="actions"><button class="primary" type="submit">搜索</button></div></form><div id="metadata-results" class="list"></div></section></div>`
  $('#bangumi-token-form').onsubmit = event => saveMetadataToken(event, 'bangumi')
  $('#tmdb-token-form').onsubmit = event => saveMetadataToken(event, 'tmdb')
  $('#bangumi-clear').onclick = () => clearMetadataToken('bangumi')
  $('#tmdb-clear').onclick = () => clearMetadataToken('tmdb')
  $('#metadata-search-form').onsubmit = searchMetadata
}
async function saveMetadataToken(event, provider) { event.preventDefault(); try { await api(`/api/settings/metadata/${provider}-token`, { method: 'PUT', body: JSON.stringify({ token: $(`#${provider}-token`).value }) }); toast(`${provider} Token 已保存`); await loadMetadata() } catch (error) { toast(error.message, true) } }
async function clearMetadataToken(provider) { if (!confirm(`清除 ${provider} Token？`)) return; try { await api(`/api/settings/metadata/${provider}-token`, { method: 'DELETE' }); toast('Token 已清除'); await loadMetadata() } catch (error) { toast(error.message, true) } }
async function searchMetadata(event) { event.preventDefault(); const provider = $('#metadata-provider').value, query = $('#metadata-query').value.trim(), year = $('#metadata-year').value; try { const results = await api(`/api/metadata/${provider}/search?query=${encodeURIComponent(query)}&limit=20${provider === 'tmdb' && year ? `&year=${year}` : ''}`); $('#metadata-results').innerHTML = results.length ? results.map(item => `<article class="list-item"><div class="card-head"><strong>${escapeHtml(item.titleCn || item.title || item.name || item.id)}</strong><span class="badge">${escapeHtml(item.provider || provider)}</span></div><p>${escapeHtml(item.originalTitle || item.originalName || item.name || '')}</p><small>ID ${escapeHtml(item.id)}${item.year ? ` · ${escapeHtml(item.year)}` : ''}</small></article>`).join('') : '<p class="muted">没有结果。</p>' } catch (error) { toast(error.message, true) } }

async function loadPlaybackSettings() {
  const [settings, scan] = await Promise.all([api('/api/settings/playback'), api('/api/settings/scan')])
  content.innerHTML = `<section class="panel"><form id="playback-settings-form" class="form"><div class="form-grid"><label>应用模式<select id="app-mode"><option value="anime" ${scan.currentAppMode === 'anime' ? 'selected' : ''}>动漫</option><option value="drama" ${scan.currentAppMode === 'drama' ? 'selected' : ''}>电视剧</option></select></label><label class="switch"><input id="auto-scan-enabled" type="checkbox" ${scan.autoScanEnabled ? 'checked' : ''}>自动扫描</label></div><label>扫描周期<select id="auto-scan-interval">${scan.autoScanIntervalOptionsHours.map(hours => `<option value="${hours}" ${scan.autoScanIntervalHours === hours ? 'selected' : ''}>每 ${hours} 小时</option>`).join('')}</select></label><label>播放结束后<select id="playback-end"><option value="return_to_detail" ${settings.endAction === 'return_to_detail' ? 'selected' : ''}>返回详情</option><option value="play_next_episode" ${settings.endAction === 'play_next_episode' ? 'selected' : ''}>自动播放下一集</option></select></label><label>字幕语言优先级<select id="playback-subtitle">${[['auto','自动'],['zh_hans','简体中文'],['zh_hant','繁体中文'],['zh','中文'],['en','英语'],['ja','日语']].map(([value,label]) => `<option value="${value}" ${settings.preferredSubtitleLanguage === value ? 'selected' : ''}>${label}</option>`).join('')}</select></label><div class="actions"><button class="primary" type="submit">保存播放设置</button></div></form></section>`
  $('#playback-settings-form').onsubmit = async event => { event.preventDefault(); try { await api('/api/settings/playback', { method: 'PUT', body: JSON.stringify({ endAction: $('#playback-end').value, preferredSubtitleLanguage: $('#playback-subtitle').value }) }); await api('/api/settings/scan', { method: 'PUT', body: JSON.stringify({ currentAppMode: $('#app-mode').value, autoScanEnabled: $('#auto-scan-enabled').checked, autoScanIntervalHours: Number($('#auto-scan-interval').value) }) }); toast('播放设置已保存') } catch (error) { toast(error.message, true) } }
}
const dspTypeNames = ['peaking', 'low_shelf', 'high_shelf', 'low_pass', 'high_pass', 'notch', 'band_pass']
const dspTargetNames = [['6', '左'], ['7', '右'], ['8', '中置'], ['9', 'LFE'], ['10', '左环绕'], ['11', '右环绕'], ['0', '全部'], ['1', '前置'], ['2', '中置/LFE'], ['3', '环绕'], ['4', '5.1'], ['5', '7.1']]
const dspTargetStorage = { 0: 'all', 1: 'front', 2: 'center_lfe', 3: 'surround', 4: 'surround_5_1', 5: 'surround_7_1', 6: 'left', 7: 'right', 8: 'center', 9: 'lfe', 10: 'left_surround', 11: 'right_surround' }
function dspTypeOptions(selected) {
  return dspTypeNames.map((name, index) => `<option value="${index}" ${Number(selected) === index ? 'selected' : ''}>${escapeHtml(name)}</option>`).join('')
}
function dspTargetOptions(selected) {
  return dspTargetNames.map(([value, label]) => `<option value="${value}" ${Number(selected) === Number(value) ? 'selected' : ''}>${escapeHtml(label)}</option>`).join('')
}
function dspPreset() {
  return state.dspConfig.presets.find(item => item.id === state.dspConfig.selectedPresetId) || state.dspConfig.presets[0]
}
function dspRule(target) {
  return (dspPreset().rules || []).find(rule => Number(rule.target) === Number(target))
}
function currentDspConfig() {
  const config = JSON.parse(JSON.stringify(state.dspConfig))
  const preset = config.presets.find(item => item.id === config.selectedPresetId) || config.presets[0]
  config.selectedPresetId = preset.id
  config.enabled = $('#dsp-enabled').checked
  preset.phaseMode = Number($('#dsp-phase').value)
  preset.firQuality = Number($('#dsp-fir').value)
  preset.outputMode = Number($('#dsp-output').value)
  preset.channelLayoutId = $('#dsp-layout').value
  preset.limiter = { enabled: $('#dsp-limiter').checked, ceilingDb: Number($('#dsp-limiter-ceiling').value), releaseMs: Number($('#dsp-limiter-release').value) }
  const target = Number($('#dsp-target').value)
  const bands = $$('[data-dsp-band]').map(row => ({
    type: Number($('[data-band-type]', row).value),
    frequencyHz: Number($('[data-band-frequency]', row).value),
    gainDb: Number($('[data-band-gain]', row).value),
    q: Number($('[data-band-q]', row).value),
    enabled: $('[data-band-enabled]', row).checked,
  }))
  const previousRule = (preset.rules || []).find(rule => Number(rule.target) === target)
  preset.rules = (preset.rules || []).filter(rule => Number(rule.target) !== target)
  preset.rules.push({ target, bands, outputGainDb: previousRule?.outputGainDb || 0 })
  return config
}
function renderDspBands() {
  const target = Number($('#dsp-target').value)
  const rule = dspRule(target)
  const bands = rule?.bands || []
  $('#dsp-bands').innerHTML = bands.map((band, index) => `<tr data-dsp-band>
    <td><input data-band-enabled type="checkbox" ${band.enabled !== false ? 'checked' : ''} aria-label="启用第 ${index + 1} 个 PEQ"></td>
    <td><select data-band-type aria-label="第 ${index + 1} 个 PEQ 类型">${dspTypeOptions(band.type)}</select></td>
    <td><input data-band-frequency type="number" min="10" max="24000" step="0.01" value="${Number(band.frequencyHz || 1000)}"></td>
    <td><input data-band-gain type="number" min="-24" max="24" step="0.1" value="${Number(band.gainDb || 0)}"></td>
    <td><input data-band-q type="number" min="0.1" max="20" step="0.001" value="${Number(band.q || 1)}"></td>
    <td><button type="button" class="icon-button" data-remove-band title="删除频段" aria-label="删除第 ${index + 1} 个 PEQ">×</button></td>
  </tr>`).join('')
  $$('[data-remove-band]').forEach(button => button.onclick = () => { button.closest('tr').remove() })
}
function renderDspView() {
  const config = state.dspConfig
  const preset = dspPreset()
  content.innerHTML = `<div class="grid"><section class="panel stack"><form id="dsp-form" class="form">
    <label class="switch"><input id="dsp-enabled" type="checkbox" ${config.enabled ? 'checked' : ''}>启用 DSP</label>
    <label>预设<select id="dsp-preset">${config.presets.map(item => `<option value="${escapeHtml(item.id)}" ${item.id === config.selectedPresetId ? 'selected' : ''}>${escapeHtml(item.name)}</option>`).join('')}</select></label>
    <div class="form-grid"><label>相位模式<select id="dsp-phase"><option value="0" ${Number(preset.phaseMode) === 0 ? 'selected' : ''}>最小相位</option><option value="1" ${Number(preset.phaseMode) === 1 ? 'selected' : ''}>线性相位 FIR</option></select></label>
    <label>FIR 质量<select id="dsp-fir"><option value="1024" ${Number(preset.firQuality) === 1024 ? 'selected' : ''}>低（1024 taps）</option><option value="2048" ${Number(preset.firQuality) === 2048 ? 'selected' : ''}>中（2048 taps）</option><option value="4096" ${Number(preset.firQuality) === 4096 ? 'selected' : ''}>高（4096 taps）</option></select></label>
    <label>声道布局<select id="dsp-layout"><option value="mono" ${preset.channelLayoutId === 'mono' ? 'selected' : ''}>Mono</option><option value="stereo" ${preset.channelLayoutId === 'stereo' ? 'selected' : ''}>Stereo</option><option value="5.1" ${preset.channelLayoutId === '5.1' ? 'selected' : ''}>5.1</option><option value="7.1" ${preset.channelLayoutId === '7.1' ? 'selected' : ''}>7.1</option></select></label></div>
    <div class="form-grid"><label>目标声道<select id="dsp-target">${dspTargetOptions(6)}</select></label><label>输出模式<select id="dsp-output"><option value="0" ${Number(preset.outputMode) === 0 ? 'selected' : ''}>保持原声道</option><option value="1" ${Number(preset.outputMode) === 1 ? 'selected' : ''}>立体声下混</option><option value="2" ${Number(preset.outputMode) === 2 ? 'selected' : ''}>HRTF 双耳</option></select></label><label class="switch"><input id="dsp-limiter" type="checkbox" ${preset.limiter?.enabled ? 'checked' : ''}>限幅</label><label>上限 dB<input id="dsp-limiter-ceiling" type="number" min="-24" max="0" step="0.1" value="${Number(preset.limiter?.ceilingDb ?? -1)}"></label><label>释放 ms<input id="dsp-limiter-release" type="number" min="1" max="2000" step="1" value="${Number(preset.limiter?.releaseMs ?? 100)}"></label></div>
    <div class="actions"><label class="file-button">导入 REW<input id="dsp-rew-file" type="file" accept=".txt,.req,text/plain" hidden></label><button id="dsp-import" type="button">导入到当前声道</button><button id="dsp-add" type="button">添加频段</button><button id="dsp-preview" type="button">预览响应</button></div>
    <div class="table-scroll"><table class="data-table"><thead><tr><th>启用</th><th>类型</th><th>频率 Hz</th><th>增益 dB</th><th>Q</th><th></th></tr></thead><tbody id="dsp-bands"></tbody></table></div>
    <div id="dsp-preview-result" class="muted" role="status"></div><div class="actions"><button class="primary" type="submit">应用 DSP</button></div>
  </form></section><section class="panel"><h2>状态</h2><p class="muted">${escapeHtml(state.dspData.effectiveRoute || 'disabled')}</p><p class="muted">${(state.dspData.warnings || []).map(escapeHtml).join('<br>') || '没有警告'}</p><p class="muted">预览和 REW 导入不会自动保存，点击应用后才会作用于当前 MiruPlay mpv。</p></section></div>`
  renderDspBands()
  $('#dsp-target').onchange = () => { state.dspConfig = currentDspConfig(); renderDspBands() }
  $('#dsp-preset').onchange = () => { state.dspConfig.selectedPresetId = $('#dsp-preset').value; renderDspView() }
  $('#dsp-add').onclick = () => { const target = Number($('#dsp-target').value); state.dspConfig = currentDspConfig(); const preset = dspPreset(); const rule = dspRule(target) || { target, bands: [], outputGainDb: 0 }; rule.bands.push({ type: 0, frequencyHz: 1000, gainDb: 0, q: 1, enabled: true }); if (!preset.rules.includes(rule)) preset.rules.push(rule); renderDspBands() }
  $('#dsp-preview').onclick = async () => { try { state.dspConfig = currentDspConfig(); const preview = await api('/api/audio-dsp/preview', { method: 'POST', body: JSON.stringify({ config: state.dspConfig }) }); $('#dsp-preview-result').textContent = preview.channels.map(channel => `${channel.channel}: ${channel.magnitudeDb.length} 个采样点`).join('；') } catch (error) { toast(error.message, true) } }
  $('#dsp-import').onclick = async () => { const file = $('#dsp-rew-file').files[0]; if (!file) { toast('请先选择 REW 文件', true); return } try { const target = Number($('#dsp-target').value); const imported = await api('/api/audio-dsp/import-rew', { method: 'POST', body: JSON.stringify({ target: dspTargetStorage[target], content: await file.text() }) }); state.dspConfig = currentDspConfig(); const presetNow = dspPreset(); presetNow.rules = (presetNow.rules || []).filter(rule => Number(rule.target) !== target); presetNow.rules.push({ target, bands: imported.bands.map(band => ({ type: dspTypeNames.indexOf(band.type), frequencyHz: band.frequencyHz, gainDb: band.gainDb, q: band.q, enabled: band.enabled })), outputGainDb: 0 }); renderDspBands(); toast(`已导入 ${imported.bands.length} 个频段`) } catch (error) { toast(error.message, true) } }
  $('#dsp-form').onsubmit = async event => { event.preventDefault(); try { state.dspConfig = currentDspConfig(); const result = await api('/api/audio-dsp', { method: 'PUT', body: JSON.stringify({ config: state.dspConfig }) }); state.dspConfig = result.config; state.dspData = result; toast('音频 DSP 已应用'); renderDspView() } catch (error) { toast(error.message, true) } }
}
async function loadAudioDsp() {
  state.dspData = await api('/api/audio-dsp')
  state.dspConfig = state.dspData.config
  renderDspView()
}

async function loadAccess() {
  const data = await api('/api/web-control/access')
  content.innerHTML = `<section class="panel stack"><div class="card-head"><h2>访问状态</h2><span class="badge ${data.enabled ? 'ok' : 'warn'}">${data.enabled ? '已启用' : '已停用'}</span></div><div class="status-tiles">${data.urls.map(url => `<div class="status-tile"><span>访问地址</span><strong>${escapeHtml(url)}</strong></div>`).join('')}</div><label>当前访问令牌<input id="current-token" value="${escapeHtml(data.accessToken)}" readonly></label><div class="actions"><button id="copy-token">复制令牌</button><button id="rotate-token" class="danger">轮换令牌</button><button id="disconnect">断开此浏览器</button></div></section>`
  $('#copy-token').onclick = async () => { await navigator.clipboard.writeText(data.accessToken); toast('令牌已复制') }
  $('#rotate-token').onclick = async () => { if (!confirm('旧令牌将立即失效，继续轮换？')) return; try { const next = await api('/api/web-control/access/rotate-token', { method: 'POST' }); state.token = next.accessToken; localStorage.setItem(TOKEN_KEY, state.token); toast('令牌已轮换并更新当前浏览器'); await loadAccess() } catch (error) { toast(error.message, true) } }
  $('#disconnect').onclick = () => { localStorage.removeItem(TOKEN_KEY); state.token = ''; showAccess() }
}

$('#access-form').onsubmit = event => { event.preventDefault(); connect($('#access-token').value) }
$('#nav').onclick = event => { const button = event.target.closest('[data-view]'); if (button) switchView(button.dataset.view) }
$('#modal-close').onclick = () => modal.close()
modal.addEventListener('click', event => { if (event.target === modal) modal.close() })
const queryToken = new URLSearchParams(location.search).get('token')
if (queryToken) history.replaceState({}, '', location.pathname)
const savedToken = queryToken || localStorage.getItem(TOKEN_KEY) || ''
if (savedToken) { $('#access-token').value = savedToken; connect(savedToken) } else showAccess()
