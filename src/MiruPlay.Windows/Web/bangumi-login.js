import { t } from './i18n.js'

function escapeHtml(value) {
  return String(value ?? '').replace(/[&<>'"]/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' })[char])
}

function isSameMachineBrowser() {
  if (typeof window === 'undefined' || !window.location) return false
  const host = window.location.hostname
  return host === '127.0.0.1' || host === 'localhost' || host === '[::1]' || host === '::1'
}

function formatExpiry(ms) {
  return ms > 0 ? new Date(Number(ms)).toLocaleString() : ''
}

function oauthStatusLine(oauth) {
  if (!oauth || !oauth.loggedIn) return t('meta.oauthLoggedOut')
  return oauth.expiresAtMillis ? t('meta.oauthLoggedIn', formatExpiry(oauth.expiresAtMillis)) : t('oauth.statusLoggedIn')
}

export function bangumiLoginPanel(oauth) {
  const configured = Boolean(oauth && oauth.configured)
  const sameMachine = isSameMachineBrowser()
  const badge = configured ? t('meta.oauthConfiguredBadge') : t('meta.oauthNotConfiguredBadge')
  return '<section class="panel" style="grid-column:1/-1">'
    + '<div class="card-head"><h2>' + t('meta.oauthTitle') + '</h2><span id="oauth-configured-badge" class="badge ' + (configured ? 'ok' : 'warn') + '">' + badge + '</span></div>'
    + '<p class="muted">' + t('meta.oauthCredentialHint') + '</p>'
    + '<p id="oauth-status-line"><strong>' + oauthStatusLine(oauth) + '</strong></p>'
    + (sameMachine ? '' : '<p id="oauth-local-only" class="muted">' + t('meta.oauthLocalOnly') + '</p>')
    + '<div class="actions"><button id="oauth-start" class="primary" type="button" ' + (configured && sameMachine ? '' : 'disabled') + '>' + t('meta.oauthStart') + '</button><button id="oauth-logout" class="danger" type="button">' + t('meta.oauthLogout') + '</button></div>'
    + '<output id="oauth-message" class="muted" aria-live="polite"></output>'
    + '<details><summary>' + t('meta.oauthCredentialsTitle') + '</summary>'
    + '<form id="oauth-credentials-form" class="form-grid">'
    + '<label>' + t('meta.oauthClientId') + '<input id="oauth-client-id" maxlength="256" autocomplete="off"></label>'
    + '<label>' + t('meta.oauthClientSecret') + '<input id="oauth-client-secret" type="password" maxlength="512" autocomplete="new-password"></label>'
    + '<div class="actions"><button class="primary" type="submit">' + t('meta.oauthSaveCredentials') + '</button><button id="oauth-clear-credentials" class="danger" type="button">' + t('meta.oauthClearCredentials') + '</button></div>'
    + '</form></details>'
    + '<h3>' + t('meta.accountLogin') + '</h3>'
    + '<p class="muted">' + t('meta.accountLoginHint') + '</p>'
    + '<div id="bangumi-login-body" class="form"></div>'
    + '</section>'
}

