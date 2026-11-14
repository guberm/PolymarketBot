'use strict'

const assert = require('assert')
const fs = require('fs')
const path = require('path')
const { app, BrowserWindow, ipcMain } = require('electron')

app.disableHardwareAcceleration()

async function run() {
  let settings = {}
  const value = result => async () => result
  ipcMain.handle('get-data-dir', value('C:\\notification-test'))
  ipcMain.handle('read-settings', value(settings))
  ipcMain.handle('write-settings', (_, next) => { settings = next; return { ok: true } })
  ipcMain.handle('read-portfolio', value(null))
  ipcMain.handle('read-trades', value([]))
  ipcMain.handle('read-logs', value([]))
  ipcMain.handle('read-config', value({}))
  ipcMain.handle('read-estimates', value([]))
  ipcMain.handle('read-pending-orders', value([]))
  ipcMain.handle('read-equity-history', value([]))
  ipcMain.handle('bot-status', value({ running: false }))

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

  const initialLanguage = await win.webContents.executeJavaScript('document.documentElement.lang')
  assert.strictEqual(initialLanguage, 'ru')
  await win.webContents.executeJavaScript(`document.getElementById('btn-lang').click()`)
  assert.strictEqual(await win.webContents.executeJavaScript('document.documentElement.lang'), 'en')
  await win.webContents.executeJavaScript(`document.getElementById('btn-lang').click()`)
  assert.strictEqual(await win.webContents.executeJavaScript('document.documentElement.lang'), 'ru')
  const logLayout = await win.webContents.executeJavaScript(`(() => {
    const header = document.querySelector('.log-card .card-header').getBoundingClientRect()
    const clear = document.getElementById('btn-clear-log').getBoundingClientRect()
    return { headerRight: header.right, clearRight: clear.right }
  })()`)
  assert(logLayout.clearRight <= logLayout.headerRight, `log controls overflow: ${JSON.stringify(logLayout)}`)

  const now = new Date().toISOString()
  win.webContents.send('bot-output', { timestamp: now, level: 'INFO', message: 'TRADE OK: YES Test market $5.00' })
  await new Promise(resolve => setTimeout(resolve, 100))
  const single = await win.webContents.executeJavaScript(`(() => {
    const count = document.querySelector('#notification-unread-count')
    document.querySelector('#notification-bell').click()
    const panel = document.querySelector('#notification-panel')
    const item = document.querySelector('.notification-item')
    const checkbox = item?.querySelector('input[type="checkbox"]')
    const before = { count: count.textContent, countHidden: count.classList.contains('hidden'), panelHidden: panel.classList.contains('hidden'), items: document.querySelectorAll('.notification-item').length, checked: checkbox?.checked }
    checkbox?.click()
    return { before, afterHidden: count.classList.contains('hidden'), afterChecked: document.querySelector('.notification-item input')?.checked, afterPanelHidden: panel.classList.contains('hidden') }
  })()`)
  assert.deepStrictEqual(single.before, { count: '1', countHidden: false, panelHidden: false, items: 1, checked: false })
  assert.strictEqual(single.afterHidden, true)
  assert.strictEqual(single.afterChecked, true)
  assert.strictEqual(single.afterPanelHidden, false)

  win.webContents.send('bot-output', { timestamp: new Date(Date.now() + 1).toISOString(), level: 'INFO', message: '[VPN] Tunnel ready. Bot external IP: 1.2.3.4' })
  win.webContents.send('bot-output', { timestamp: new Date(Date.now() + 2).toISOString(), level: 'ERROR', message: 'Provider failed' })
  win.webContents.send('bot-output', { timestamp: new Date(Date.now() + 3).toISOString(), level: 'INFO', message: 'Wallet-flow shadow: 12 trades, $345.67 volume, imbalance=+0.42' })
  await new Promise(resolve => setTimeout(resolve, 100))
  const beforeAll = await win.webContents.executeJavaScript(`(() => {
    const count = document.querySelector('#notification-unread-count')
    return { count: count.textContent, items: document.querySelectorAll('.notification-item').length, panelHidden: document.querySelector('#notification-panel').classList.contains('hidden'), titles: [...document.querySelectorAll('.notification-meta strong')].map(node => node.textContent) }
  })()`)
  assert.strictEqual(beforeAll.count, '3')
  assert.strictEqual(beforeAll.items, 4)
  assert.strictEqual(beforeAll.panelHidden, false)
  assert(beforeAll.titles.includes('Поток кошельков'))
  if (process.env.NOTIFICATION_SCREENSHOT) {
    win.setPosition(-3000, -3000)
    win.showInactive()
    await new Promise(resolve => setTimeout(resolve, 150))
    const image = await win.webContents.capturePage()
    fs.writeFileSync(process.env.NOTIFICATION_SCREENSHOT, image.toPNG())
    win.hide()
  }
  const all = await win.webContents.executeJavaScript(`(() => {
    const count = document.querySelector('#notification-unread-count')
    document.querySelector('#notification-mark-all').click()
    return { hidden: count.classList.contains('hidden'), checked: [...document.querySelectorAll('.notification-item input')].every(input => input.checked) }
  })()`)
  assert.strictEqual(all.hidden, true)
  assert.strictEqual(all.checked, true)
  assert.strictEqual(settings.notifications.length, 4)
  assert(settings.notifications.every(item => item.read))
  assert.deepStrictEqual(errors, [])

  await win.close()
  console.log('notification center Electron integration test passed')
}

app.whenReady()
  .then(run)
  .then(() => app.quit())
  .catch(error => {
    console.error(error)
    app.exit(1)
  })
