'use strict'

const assert = require('assert')
const path = require('path')
const { app, BrowserWindow, ipcMain } = require('electron')

app.disableHardwareAcceleration()

async function run() {
  const future = new Date(Date.now() + 5000).toISOString()
  const settings = {}
  const calls = { start: [], stop: 0, config: [], save: [], copy: [] }
  let config = {
    live_trading: false,
    ai_provider: 'openai',
    openai_api_key: 'test-openai-key',
    openai_api_host: 'https://openai.example',
    gemini_api_key: 'test-gemini-key',
    gemini_api_host: 'https://gemini.example',
    network_mode: 'direct',
    scan_interval_minutes: 10,
  }
  let running = false
  const portfolio = {
    bankroll: 75, initial_bankroll: 100, total_realized_pnl: 5, high_water_mark: 100,
    daily_start_value: 90, total_trades: 3, is_halted: false,
    positions: [
      { condition_id: 'alpha', question: 'Alpha market', side: 'YES', entry_price: .4,
        current_price: .5, fair_estimate_at_entry: .7, shares: 10, size_usd: 4,
        unrealized_pnl: 1, category: 'politics', event_title: 'Election', opened_at: Date.now() / 1000 - 3600,
        liquidation_limit_price: .48, book_depth_complete: true, quote_age_seconds: 1, quote_failures: 0 },
      { condition_id: 'beta', question: 'Beta market', side: 'NO', entry_price: .6,
        current_price: .25, fair_estimate_at_entry: .3, shares: 20, size_usd: 12,
        unrealized_pnl: 1, category: 'weather', event_title: 'Forecast', opened_at: Date.now() / 1000 - 7200,
        liquidation_limit_price: .24, book_depth_complete: true, quote_age_seconds: 2, quote_failures: 0 },
    ],
  }
  const trades = [{ timestamp: Date.now() / 1000 - 60, action: 'BUY', side: 'YES', question: 'Alpha market',
    price: .4, size_usd: 4, shares: 10, edge_at_entry: .3, kelly_at_entry: .1,
    exit_reason: '', is_paper: true }]
  const logs = [{ timestamp: future, level: 'INFO', message: 'Cycle complete' }]
  const value = result => async () => result
  ipcMain.handle('get-data-dir', value('C:\\test-data'))
  ipcMain.handle('set-data-dir', (_, dir) => dir)
  ipcMain.handle('browse-data-dir', value('C:\\selected-data'))
  ipcMain.handle('browse-vpn-config', value(null))
  ipcMain.handle('read-portfolio', value(portfolio))
  ipcMain.handle('read-trades', value(trades))
  ipcMain.handle('read-logs', value(logs))
  ipcMain.handle('read-estimates', value([{ record_type: 'evaluation', timestamp: Date.now() / 1000, provider_estimates: { openai: .6 } }]))
  ipcMain.handle('read-pending-orders', value([]))
  ipcMain.handle('read-equity-history', value([
    { timestamp: Date.now() / 1000 - 60, equity: 80, drawdown: .1, daily_api_cost: .2 },
    { timestamp: Date.now() / 1000, equity: 85, drawdown: .05, daily_api_cost: .4 },
  ]))
  ipcMain.handle('read-config', () => config)
  ipcMain.handle('write-config', (_, next) => { config = next; calls.config.push(next); return { ok: true } })
  ipcMain.handle('read-settings', value(settings))
  ipcMain.handle('write-settings', (_, next) => { Object.assign(settings, next); return { ok: true } })
  ipcMain.handle('fetch-ai-models', (_, options) => {
    calls.models = options
    return { models: [{ id: 'gemini-test-model', name: 'Gemini Test Model' }] }
  })
  ipcMain.handle('bot-status', () => ({ running }))
  ipcMain.handle('start-bot', (_, options) => { calls.start.push(options); running = true; return { pid: 1234 } })
  ipcMain.handle('stop-bot', () => { calls.stop++; running = false; return { ok: true } })
  ipcMain.handle('save-file', (_, options) => { calls.save.push(options); return { ok: true } })
  ipcMain.handle('open-logs-dir', value({ ok: true }))
  ipcMain.handle('copy-text', (_, text) => { calls.copy.push(text); return { ok: true } })

  const errors = []
  const win = new BrowserWindow({
    width: 1280,
    height: 800,
    show: false,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  })
  win.webContents.on('console-message', event => {
    if (event.level === 'error') errors.push(event.message)
  })
  await win.loadFile(path.join(__dirname, 'index.html'))
  await new Promise(resolve => setTimeout(resolve, 500))

  const stats = await win.webContents.executeJavaScript(`({
    bankroll: document.getElementById('val-bankroll').textContent,
    portfolio: document.getElementById('val-portval').textContent,
    positions: document.querySelectorAll('#positions-body tr').length,
    trades: document.querySelectorAll('#trades-body tr').length,
    riskMeters: document.querySelectorAll('#risk-container .risk-item').length,
    provider: document.getElementById('provider-health').textContent,
  })`)
  assert.strictEqual(stats.bankroll, '$75.00')
  assert.strictEqual(stats.portfolio, '$85.00')
  assert.strictEqual(stats.positions, 2)
  assert.strictEqual(stats.trades, 1)
  assert.strictEqual(stats.riskMeters, 8)
  assert.match(stats.provider, /OpenAI/)

  await win.webContents.executeJavaScript(`document.querySelector('#positions-table [data-sort="entry"] button').click()`)
  const sortedFirst = await win.webContents.executeJavaScript('document.querySelector("#positions-body .market-link").dataset.positionId')
  assert.strictEqual(sortedFirst, 'alpha')
  assert.strictEqual(await win.webContents.executeJavaScript('document.querySelector("#positions-table [data-sort=entry]").getAttribute("aria-sort")'), 'ascending')

  await win.webContents.executeJavaScript(`document.querySelector('#cat-filters [data-cat="politics"]').click()`)
  assert.strictEqual(await win.webContents.executeJavaScript('document.querySelectorAll("#positions-body tr").length'), 1)
  assert.strictEqual(await win.webContents.executeJavaScript('document.getElementById("positions-count").textContent'), '1 (2 total)')
  await win.webContents.executeJavaScript(`document.querySelector('#cat-filters [data-cat="politics"]').click()`)

  await win.webContents.executeJavaScript('document.querySelector("#positions-body .market-link").click()')
  await new Promise(resolve => setTimeout(resolve, 50))
  assert.strictEqual(await win.webContents.executeJavaScript('document.getElementById("position-modal").classList.contains("hidden")'), false)
  assert.match(await win.webContents.executeJavaScript('document.getElementById("position-details").textContent'), /Alpha market/)
  await win.webContents.executeJavaScript(`document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))`)
  assert.strictEqual(await win.webContents.executeJavaScript('document.getElementById("position-modal").classList.contains("hidden")'), true)

  await win.webContents.executeJavaScript(`document.querySelector('[data-history-mode="api"]').click()`)
  assert.strictEqual(settings['history-mode'], 'api')
  assert.strictEqual(await win.webContents.executeJavaScript('document.querySelector("[data-history-mode=api]").getAttribute("aria-pressed")'), 'true')
  await win.webContents.executeJavaScript('document.getElementById("btn-theme").click()')
  assert.strictEqual(settings.theme, 'light')
  assert.strictEqual(await win.webContents.executeJavaScript('document.body.classList.contains("light")'), true)

  await win.webContents.executeJavaScript('document.getElementById("btn-config").click()')
  await new Promise(resolve => setTimeout(resolve, 50))
  await win.webContents.executeJavaScript(`(() => {
    const provider = document.querySelector('[data-key="ai_provider"]')
    provider.value = 'gemini'
    provider.dispatchEvent(new Event('change', { bubbles: true }))
  })()`)
  await win.webContents.executeJavaScript('document.querySelector("#cfg-gemini_model").parentElement.querySelector(".load-models-btn").click()')
  await new Promise(resolve => setTimeout(resolve, 50))
  assert.strictEqual(calls.models.provider, 'gemini')
  assert.strictEqual(await win.webContents.executeJavaScript('document.querySelector("#cfg-gemini_model option").value'), 'gemini-test-model')
  await win.webContents.executeJavaScript(`document.querySelector('[data-key="scan_interval_minutes"]').value = '11'`)
  await win.webContents.executeJavaScript('document.getElementById("btn-save-config").click()')
  await new Promise(resolve => setTimeout(resolve, 100))
  assert.strictEqual(config.ai_provider, 'gemini')
  assert.strictEqual(config.scan_interval_minutes, 11)
  assert.strictEqual(calls.config.length, 1)

  await win.webContents.executeJavaScript('document.getElementById("btn-browse-dir").click()')
  await new Promise(resolve => setTimeout(resolve, 50))
  assert.strictEqual(await win.webContents.executeJavaScript('document.getElementById("data-dir-label").textContent'), 'C:\\selected-data')

  await win.webContents.executeJavaScript('document.getElementById("rh-positions").dispatchEvent(new KeyboardEvent("keydown", { key: "ArrowDown", bubbles: true }))')
  assert(settings['panel-positions-h'] > 180)

  await win.webContents.executeJavaScript('document.getElementById("btn-copy-log").click()')
  await new Promise(resolve => setTimeout(resolve, 50))
  assert(calls.copy.some(text => text.includes('Cycle complete')))
  await win.webContents.executeJavaScript('document.getElementById("btn-export-log").click()')
  await new Promise(resolve => setTimeout(resolve, 50))
  assert(calls.save.some(item => item.content.includes('Cycle complete')))

  await win.webContents.executeJavaScript('window.confirm = () => true; document.getElementById("btn-start-stop").click()')
  await new Promise(resolve => setTimeout(resolve, 50))
  assert.strictEqual(await win.webContents.executeJavaScript('document.getElementById("start-modal").classList.contains("hidden")'), false)
  await win.webContents.executeJavaScript(`(() => {
    document.querySelector('input[name="bot-mode"][value="python"]').checked = true
    document.getElementById('opt-verbose').checked = true
    document.getElementById('btn-confirm-start').click()
  })()`)
  await new Promise(resolve => setTimeout(resolve, 100))
  assert.deepStrictEqual(calls.start[0], { mode: 'python', verbose: true, console: false })
  assert.strictEqual(await win.webContents.executeJavaScript('document.getElementById("bot-status-badge").classList.contains("badge-green")'), true)

  await win.webContents.executeJavaScript('document.getElementById("btn-start-stop").click()')
  await new Promise(resolve => setTimeout(resolve, 50))
  assert.strictEqual(calls.stop, 1)
  assert.strictEqual(await win.webContents.executeJavaScript('document.getElementById("bot-status-badge").classList.contains("badge-gray")'), true)

  config = { ...config, live_trading: true }
  await win.webContents.executeJavaScript('document.getElementById("btn-start-stop").click()')
  await new Promise(resolve => setTimeout(resolve, 50))
  const liveGate = await win.webContents.executeJavaScript(`({
    warning: !document.getElementById('live-start-warning').classList.contains('hidden'),
    disabled: document.getElementById('btn-confirm-start').disabled,
  })`)
  assert.deepStrictEqual(liveGate, { warning: true, disabled: true })
  await win.webContents.executeJavaScript('document.getElementById("live-start-confirm").click()')
  assert.strictEqual(await win.webContents.executeJavaScript('document.getElementById("btn-confirm-start").disabled'), false)
  await win.webContents.executeJavaScript(`document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))`)

  assert.deepStrictEqual(errors, [])
  await win.close()
  console.log('dashboard UI smoke test passed')
}

app.whenReady()
  .then(run)
  .then(() => app.quit())
  .catch(error => {
    console.error(error)
    app.exit(1)
  })