export function bindBangumiLoginPanel(options) {
  const { root = document, api, toast, reload, oauth } = options
  const sameMachine = isSameMachineBrowser()
  const q = selector => root.querySelector(selector)
  let pollTimer = 0
  const stopPolling = () => { if (pollTimer) { clearInterval(pollTimer); pollTimer = 0 } }
  const setMessage = (text, error) => { const node = q('#oauth-message'); if (!node) return; node.textContent = text || ''; node.className = error ? 'error' : 'muted' }

  const refreshStatus = async () => {
    try {
      const status = await api('/api/metadata/bangumi/oauth')
      const line = q('#oauth-status-line')
      if (line) line.innerHTML = '<strong>' + oauthStatusLine(status) + '</strong>'
      return status
    } catch (error) {
      setMessage(error.message, true)
      return null
    }
  }

  const startOAuth = async () => {
    if (!sameMachine) { setMessage(t('meta.oauthLocalOnly'), true); return }
    try {
      const result = await api('/api/metadata/bangumi/oauth/start', { method: 'POST' })
      if (!result.authorizeUrl) { setMessage(result.message || t('meta.oauthFailed'), true); return }
      window.open(result.authorizeUrl, '_blank', 'noopener')
      setMessage(t('meta.oauthWaiting'))
      let attempts = 0
      stopPolling()
      pollTimer = setInterval(async () => {
        attempts += 1
        const status = await refreshStatus()
        if (status && ['success', 'failed', 'idle'].includes(status.state)) {
          stopPolling()
          const ok = status.state === 'success'
          setMessage(ok ? t('meta.oauthSuccess') : (status.message || t('meta.oauthFailed')), !ok)
          if (ok && reload) reload()
        } else if (attempts > 150) {
          stopPolling()
          setMessage(t('meta.oauthFailed'), true)
        }
      }, 2000)
    } catch (error) { setMessage(error.message, true) }
  }

  const logoutOAuth = async () => {
    stopPolling()
    try {
      await api('/api/metadata/bangumi/oauth/logout', { method: 'POST' })
      toast(t('meta.oauthLoggedOut'))
      if (reload) reload()
    } catch (error) { toast(error.message, true) }
  }

  const saveCredentials = async event => {
    event.preventDefault()
    try {
      await api('/api/settings/metadata/oauth', { method: 'PUT', body: JSON.stringify({ clientId: q('#oauth-client-id').value.trim(), clientSecret: q('#oauth-client-secret').value }) })
      q('#oauth-client-secret').value = ''
      toast(t('meta.oauthSaveCredentials'))
      if (reload) reload()
    } catch (error) { toast(error.message, true) }
  }

  const clearCredentials = async () => {
    if (!confirm(t('meta.oauthClearCredentials') + '?')) return
    try {
      await api('/api/settings/metadata/oauth', { method: 'DELETE' })
      toast(t('meta.oauthClearCredentials'))
      if (reload) reload()
    } catch (error) { toast(error.message, true) }
  }

  const startLoginButton = () => '<div class="actions"><button id="bangumi-login-start" class="primary" type="button">' + t('meta.loginStart') + '</button></div>'

  const challengeMarkup = state => {
    const image = state && state.captchaImage ? state.captchaImage : ''
    return '<p class="muted">' + t('meta.loginStateChallenge') + '</p>'
      + '<div id="bangumi-login-error" class="error" aria-live="polite"></div>'
      + '<div class="actions">'
      + '<img id="bangumi-captcha" alt="' + escapeHtml(t('meta.captcha')) + '" src="' + escapeHtml(image) + '" title="' + escapeHtml(t('meta.captchaRefresh')) + '" style="cursor:pointer;min-height:40px;background:#1b1b1b">'
      + '<button id="bangumi-captcha-refresh" class="secondary" type="button">' + t('meta.captchaRefresh') + '</button>'
      + '</div>'
      + '<form id="bangumi-login-form" class="form">'
      + '<label>' + t('meta.email') + '<input id="bangumi-email" type="email" autocomplete="username" maxlength="320" required></label>'
      + '<label>' + t('meta.password') + '<input id="bangumi-password" type="password" autocomplete="current-password" maxlength="256" required></label>'
      + '<label>' + t('meta.captcha') + '<input id="bangumi-captcha-text" maxlength="64" autocomplete="off" required></label>'
      + '<div class="actions"><button class="primary" type="submit">' + t('meta.loginSubmit') + '</button><button id="bangumi-login-cancel" class="danger" type="button">' + t('meta.loginCancel') + '</button></div>'
      + '</form>'
  }

  const applyChallenge = (state, message) => {
    const image = q('#bangumi-captcha')
    if (image && state && state.captchaImage) image.src = state.captchaImage
    const error = q('#bangumi-login-error')
    if (error) error.textContent = message || ''
    // Android parity: a wrong captcha keeps the email/password so the user only retypes
    // the captcha; the terminal-failure path below clears the password explicitly.
    const captcha = q('#bangumi-captcha-text')
    if (captcha) captcha.value = ''
  }

  const bindLoginBody = () => {
    const start = q('#bangumi-login-start')
    if (start) start.onclick = startLogin
    const refresh = q('#bangumi-captcha-refresh')
    const image = q('#bangumi-captcha')
    if (refresh) refresh.onclick = refreshCaptcha
    if (image) image.onclick = refreshCaptcha
    const cancel = q('#bangumi-login-cancel')
    if (cancel) cancel.onclick = cancelLogin
    const form = q('#bangumi-login-form')
    if (form) form.onsubmit = submitLogin
  }

  const renderLoginBody = state => {
    const body = q('#bangumi-login-body')
    if (!body) return
    body.innerHTML = state && state.status === 'challenge' ? challengeMarkup(state) : startLoginButton()
    bindLoginBody()
  }

  const startLogin = async () => {
    try {
      const state = await api('/api/metadata/bangumi/login/start', { method: 'POST' })
      if (state.status === 'challenge') renderLoginBody(state)
      else setMessage(state.message || t('meta.loginFailedNotice', state.status || 'failed'), true)
    } catch (error) { toast(error.message, true) }
  }

  const refreshCaptcha = async event => {
    if (event) event.preventDefault()
    try {
      const state = await api('/api/metadata/bangumi/login/captcha', { method: 'POST' })
      if (state.status === 'challenge') applyChallenge(state, state.message)
      else toast(state.message || t('meta.oauthFailed'), true)
    } catch (error) { toast(error.message, true) }
  }

  const cancelLogin = async () => {
    try {
      await api('/api/metadata/bangumi/login/cancel', { method: 'POST' })
      renderLoginBody({ status: 'idle' })
    } catch (error) { toast(error.message, true) }
  }

  const submitLogin = async event => {
    event.preventDefault()
    const email = q('#bangumi-email') ? q('#bangumi-email').value.trim() : ''
    const password = q('#bangumi-password') ? q('#bangumi-password').value : ''
    const captcha = q('#bangumi-captcha-text') ? q('#bangumi-captcha-text').value.trim() : ''
    try {
      const state = await api('/api/metadata/bangumi/login', { method: 'POST', body: JSON.stringify({ email, password, captcha }) })
      if (state.status === 'success') {
        setMessage(t('meta.loginSucceeded'))
        toast(t('meta.loginSucceeded'))
        if (reload) reload()
        return
      }
      if (state.status === 'challenge') {
        applyChallenge(state, state.message)
        return
      }
      const error = q('#bangumi-login-error')
      if (error) error.textContent = state.message || t('meta.oauthFailed')
      const passwordBox = q('#bangumi-password')
      if (passwordBox) passwordBox.value = ''
    } catch (error) {
      toast(error.message, true)
      const passwordBox = q('#bangumi-password')
      if (passwordBox) passwordBox.value = ''
    }
  }

  const startButton = q('#oauth-start')
  if (startButton && sameMachine) startButton.onclick = startOAuth
  const logoutButton = q('#oauth-logout')
  if (logoutButton) logoutButton.onclick = logoutOAuth
  const credentialsForm = q('#oauth-credentials-form')
  if (credentialsForm) credentialsForm.onsubmit = saveCredentials
  const clearButton = q('#oauth-clear-credentials')
  if (clearButton) clearButton.onclick = clearCredentials
  renderLoginBody({ status: 'idle' })
}
