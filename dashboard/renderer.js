'use strict'

// ── Settings (file-backed, loaded async at boot) ───────────────────────────
let _settings = {}
function getSetting(key, def) { return key in _settings ? _settings[key] : def }
function setSetting(key, val) { _settings[key] = val; api.writeSettings(_settings) }

// ── Translations ──────────────────────────────────────────────────────────
const TRANS = {
  ru: {
    // Header
    statusStopped: 'ОСТАНОВЛЕН', statusRunning: 'РАБОТАЕТ', statusHalted: 'ЗАМОРОЖЕН',
    configBtn: '⚙ Настройки', startBtn: '▶ Запуск', stopBtn: '■ Стоп',
    updatedAt: v => `обновлено ${v}`,
    // Stats subs
    freeCash: 'СВОБОДНЫЕ СРЕДСТВА', portfolioValue: 'СТОИМОСТЬ ПОРТФЕЛЯ',
    realizedPnl: 'РЕАЛИЗОВАННАЯ П/У', unrealizedPnl: 'НЕРЕАЛИЗОВАННАЯ П/У',
    drawdown: 'ПРОСАДКА', winRate: 'ДОЛЯ ПОБЕД',
    subInitial: v => `нач. ${v}`,
    subDeployed: v => `${v} в позициях`,
    subTrades: n => `${n} сделок всего`,
    subOpen: n => `${n} открыто`,
    subHwm: v => `пик ${v}`,
    subClosed: (w, t) => `${w} / ${t} закрыто`,
    // Sections
    openPositions: 'ОТКРЫТЫЕ ПОЗИЦИИ', tradeHistory: 'ИСТОРИЯ СДЕЛОК',
    cumulativePnl: 'НАКОПЛЕННАЯ П/У', exposureByCategory: 'ПОЗИЦИИ ПО КАТЕГОРИЯМ',
    riskLimits: 'ЛИМИТЫ РИСКА', exitBreakdown: 'ПРИЧИНЫ ВЫХОДА', liveLog: 'ЖУРНАЛ',
    // Log controls
    autoScroll: 'авто-прокрутка', folderBtn: '📂 папка', exportBtn: '⬇ экспорт', copyBtn: '⎘ копировать', clearBtn: '✕ очистить',
    // Config modal
    configTitle: '⚙ Настройки', saveBtn: '💾 Сохранить', browseBtn: 'Обзор', dataDirLabel: 'Папка данных: ',
    // Start modal
    startModalTitle: '▶ Запуск бота', implLabel: 'Реализация', flagsLabel: 'Флаги', launchBtn: '▶ Запустить',
    // Table headers
    colMarket: 'РЫНОК', colSide: 'СТОРОНА', colEntry: 'ВХОД', colCurrent: 'ТЕКУЩАЯ',
    colFair: 'ОЦ. СТОИМ.', colShares: 'ТОКЕНЫ', colCost: 'ЗАТРАТЫ', colValue: 'СТОИМОСТЬ',
    colPnlUsd: 'П/У $', colPnlPct: 'П/У %', colEdge: 'ПРЕИМУЩ.', colCategory: 'КАТЕГОРИЯ', colAge: 'ВОЗРАСТ',
    colTime: 'ВРЕМЯ', colAction: 'ДЕЙСТВИЕ', colPrice: 'ЦЕНА', colSize: 'ОБЪЁМ',
    colKelly: 'КЕЛЛИ', colExit: 'ВЫХОД', colPaper: 'ТЕСТ',
    // Empty states
    emptyPositions: 'Нет открытых позиций', emptyTrades: 'Сделок пока нет',
    emptyWaiting: 'Ожидание…', emptyNoTrades: 'Нет завершённых сделок',
    noData: 'Нет данных — ожидание portfolio.json',
    // Risk meters
    riskTotalExposure: 'Общие позиции', riskLargestPos: 'Крупнейшая позиция',
    riskDailyLoss: 'Дневные убытки', riskMaxDD: 'Макс. просадка',
    riskFreeCash: 'Свободные средства', riskPositions: 'Открытых позиций',
    // Exit reasons
    exitStopLoss: 'Стоп-лосс', exitTakeProfit: 'Тейк-профит', exitEdgeGone: 'Грань ушла',
    exitResolvedWon: 'Победа', exitResolvedLost: 'Поражение',
    exitTopUpSell: 'Топап-продажа', exitReestimate: 'Переоценка',
    // Bot messages
    botStarted: (pid, mode) => `Бот запущен (PID ${pid}, режим: ${mode})`,
    botStopped: code => `Бот завершён (код ${code})`,
    startError: e => `Ошибка запуска: ${e}`,
    // Tooltips
    tips: {
      tipFreeCash:       'Свободные USDC на счёте, не вложенные в позиции.\nОбновляется каждый цикл через синхронизацию баланса с блокчейном.',
      tipPortfolioValue: 'Bankroll + текущая стоимость открытых позиций по рыночной цене.\nЭта сумма используется для проверки лимитов риска.',
      tipRealizedPnl:    'Суммарная прибыль/убыток по всем закрытым позициям:\nпродажи, стоп-лоссы, тейк-профиты, разрешённые рынки.',
      tipUnrealizedPnl:  'Бумажная прибыль/убыток по открытым позициям.\nТекущая рыночная цена × токены − затраты на вход.',
      tipDrawdown:       'Текущее снижение от исторического максимума портфеля.\nБот останавливается при превышении лимита max drawdown.',
      tipWinRate:        'Доля закрытых позиций, принёсших прибыль.\nУчитываются только полностью закрытые позиции.',
      tipPositions:      'Рынки, где бот держит токены YES или NO.\nЕсть сортировка по любому столбцу. Фильтрация по категориям — через пилюли выше.',
      tipCumPnl:         'Накопленная реализованная прибыль/убыток по закрытым сделкам.\nКаждая точка — момент закрытия позиции (SELL).\nНереализованный P&L открытых позиций не учитывается.',
      tipCatChart:       'Распределение вложенного капитала по категориям рынков.\nОснован на стоимости входа в открытые позиции.',
      tipTrades:         'Все ордера BUY и SELL, исполненные ботом.\nВключает цену, долю Келли и причину выхода.\nПоследние 500 сделок, сортировка по любому столбцу.',
      tipRisk:           'Текущие метрики риска относительно настроенных лимитов.\nПолоска краснеет при превышении лимита.\nЛимиты настраиваются в ⚙ Настройках.',
      tipExit:           'Распределение причин закрытия позиций:\nстоп-лосс — цена упала > 30% от входа\nтейк-профит — цена ≥ 95¢\nedge gone — рынок прошёл оценку бота\nresolved — рынок завершился',
      tipLog:            'Вывод запущенного бота в реальном времени.\nПри каждом новом запуске лог ротируется в отдельный файл.',
    },
  },
  en: {
    statusStopped: 'STOPPED', statusRunning: 'RUNNING', statusHalted: 'HALTED',
    configBtn: '⚙ Config', startBtn: '▶ Start Bot', stopBtn: '■ Stop Bot',
    updatedAt: v => `updated ${v}`,
    freeCash: 'FREE CASH', portfolioValue: 'PORTFOLIO VALUE',
    realizedPnl: 'REALIZED P&L', unrealizedPnl: 'UNREALIZED P&L',
    drawdown: 'DRAWDOWN', winRate: 'WIN RATE',
    subInitial: v => `initial ${v}`,
    subDeployed: v => `${v} deployed`,
    subTrades: n => `${n} total trades`,
    subOpen: n => `${n} open`,
    subHwm: v => `hwm ${v}`,
    subClosed: (w, t) => `${w} / ${t} closed`,
    openPositions: 'OPEN POSITIONS', tradeHistory: 'TRADE HISTORY',
    cumulativePnl: 'CUMULATIVE P&L', exposureByCategory: 'EXPOSURE BY CATEGORY',
    riskLimits: 'RISK LIMITS', exitBreakdown: 'EXIT BREAKDOWN', liveLog: 'LIVE LOG',
    autoScroll: 'auto-scroll', folderBtn: '📂 folder', exportBtn: '⬇ export', copyBtn: '⎘ copy', clearBtn: '✕ clear',
    configTitle: '⚙ Configuration', saveBtn: '💾 Save', browseBtn: 'Browse', dataDirLabel: 'Data dir: ',
    startModalTitle: '▶ Start Bot', implLabel: 'Implementation', flagsLabel: 'Flags', launchBtn: '▶ Launch',
    colMarket: 'MARKET', colSide: 'SIDE', colEntry: 'ENTRY', colCurrent: 'CURRENT',
    colFair: 'FAIR EST.', colShares: 'SHARES', colCost: 'COST', colValue: 'VALUE',
    colPnlUsd: 'P&L $', colPnlPct: 'P&L %', colEdge: 'EDGE', colCategory: 'CATEGORY', colAge: 'AGE',
    colTime: 'TIME', colAction: 'ACTION', colPrice: 'PRICE', colSize: 'SIZE',
    colKelly: 'KELLY', colExit: 'EXIT', colPaper: 'PAPER',
    emptyPositions: 'No open positions', emptyTrades: 'No trades yet',
    emptyWaiting: 'Waiting…', emptyNoTrades: 'No closed trades yet',
    noData: 'No data — waiting for portfolio.json',
    riskTotalExposure: 'Total Exposure', riskLargestPos: 'Largest Position',
    riskDailyLoss: 'Daily P&L Loss', riskMaxDD: 'Max Drawdown',
    riskFreeCash: 'Free Cash', riskPositions: 'Positions Open',
    exitStopLoss: 'stop loss', exitTakeProfit: 'take profit', exitEdgeGone: 'edge gone',
    exitResolvedWon: 'resolved won', exitResolvedLost: 'resolved lost',
    exitTopUpSell: 'top up sell', exitReestimate: 'reestimate exit',
    botStarted: (pid, mode) => `Bot started (PID ${pid}, mode: ${mode})`,
    botStopped: code => `Bot process exited (code ${code})`,
    startError: e => `Failed to start: ${e}`,
    // Tooltips
    tips: {
      tipFreeCash:       'Free USDC on your account, not yet deployed in positions.\nUpdated every cycle via on-chain balance sync.',
      tipPortfolioValue: 'Bankroll + current value of all open positions at mid-price.\nThis is what all risk limits are measured against.',
      tipRealizedPnl:    'Total profit/loss from all closed positions:\nsells, stop-losses, take-profits, resolved markets.',
      tipUnrealizedPnl:  'Paper profit/loss on currently open positions.\nMid-price × shares − entry cost.',
      tipDrawdown:       'Current portfolio decline from its all-time peak value.\nBot halts when drawdown exceeds the max drawdown limit.',
      tipWinRate:        'Percentage of closed positions that were profitable.\nOnly counts fully closed positions.',
      tipPositions:      'Markets where the bot holds YES or NO shares.\nClick any column header to sort. Use category pills above to filter.',
      tipCumPnl:         'Running total of realized P&L from closed trades over time.\nEach point marks a position close (SELL).\nDoes not include unrealized gains on open positions.',
      tipCatChart:       'Current capital deployed broken down by market category.\nBased on entry cost of open positions.',
      tipTrades:         'All BUY and SELL orders executed by the bot.\nShows price, Kelly fraction, and exit reason.\nLast 500 trades, sortable by any column.',
      tipRisk:           'Real-time risk meters vs configured limits.\nBar turns red when a limit is breached.\nAdjust limits in ⚙ Config.',
      tipExit:           'Breakdown of why closed positions were exited:\nstop-loss — price dropped > 30% from entry\ntake-profit — price reached ≥ 95¢\nedge gone — market price passed bot\'s fair estimate\nresolved — market settled',
      tipLog:            'Live output from the running bot process.\nRotated to a new timestamped file on each bot start.',
    },
  },
}

let currentLang = 'ru' // overwritten after settings load

function t(key, ...args) {
  const val = TRANS[currentLang]?.[key] ?? TRANS.en[key]
  return typeof val === 'function' ? val(...args) : (val ?? key)
}

function applyLang() {
  document.querySelectorAll('[data-i18n]').forEach(el => {
    const key = el.dataset.i18n
    const val = t(key)
    const suffix = el.tagName === 'TH' ? ' ' : ''
    // Preserve child elements (tip icons, sort indicators) — update only the leading text node
    const firstText = [...el.childNodes].find(n => n.nodeType === 3)
    if (firstText) {
      firstText.textContent = val + suffix
    } else {
      el.insertBefore(document.createTextNode(val + suffix), el.firstChild)
    }
  })
  applyTips()
  // Language button shows the OTHER language (what you'd switch TO)
  const btn = document.getElementById('btn-lang')
  if (btn) btn.textContent = currentLang === 'ru' ? 'EN' : 'RU'
}

function applyTips() {
  const tipDict = TRANS[currentLang]?.tips ?? TRANS.en.tips
  document.querySelectorAll('.tip-icon[data-tip-key]').forEach(el => {
    const text = tipDict[el.dataset.tipKey]
    if (text) el.dataset.tip = text
  })
}

// ── Floating tooltip (position:fixed — not clipped by overflow:hidden) ────
function initTooltips() {
  const popup = document.createElement('div')
  popup.className = 'tooltip-popup hidden'
  document.body.appendChild(popup)

  let hideTimer = null

  document.addEventListener('mouseover', e => {
    const icon = e.target.closest('.tip-icon[data-tip]')
    if (!icon) return
    clearTimeout(hideTimer)
    popup.textContent = icon.dataset.tip
    popup.classList.remove('hidden')

    // Position: prefer above, fall back to below if near top of viewport
    const rect = icon.getBoundingClientRect()
    const TIP_W = 240, GAP = 8
    let left = rect.left + rect.width / 2 - TIP_W / 2
    left = Math.max(8, Math.min(left, window.innerWidth - TIP_W - 8))
    popup.style.left = left + 'px'
    popup.style.width = TIP_W + 'px'

    // Measure height after content set, then decide above/below
    popup.style.top = '-9999px'
    requestAnimationFrame(() => {
      const ph = popup.offsetHeight
      const above = rect.top - ph - GAP
      popup.style.top = (above >= 4 ? above : rect.bottom + GAP) + 'px'
      popup.classList.add('visible')
    })
  })

  document.addEventListener('mouseout', e => {
    const icon = e.target.closest('.tip-icon[data-tip]')
    if (!icon) return
    popup.classList.remove('visible')
    hideTimer = setTimeout(() => popup.classList.add('hidden'), 150)
  })
}

// ── State ─────────────────────────────────────────────────────────────────
let portfolio = null
let trades = []
let logs = []
let extraLogLines = []
let logClearedAt = Date.now()   // hide everything before dashboard opened; reset on bot start
let pnlChart = null
let catChart = null
let botRunning = false

// Sort state for positions table
let posSort = { col: null, dir: 'asc' }
// Sort state for trades table
let tradesSort = { col: null, dir: 'asc' }

// Category filter: hidden categories
let hiddenCategories = new Set()

// Consistent category → color mapping
const CAT_PALETTE = ['#3b82f6','#10b981','#f59e0b','#ef4444','#8b5cf6','#06b6d4','#f97316','#ec4899','#6366f1','#84cc16']
const catColorCache = {}
let catColorIdx = 0

function getCatColor(cat) {
  if (!catColorCache[cat]) catColorCache[cat] = CAT_PALETTE[catColorIdx++ % CAT_PALETTE.length]
  return catColorCache[cat]
}

// Normalize timestamps before parsing — .NET emits 7 fractional-second digits
// (e.g. "2024-01-15T10:30:45.1234567Z") which some JS engines reject.
function parseTs(ts) {
  if (!ts) return 0
  const normalized = String(ts).replace(/(\.\d{3})\d+/, '$1')
  return new Date(normalized).getTime() || 0
}

// ── Boot ──────────────────────────────────────────────────────────────────
async function init() {
  _settings = (await api.readSettings()) || {}
  currentLang = getSetting('lang', 'ru')

  const dataDir = await api.getDataDir()
  document.getElementById('data-dir-label').textContent = dataDir
  document.getElementById('cfg-datadir-val').textContent = dataDir

  initTheme()
  initLang()
  initTooltips()
  initCharts()
  await refresh()

  setInterval(refresh, 8000)

  api.onFileChanged(() => refresh())
  api.onBotOutput(line => {
    // Drop lines that predate a clear (shouldn't happen, but guards against races)
    if (parseTs(line.timestamp) <= logClearedAt) return
    extraLogLines.push(line)
    if (extraLogLines.length > 500) extraLogLines.shift()
    appendLogLine(line)
  })
  api.onBotStopped(({ code }) => {
    botRunning = false; updateBotStatusBadge()
    appendLogLine({ level: 'WARNING', message: t('botStopped', code), timestamp: new Date().toISOString() })
  })

  const status = await api.botStatus()
  botRunning = status.running
  updateBotStatusBadge()

  initModals()
  initSortHeaders()
  setupResize()
}

// ── Main refresh ──────────────────────────────────────────────────────────
async function refresh() {
  const [p, tr, l] = await Promise.all([api.readPortfolio(), api.readTrades(), api.readLogs(200)])
  portfolio = p
  trades = tr || []
  logs = l || []

  renderStats()
  renderCategoryFilters()
  renderPositions()
  renderRiskMeters()
  renderExitBreakdown()
  renderCharts()
  renderTrades()
  renderLog()

  document.getElementById('last-updated').textContent = t('updatedAt', new Date().toLocaleTimeString())
}

// ── Helpers ───────────────────────────────────────────────────────────────
const $ = id => document.getElementById(id)
const fmt$ = v => v >= 0 ? `+$${v.toFixed(2)}` : `-$${Math.abs(v).toFixed(2)}`
const fmtUsd = v => `$${v.toFixed(2)}`
const fmtPct2 = v => `${v >= 0 ? '+' : ''}${(v * 100).toFixed(1)}%`
const fmtAge = ts => {
  const s = Math.floor(Date.now() / 1000 - ts)
  if (s < 3600) return `${Math.floor(s/60)}m`
  if (s < 86400) return `${Math.floor(s/3600)}h`
  return `${Math.floor(s/86400)}d`
}
const fmtTime = ts => new Date(ts * 1000).toLocaleString(currentLang === 'ru' ? 'ru-RU' : 'en-US', { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })
const clamp01 = v => Math.min(100, Math.max(0, v))
const colorClass = v => v > 0 ? 'positive' : v < 0 ? 'negative' : 'neutral'
function setEl(id, html, cls) {
  const el = $(id); if (!el) return; el.innerHTML = html
  if (cls) el.className = el.className.replace(/\b(positive|negative|neutral|warning)\b/g, '') + ' ' + cls
}
function escHtml(s) {
  return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;')
}
function truncate(s, n) { return s && s.length > n ? s.slice(0, n) + '…' : s }

// ── Stats row ─────────────────────────────────────────────────────────────
function renderStats() {
  if (!portfolio) return
  const { bankroll, initial_bankroll, positions = [], total_realized_pnl, high_water_mark, total_trades, is_halted } = portfolio
  const totalExposure = positions.reduce((s, p) => s + p.shares * p.current_price, 0)
  const portVal = bankroll + totalExposure
  const unrealPnl = positions.reduce((s, p) => s + p.unrealized_pnl, 0)
  const drawdownPct = high_water_mark > 0 ? (high_water_mark - portVal) / high_water_mark : 0
  const { won, total: closedCount } = computeWinRate(trades)
  const winRate = closedCount > 0 ? won / closedCount : 0

  setEl('val-bankroll', fmtUsd(bankroll), bankroll < 1 ? 'negative' : bankroll < 5 ? 'warning' : 'neutral')
  setEl('sub-bankroll', t('subInitial', fmtUsd(initial_bankroll)))
  $('bar-bankroll').style.width = clamp01(bankroll / initial_bankroll * 100) + '%'

  setEl('val-portval', fmtUsd(portVal))
  setEl('sub-portval', t('subDeployed', fmtUsd(totalExposure)))
  $('bar-exposure').style.width = clamp01(portVal > 0 ? totalExposure / portVal * 100 : 0) + '%'

  setEl('val-realized', fmt$(total_realized_pnl), colorClass(total_realized_pnl))
  setEl('sub-realized', t('subTrades', total_trades))

  setEl('val-unrealized', fmt$(unrealPnl), colorClass(unrealPnl))
  setEl('sub-unrealized', t('subOpen', positions.length))

  const ddPct = drawdownPct * 100
  setEl('val-drawdown', `${ddPct.toFixed(1)}%`, ddPct > 30 ? 'negative' : ddPct > 15 ? 'warning' : 'neutral')
  setEl('sub-drawdown', t('subHwm', fmtUsd(high_water_mark)))
  const ddBar = $('bar-drawdown')
  ddBar.style.width = clamp01(ddPct / 50 * 100) + '%'
  ddBar.className = ddPct < 15 ? 'stat-bar bar-green' : ddPct < 30 ? 'stat-bar bar-amber' : 'stat-bar bar-red'

  setEl('val-winrate', closedCount > 0 ? `${(winRate * 100).toFixed(1)}%` : '—', winRate >= 0.5 ? 'positive' : winRate >= 0.35 ? 'warning' : closedCount > 0 ? 'negative' : 'neutral')
  setEl('sub-winrate', t('subClosed', won, closedCount))
  $('bar-winrate').style.width = clamp01(winRate * 100) + '%'

  $('halted-badge').classList.toggle('hidden', !is_halted)
}

// ── Category filters ──────────────────────────────────────────────────────
function renderCategoryFilters() {
  if (!portfolio?.positions?.length) { $('cat-filters').innerHTML = ''; return }
  const cats = [...new Set(portfolio.positions.map(p => p.category || 'other'))]
  $('cat-filters').innerHTML = cats.map(cat => {
    const color = getCatColor(cat)
    const off = hiddenCategories.has(cat)
    return `<button class="cat-pill ${off ? 'off' : ''}" data-cat="${escHtml(cat)}">
      <span class="cat-dot" style="background:${color}"></span>${escHtml(cat)}
    </button>`
  }).join('')
  $('cat-filters').querySelectorAll('.cat-pill').forEach(btn => {
    btn.addEventListener('click', () => {
      const cat = btn.dataset.cat
      hiddenCategories.has(cat) ? hiddenCategories.delete(cat) : hiddenCategories.add(cat)
      renderCategoryFilters()
      renderPositions()
    })
  })
}

// ── Trade value extractor ─────────────────────────────────────────────────
function getTradeVal(t, col) {
  switch (col) {
    case 'time':   return t.timestamp || ''
    case 'action': return t.action || ''
    case 'side':   return t.side || ''
    case 'market': return t.question || ''
    case 'price':  return t.price || 0
    case 'size':   return t.size_usd || 0
    case 'shares': return t.shares || 0
    case 'edge':   return t.edge_at_entry || 0
    case 'kelly':  return t.kelly_at_entry || 0
    case 'exit':   return t.exit_reason || ''
    case 'paper':  return t.is_paper ? 1 : 0
    default:       return 0
  }
}

// ── Positions table ───────────────────────────────────────────────────────
function getPosVal(p, col) {
  switch (col) {
    case 'market':   return p.question || ''
    case 'side':     return p.side
    case 'entry':    return p.entry_price
    case 'current':  return p.current_price
    case 'fair':     return p.fair_estimate_at_entry
    case 'shares':   return p.shares
    case 'cost':     return p.size_usd
    case 'value':    return p.shares * p.current_price
    case 'pnl':      return p.unrealized_pnl
    case 'pnlpct':   return p.entry_price > 0 ? (p.current_price - p.entry_price) / p.entry_price : 0
    case 'edge': {
      if (!p.fair_estimate_at_entry) return -Infinity
      return p.side === 'YES'
        ? p.fair_estimate_at_entry - p.current_price
        : (1 - p.fair_estimate_at_entry) - p.current_price
    }
    case 'category': return p.category || ''
    case 'age':      return p.opened_at
    default:         return 0
  }
}

function renderPositions() {
  const tbody = $('positions-body')
  if (!portfolio?.positions?.length) {
    tbody.innerHTML = `<tr><td colspan="13" class="empty-msg">${t('emptyPositions')}</td></tr>`
    $('positions-count').textContent = '0'
    return
  }

  let positions = portfolio.positions.filter(p => !hiddenCategories.has(p.category || 'other'))

  if (posSort.col) {
    positions = [...positions].sort((a, b) => {
      const va = getPosVal(a, posSort.col), vb = getPosVal(b, posSort.col)
      const cmp = typeof va === 'string' ? va.localeCompare(vb) : (va > vb ? 1 : va < vb ? -1 : 0)
      return posSort.dir === 'asc' ? cmp : -cmp
    })
  }

  $('positions-count').textContent = positions.length + (hiddenCategories.size ? ` (${portfolio.positions.length} total)` : '')

  tbody.innerHTML = positions.map(p => {
    const curVal = p.shares * p.current_price
    const pnlPct = p.entry_price > 0 ? (p.current_price - p.entry_price) / p.entry_price : 0
    const edge = p.fair_estimate_at_entry > 0
      ? (p.side === 'YES' ? p.fair_estimate_at_entry - p.current_price : (1 - p.fair_estimate_at_entry) - p.current_price)
      : null
    const catColor = getCatColor(p.category || 'other')
    const fairTxt = p.fair_estimate_at_entry > 0 ? p.fair_estimate_at_entry.toFixed(3) : '<span class="muted">—</span>'
    const edgeTxt = edge !== null
      ? `<span class="${edge >= 0 ? 'positive' : 'negative'}">${(edge * 100).toFixed(1)}%</span>`
      : '<span class="muted">—</span>'

    return `<tr>
      <td class="market-cell" title="${escHtml(p.question)}">${escHtml(truncate(p.question, 40))}</td>
      <td><span class="pill ${p.side === 'YES' ? 'pill-yes' : 'pill-no'}">${p.side}</span></td>
      <td>${p.entry_price.toFixed(4)}</td>
      <td>${p.current_price.toFixed(4)}</td>
      <td>${fairTxt}</td>
      <td>${p.shares.toFixed(2)}</td>
      <td>${fmtUsd(p.size_usd)}</td>
      <td>${fmtUsd(curVal)}</td>
      <td class="${colorClass(p.unrealized_pnl)}">${fmt$(p.unrealized_pnl)}</td>
      <td class="${colorClass(pnlPct)}">${fmtPct2(pnlPct)}</td>
      <td>${edgeTxt}</td>
      <td><div class="cat-cell"><span class="cat-dot" style="background:${catColor}"></span><span class="muted" style="font-size:9px">${escHtml(p.category || 'other')}</span></div></td>
      <td class="muted">${fmtAge(p.opened_at)}</td>
    </tr>`
  }).join('')
}

// ── Sort headers ──────────────────────────────────────────────────────────
function initSortHeaders() {
  document.querySelectorAll('#positions-table .th-sort').forEach(th => {
    th.addEventListener('click', () => {
      const col = th.dataset.sort
      if (posSort.col === col) posSort.dir = posSort.dir === 'asc' ? 'desc' : 'asc'
      else posSort = { col, dir: 'asc' }
      document.querySelectorAll('#positions-table .th-sort').forEach(h => h.classList.remove('sort-asc', 'sort-desc'))
      th.classList.add('sort-' + posSort.dir)
      renderPositions()
    })
  })

  document.querySelectorAll('#trades-table .th-sort').forEach(th => {
    th.addEventListener('click', () => {
      const col = th.dataset.sort
      if (tradesSort.col === col) tradesSort.dir = tradesSort.dir === 'asc' ? 'desc' : 'asc'
      else tradesSort = { col, dir: 'asc' }
      document.querySelectorAll('#trades-table .th-sort').forEach(h => h.classList.remove('sort-asc', 'sort-desc'))
      th.classList.add('sort-' + tradesSort.dir)
      renderTrades()
    })
  })
}

// ── Risk meters ───────────────────────────────────────────────────────────
function renderRiskMeters() {
  const container = $('risk-container')
  if (!portfolio) { container.innerHTML = `<div class="muted small" style="padding:10px">${t('emptyWaiting')}</div>`; return }
  const { bankroll, positions = [], high_water_mark, daily_start_value } = portfolio
  const totalExposure = positions.reduce((s, p) => s + p.shares * p.current_price, 0)
  const portVal = bankroll + totalExposure
  const maxPos = positions.length > 0 ? Math.max(...positions.map(p => p.size_usd)) : 0
  const drawdown = high_water_mark > 0 ? (high_water_mark - portVal) / high_water_mark : 0
  const dailyLoss = daily_start_value > 0 ? Math.max(0, (daily_start_value - portVal) / daily_start_value) : 0

  const metrics = [
    { label: t('riskTotalExposure'), val: portVal > 0 ? totalExposure / portVal : 0, limit: 1.00, fmt: v => `${(v*100).toFixed(0)}% / 100%` },
    { label: t('riskLargestPos'),   val: portVal > 0 ? maxPos / portVal : 0,         limit: 0.15, fmt: v => `${(v*100).toFixed(1)}% / 15%` },
    { label: t('riskDailyLoss'),    val: dailyLoss,                                   limit: 0.20, fmt: v => `${(v*100).toFixed(1)}% / 20%` },
    { label: t('riskMaxDD'),        val: drawdown,                                    limit: 0.50, fmt: v => `${(v*100).toFixed(1)}% / 50%` },
    { label: t('riskFreeCash'),     val: portVal > 0 ? bankroll / portVal : 0,        limit: null, fmt: () => `${fmtUsd(bankroll)} / ${fmtUsd(portVal)}` },
    { label: t('riskPositions'),    val: positions.length / 20,                       limit: null, fmt: () => `${positions.length} / 20` },
  ]

  container.innerHTML = metrics.map(m => {
    const frac = m.limit ? m.val / m.limit : m.val
    const pct = clamp01(frac * 100)
    const cls = m.limit ? (frac > 0.85 ? 'risk-crit' : frac > 0.6 ? 'risk-warn' : 'risk-ok') : 'risk-ok'
    const valCls = m.limit ? (frac > 0.85 ? 'negative' : frac > 0.6 ? 'warning' : 'positive') : 'neutral'
    return `<div class="risk-item">
      <div class="risk-row">
        <span class="risk-label">${m.label}</span>
        <span class="risk-val ${valCls}">${m.fmt(m.val)}</span>
      </div>
      <div class="risk-bar-wrap"><div class="risk-bar ${cls}" style="width:${pct}%"></div></div>
    </div>`
  }).join('')
}

// ── Exit breakdown ────────────────────────────────────────────────────────
const EXIT_COLORS = { stop_loss:'#ef4444', take_profit:'#10b981', edge_gone:'#f59e0b', resolved_won:'#06b6d4', resolved_lost:'#ef4444', top_up_sell:'#8b5cf6', reestimate_exit:'#f59e0b' }

function renderExitBreakdown() {
  const container = $('exit-stats')
  const sells = trades.filter(t => t.action === 'SELL' && t.exit_reason)
  if (!sells.length) { container.innerHTML = `<div class="muted small" style="padding:8px">${t('emptyNoTrades')}</div>`; return }
  const counts = {}
  sells.forEach(t => { const r = t.exit_reason || 'unknown'; counts[r] = (counts[r] || 0) + 1 })
  const total = sells.length
  container.innerHTML = Object.entries(counts).sort((a,b) => b[1]-a[1]).map(([reason, count]) => {
    const pct = count / total
    const color = EXIT_COLORS[reason] || '#4a5f7a'
    const EXIT_LABELS = { stop_loss: t('exitStopLoss'), take_profit: t('exitTakeProfit'), edge_gone: t('exitEdgeGone'), resolved_won: t('exitResolvedWon'), resolved_lost: t('exitResolvedLost'), top_up_sell: t('exitTopUpSell'), reestimate_exit: t('exitReestimate') }
    return `<div class="exit-row">
      <span class="exit-label">${EXIT_LABELS[reason] || reason.replace(/_/g,' ')}</span>
      <span class="exit-count">${count}</span>
      <span class="exit-pct">${(pct*100).toFixed(0)}%</span>
      <div class="exit-bar-wrap"><div class="exit-bar" style="width:${pct*100}%;background:${color}"></div></div>
    </div>`
  }).join('')
}

// ── Charts: init once, update in place ───────────────────────────────────
function initCharts() {
  const pnlCtx = $('pnl-chart').getContext('2d')
  pnlChart = new Chart(pnlCtx, {
    type: 'line',
    data: { labels: [], datasets: [{ label: 'Cum. P&L ($)', data: [], borderColor: '#ef4444', backgroundColor: 'rgba(239,68,68,0.08)', borderWidth: 2, pointRadius: 3, pointHoverRadius: 5, fill: true, tension: 0.3 }] },
    options: {
      ...baseChartOpts('$'),
      animation: false,
    },
  })

  const catCtx = $('cat-chart').getContext('2d')
  catChart = new Chart(catCtx, {
    type: 'doughnut',
    data: { labels: [], datasets: [{ data: [], backgroundColor: [], borderColor: '#141b2d', borderWidth: 2, hoverOffset: 6 }] },
    options: {
      animation: false,
      responsive: true,
      maintainAspectRatio: false,
      plugins: {
        legend: { position: 'right', labels: { color: '#7a8fa8', font: { family: 'monospace', size: 10 }, padding: 8, boxWidth: 10 } },
        tooltip: { callbacks: { label: ctx => ` $${ctx.parsed.toFixed(2)}` }, backgroundColor: '#141b2d', titleColor: '#d4dff0', bodyColor: '#7a8fa8', borderColor: '#1e2d45', borderWidth: 1 },
      },
      cutout: '65%',
    },
  })
}

function renderCharts() {
  renderPnlChart()
  renderCatChart()
}

function buildPnlTimeline() {
  if (!trades.length) return []
  const sorted = [...trades].sort((a, b) => a.timestamp - b.timestamp)
  const queues = {}
  const points = []
  let cumPnl = 0
  for (const t of sorted) {
    const key = t.condition_id + ':' + t.side
    if (t.action === 'BUY') { if (!queues[key]) queues[key] = []; queues[key].push(t) }
    else if (t.action === 'SELL') {
      const cost = (queues[key] || []).reduce((s, b) => s + b.size_usd, 0)
      // resolved trades store size_usd = original buy cost, so compute true P&L from shares
      let tradePnl
      if (t.exit_reason === 'resolved_won') tradePnl = (t.shares || 0) - cost
      else if (t.exit_reason === 'resolved_lost') tradePnl = -cost
      else tradePnl = t.size_usd - cost
      cumPnl += tradePnl
      points.push({ ts: t.timestamp, pnl: cumPnl })
      delete queues[key]
    }
  }
  return points
}

function renderPnlChart() {
  const points = buildPnlTimeline()
  const labels = points.map(p => new Date(p.ts * 1000).toLocaleDateString('en-US', { month: 'short', day: 'numeric' }))
  const data = points.map(p => parseFloat(p.pnl.toFixed(2)))
  const finalVal = data[data.length - 1] ?? 0
  const lineColor = finalVal >= 0 ? 'rgba(16,185,129,1)' : 'rgba(239,68,68,1)'
  const fillColor = finalVal >= 0 ? 'rgba(16,185,129,0.08)' : 'rgba(239,68,68,0.08)'

  pnlChart.data.labels = labels
  pnlChart.data.datasets[0].data = data
  pnlChart.data.datasets[0].borderColor = lineColor
  pnlChart.data.datasets[0].backgroundColor = fillColor
  pnlChart.data.datasets[0].pointRadius = data.length < 30 ? 3 : 0
  pnlChart.update('none')
}

function renderCatChart() {
  if (!portfolio?.positions?.length) {
    catChart.data.labels = []; catChart.data.datasets[0].data = []; catChart.data.datasets[0].backgroundColor = []
    catChart.update('none'); return
  }
  const catMap = {}
  for (const p of portfolio.positions) {
    const cat = p.category || 'other'
    catMap[cat] = (catMap[cat] || 0) + p.shares * p.current_price
  }
  const entries = Object.entries(catMap).sort((a, b) => b[1] - a[1])
  catChart.data.labels = entries.map(([cat]) => cat)
  catChart.data.datasets[0].data = entries.map(([, v]) => parseFloat(v.toFixed(2)))
  catChart.data.datasets[0].backgroundColor = entries.map(([cat]) => getCatColor(cat))
  catChart.update('none')
}

function baseChartOpts(prefix = '') {
  return {
    responsive: true,
    maintainAspectRatio: false,
    interaction: { mode: 'index', intersect: false },
    plugins: {
      legend: { display: false },
      tooltip: { callbacks: { label: ctx => ` ${prefix}${ctx.parsed.y?.toFixed(2)}` }, backgroundColor: '#141b2d', titleColor: '#d4dff0', bodyColor: '#7a8fa8', borderColor: '#1e2d45', borderWidth: 1 },
    },
    scales: {
      x: { ticks: { color: '#4a5f7a', font: { family: 'monospace', size: 9 }, maxTicksLimit: 8 }, grid: { color: 'rgba(30,45,69,0.5)' } },
      y: { ticks: { color: '#4a5f7a', font: { family: 'monospace', size: 9 }, callback: v => prefix + v.toFixed(0) }, grid: { color: 'rgba(30,45,69,0.5)' } },
    },
  }
}

// ── Trade history ─────────────────────────────────────────────────────────
function renderTrades() {
  const tbody = $('trades-body')
  $('trades-count').textContent = trades.length
  if (!trades.length) { tbody.innerHTML = `<tr><td colspan="11" class="empty-msg">${t('emptyTrades')}</td></tr>`; return }
  let sorted = [...trades]
  if (tradesSort.col) {
    sorted.sort((a, b) => {
      const va = getTradeVal(a, tradesSort.col), vb = getTradeVal(b, tradesSort.col)
      const cmp = typeof va === 'string' ? va.localeCompare(vb) : (va > vb ? 1 : va < vb ? -1 : 0)
      return tradesSort.dir === 'asc' ? cmp : -cmp
    })
  } else {
    sorted.sort((a, b) => String(b.timestamp).localeCompare(String(a.timestamp)))
  }
  const recent = sorted.slice(0, 500)
  tbody.innerHTML = recent.map(t => {
    const isBuy = t.action === 'BUY'
    const exitTxt = t.exit_reason
      ? `<span style="color:${EXIT_COLORS[t.exit_reason]||'#4a5f7a'};font-size:9px">${t.exit_reason.replace(/_/g,' ')}</span>`
      : '<span class="muted">—</span>'
    return `<tr>
      <td class="muted">${fmtTime(t.timestamp)}</td>
      <td><span class="pill ${isBuy ? 'pill-buy' : 'pill-sell'}">${t.action}</span></td>
      <td><span class="pill ${t.side === 'YES' ? 'pill-yes' : 'pill-no'}">${t.side}</span></td>
      <td class="market-cell" title="${escHtml(t.question)}">${escHtml(truncate(t.question, 35))}</td>
      <td>${t.price.toFixed(4)}</td>
      <td>${fmtUsd(t.size_usd)}</td>
      <td>${t.shares.toFixed(1)}</td>
      <td>${t.edge_at_entry > 0 ? (t.edge_at_entry*100).toFixed(1)+'%' : '<span class="muted">—</span>'}</td>
      <td>${t.kelly_at_entry > 0 ? (t.kelly_at_entry*100).toFixed(1)+'%' : '<span class="muted">—</span>'}</td>
      <td>${exitTxt}</td>
      <td><span class="pill ${t.is_paper ? 'pill-paper' : 'pill-live'}">${t.is_paper ? 'PAPER' : 'LIVE'}</span></td>
    </tr>`
  }).join('')
}

// ── Log ───────────────────────────────────────────────────────────────────
function renderLog() {
  const container = $('log-container')
  const autoscroll = $('log-autoscroll').checked
  const visible = logs.filter(l => parseTs(l.timestamp) > logClearedAt)
  container.innerHTML = visible.slice(-200).map(formatLogLine).join('')
  if (autoscroll) container.scrollTop = container.scrollHeight
}

function appendLogLine(line) {
  const container = $('log-container')
  const autoscroll = $('log-autoscroll').checked
  const div = document.createElement('div')
  div.innerHTML = formatLogLine(line)
  container.appendChild(div.firstChild)
  while (container.children.length > 400) container.removeChild(container.firstChild)
  if (autoscroll) container.scrollTop = container.scrollHeight
}

function formatLogLine(entry) {
  const ts = new Date(entry.timestamp).toLocaleTimeString('en-US', { hour12: false })
  const lvl = (entry.level || 'INFO').toUpperCase().substring(0, 8).padEnd(8)
  const msg = escHtml(entry.message || '')
    .replace(/\b(BUY(?:ING)?)\b/g, '<span class="log-buy">$1</span>')
    .replace(/\b(SELL(?:ING)?)\b/g, '<span class="log-sell">$1</span>')
  return `<div class="log-line log-${entry.level||'INFO'}">
    <span class="log-ts">${ts}</span>
    <span class="log-lvl">${lvl}</span>
    <span class="log-msg">${msg}</span>
  </div>`
}

// ── Export log ────────────────────────────────────────────────────────────
async function exportLog() {
  // Only export what is currently visible — both sources filtered by logClearedAt
  const isVisible = l => parseTs(l.timestamp) > logClearedAt
  const allLines = [...logs.filter(isVisible), ...extraLogLines.filter(isVisible)]
    .sort((a, b) => parseTs(a.timestamp) - parseTs(b.timestamp))
    .map(l => `${l.timestamp}\t${(l.level||'').padEnd(8)}\t${l.message||''}`)
    .join('\n')
  const defaultName = `bot-log-${new Date().toISOString().slice(0,19).replace(/:/g,'-')}.txt`
  await api.saveFile({ content: allLines, defaultName })
}

// ── Win rate ──────────────────────────────────────────────────────────────
function computeWinRate(tradeList) {
  const queues = {}; let won = 0, total = 0
  for (const t of [...tradeList].sort((a,b) => a.timestamp - b.timestamp)) {
    const key = t.condition_id + ':' + t.side
    if (t.action === 'BUY') { if (!queues[key]) queues[key] = []; queues[key].push(t) }
    else if (t.action === 'SELL') {
      const cost = (queues[key] || []).reduce((s, b) => s + b.size_usd, 0)
      // resolved trades store size_usd = original buy cost, so use exit_reason to determine outcome
      const isWin = t.exit_reason === 'resolved_won' || t.exit_reason === 'take_profit' || t.size_usd > cost
      if (isWin) won++; total++; delete queues[key]
    }
  }
  return { won, total }
}

// ── Bot status ────────────────────────────────────────────────────────────
function updateBotStatusBadge() {
  const badge = $('bot-status-badge'), btn = $('btn-start-stop')
  if (botRunning) {
    badge.textContent = t('statusRunning'); badge.className = 'badge badge-green'
    btn.textContent = t('stopBtn'); btn.className = 'btn btn-danger'
  } else {
    badge.textContent = t('statusStopped'); badge.className = 'badge badge-gray'
    btn.textContent = t('startBtn'); btn.className = 'btn btn-success'
  }
}

// ── Resize handles ────────────────────────────────────────────────────────
function setupResize() {
  const grid  = $('main-grid')
  const upper = $('right-upper')

  // Restore saved sizes
  const savedW = getSetting('panel-left-w', null)
  if (savedW) grid.style.gridTemplateColumns = `${savedW}px 6px 1fr`
  const savedH = getSetting('panel-upper-h', null)
  if (savedH) upper.style.height = `${savedH}px`

  // Horizontal: left-col / right-col
  let lastW = null
  dragResize($('rh-main'), false,
    delta => {
      lastW = Math.max(600, $('left-col').offsetWidth + delta)
      grid.style.gridTemplateColumns = `${lastW}px 6px 1fr`
    },
    () => { if (lastW !== null) setSetting('panel-left-w', lastW) }
  )

  // Vertical: right-upper / log
  let lastH = null
  dragResize($('rh-right'), true,
    delta => {
      lastH = Math.max(80, upper.offsetHeight + delta)
      upper.style.height = `${lastH}px`
    },
    () => { if (lastH !== null) setSetting('panel-upper-h', lastH) }
  )
}

function dragResize(handle, vertical, onDelta, onDone) {
  let active = false, last = 0
  handle.addEventListener('mousedown', e => {
    active = true; last = vertical ? e.clientY : e.clientX
    handle.classList.add('rh-active')
    document.body.style.cursor = vertical ? 'row-resize' : 'col-resize'
    document.body.style.userSelect = 'none'
    e.preventDefault()
  })
  document.addEventListener('mousemove', e => {
    if (!active) return
    const pos = vertical ? e.clientY : e.clientX
    onDelta(pos - last); last = pos
  })
  document.addEventListener('mouseup', () => {
    if (!active) return
    active = false; handle.classList.remove('rh-active')
    document.body.style.cursor = ''; document.body.style.userSelect = ''
    if (onDone) onDone()
  })
}

// ── Config modal ──────────────────────────────────────────────────────────
const CONFIG_SCHEMA = [
  { section: 'CORE', ru: 'ОСНОВНОЕ', fields: [
    { key: 'live_trading',     label: 'Live Trading',     ru: 'Боевой режим',     type: 'bool', danger: true },
    { key: 'initial_bankroll', label: 'Initial Bankroll', ru: 'Начальный баланс', type: 'number', step: 1 },
  ]},
  { section: 'AI PROVIDER', ru: 'ИИ ПРОВАЙДЕР', fields: [
    { key: 'ai_provider',   label: 'Active Provider (single mode)', ru: 'Провайдер (один)',       type: 'provider-select' },
    { key: 'multi_provider',label: 'Query All Providers',           ru: 'Опросить все провайдеры', type: 'bool' },
  ]},
  { section: 'ANTHROPIC', ru: 'ANTHROPIC', fields: [
    { key: 'anthropic_enabled',  label: 'Enabled',  ru: 'Включён',   type: 'bool' },
    { key: 'anthropic_api_key',  label: 'API Key',  ru: 'API ключ',  type: 'password' },
    { key: 'anthropic_api_host', label: 'API Host', ru: 'API хост',  type: 'text' },
    { key: 'anthropic_model',    label: 'Model',    ru: 'Модель',    type: 'model-select', loadFrom: 'anthropic' },
  ]},
  { section: 'OPENAI', ru: 'OPENAI', fields: [
    { key: 'openai_enabled',  label: 'Enabled',  ru: 'Включён',   type: 'bool' },
    { key: 'openai_api_key',  label: 'API Key',  ru: 'API ключ', type: 'password' },
    { key: 'openai_api_host', label: 'API Host', ru: 'API хост', type: 'text' },
    { key: 'openai_model',    label: 'Model',    ru: 'Модель',   type: 'model-select', loadFrom: 'openai' },
  ]},
  { section: 'GEMINI', ru: 'GEMINI', fields: [
    { key: 'gemini_enabled',  label: 'Enabled',  ru: 'Включён',   type: 'bool' },
    { key: 'gemini_api_key',  label: 'API Key',  ru: 'API ключ', type: 'password' },
    { key: 'gemini_api_host', label: 'API Host', ru: 'API хост', type: 'text' },
    { key: 'gemini_model',    label: 'Model',    ru: 'Модель',   type: 'model-select', loadFrom: 'gemini' },
  ]},
  { section: 'OPENROUTER', ru: 'OPENROUTER', fields: [
    { key: 'openrouter_enabled',  label: 'Enabled',  ru: 'Включён',   type: 'bool' },
    { key: 'openrouter_api_key',  label: 'API Key',  ru: 'API ключ', type: 'password' },
    { key: 'openrouter_api_host', label: 'API Host', ru: 'API хост', type: 'text' },
    { key: 'openrouter_model',    label: 'Model',    ru: 'Модель',   type: 'model-select', loadFrom: 'openrouter' },
  ]},
  { section: 'AZURE OPENAI', ru: 'AZURE OPENAI', fields: [
    { key: 'azure_openai_enabled',     label: 'Enabled',      ru: 'Включён',       type: 'bool' },
    { key: 'azure_openai_api_key',     label: 'API Key',      ru: 'API ключ',      type: 'password' },
    { key: 'azure_openai_endpoint',    label: 'Endpoint',     ru: 'Эндпоинт',      type: 'text' },
    { key: 'azure_openai_deployment',  label: 'Deployment',   ru: 'Деплоймент',    type: 'text' },
    { key: 'azure_openai_api_version', label: 'API Version',  ru: 'Версия API',    type: 'text' },
  ]},
  { section: 'API KEYS', ru: 'API КЛЮЧИ', fields: [
    { key: 'polymarket_private_key',    label: 'PK Private Key',      ru: 'Приватный ключ',      type: 'password' },
    { key: 'polymarket_funder_address', label: 'Funder Address',      ru: 'Адрес фондирования',  type: 'text' },
    { key: 'polymarket_api_key',        label: 'CLOB API Key',        ru: 'CLOB API ключ',       type: 'password' },
    { key: 'polymarket_api_secret',     label: 'CLOB API Secret',     ru: 'CLOB API секрет',     type: 'password' },
    { key: 'polymarket_api_passphrase', label: 'CLOB Passphrase',     ru: 'CLOB пароль',         type: 'password' },
    { key: 'polymarket_chain_id',       label: 'Chain ID',            ru: 'Chain ID',            type: 'number', step: 1 },
    { key: 'polymarket_signature_type', label: 'Signature Type',      ru: 'Тип подписи',         type: 'number', step: 1 },
  ]},
  { section: 'ENDPOINTS', ru: 'ЭНДПОИНТЫ', fields: [
    { key: 'gamma_api_host',            label: 'Gamma API Host',      ru: 'Хост Gamma API',      type: 'text' },
    { key: 'clob_host',                 label: 'CLOB Host',           ru: 'Хост CLOB',           type: 'text' },
    { key: 'exchange_address',          label: 'Exchange Address',    ru: 'Адрес биржи',         type: 'text' },
    { key: 'neg_risk_exchange_address', label: 'Neg Risk Exchange',   ru: 'Neg Risk адрес',      type: 'text' },
  ]},
  { section: 'SCANNING', ru: 'СКАНИРОВАНИЕ', fields: [
    { key: 'scan_interval_minutes',           label: 'Scan Interval (min)',       ru: 'Интервал сканирования (мин)', type: 'number', step: 1 },
    { key: 'min_liquidity',                   label: 'Min Liquidity ($)',          ru: 'Мин. ликвидность ($)',        type: 'number', step: 100 },
    { key: 'min_volume_24hr',                label: 'Min 24h Volume ($)',         ru: 'Мин. объём 24ч ($)',          type: 'number', step: 100 },
    { key: 'min_time_to_resolution_hours',   label: 'Min Time to Resolution (h)', ru: 'Мин. время до завершения (ч)', type: 'number', step: 1 },
    { key: 'min_market_price',               label: 'Min Market Price',           ru: 'Мин. цена рынка',            type: 'number', step: 0.01 },
    { key: 'markets_per_cycle',              label: 'Markets Per Cycle',          ru: 'Рынков за цикл',             type: 'number', step: 1 },
  ]},
  { section: 'ESTIMATION', ru: 'ОЦЕНКА', fields: [
    { key: 'ensemble_size',        label: 'Ensemble Size',   ru: 'Размер ансамбля',  type: 'number', step: 1 },
    { key: 'ensemble_temperature', label: 'Temperature',     ru: 'Температура',      type: 'number', step: 0.1 },
    { key: 'max_estimate_tokens',  label: 'Max Tokens',      ru: 'Макс. токенов',    type: 'number', step: 64 },
    { key: 'max_estimate_std',     label: 'Max Std Dev',     ru: 'Макс. разброс',    type: 'number', step: 0.01 },
    { key: 'max_cycle_api_cost_usd', label: 'Cycle API Budget $', ru: 'API бюджет цикла $', type: 'number', step: 0.05 },
    { key: 'max_daily_api_cost_usd', label: 'Daily API Budget $', ru: 'API бюджет дня $',    type: 'number', step: 0.50 },
  ]},
  { section: 'SIZING & RISK', ru: 'РАЗМЕРЫ И РИСКИ', fields: [
    { key: 'kelly_fraction',            label: 'Kelly Fraction',    ru: 'Доля Келли',           type: 'number', step: 0.05 },
    { key: 'min_edge',                  label: 'Min Edge',          ru: 'Мин. преимущество',    type: 'number', step: 0.01 },
    { key: 'min_trade_usd',            label: 'Min Trade ($)',     ru: 'Мин. сделка ($)',      type: 'number', step: 0.1 },
    { key: 'entry_price_buffer',        label: 'Entry Buffer',      ru: 'Буфер входа',          type: 'number', step: 0.01 },
    { key: 'max_live_order_bankroll_pct', label: 'Max Live Order %', ru: 'Макс. live ордер %',   type: 'number', step: 0.05 },
    { key: 'max_position_pct',          label: 'Max Position %',   ru: 'Макс. позиция %',      type: 'number', step: 0.01 },
    { key: 'max_total_exposure_pct',    label: 'Max Exposure %',   ru: 'Макс. открытые %',     type: 'number', step: 0.05 },
    { key: 'max_category_exposure_pct', label: 'Max Category %',   ru: 'Макс. категория %',    type: 'number', step: 0.05 },
    { key: 'daily_stop_loss_pct',       label: 'Daily Stop-Loss %',ru: 'Дневной стоп-лосс %',  type: 'number', step: 0.01 },
    { key: 'max_drawdown_pct',          label: 'Max Drawdown %',   ru: 'Макс. просадка %',     type: 'number', step: 0.01 },
    { key: 'max_concurrent_positions',  label: 'Max Positions',    ru: 'Макс. позиций',        type: 'number', step: 1 },
    { key: 'allow_unsafe_risk',         label: 'Allow Unsafe Risk', ru: 'Разрешить риск',       type: 'bool' },
  ]},
  { section: 'EXIT RULES', ru: 'ПРАВИЛА ВЫХОДА', fields: [
    { key: 'enable_position_review',           label: 'Enable Position Review',   ru: 'Мониторинг позиций',     type: 'bool' },
    { key: 'position_stop_loss_pct',           label: 'Position Stop-Loss %',     ru: 'Стоп-лосс позиции %',   type: 'number', step: 0.01 },
    { key: 'take_profit_price',               label: 'Take-Profit Price',        ru: 'Цена тейк-профита',      type: 'number', step: 0.01 },
    { key: 'exit_edge_buffer',                label: 'Edge-Gone Buffer',         ru: 'Буфер выхода по грани',  type: 'number', step: 0.01 },
    { key: 'review_reestimate_threshold_pct', label: 'Re-estimate Threshold %',  ru: 'Порог переоценки %',     type: 'number', step: 0.01 },
    { key: 'review_ensemble_size',            label: 'Review Ensemble Size',     ru: 'Ансамбль переоценки',    type: 'number', step: 1 },
    { key: 'stop_loss_requires_negative_edge', label: 'Confirm Stop-Loss',       ru: 'Подтверждать стоп',      type: 'bool' },
  ]},
  { section: 'EMAIL', ru: 'ПОЧТА', fields: [
    { key: 'email_enabled',   label: 'Email Enabled',  ru: 'Email включён',  type: 'bool' },
    { key: 'email_smtp_host', label: 'SMTP Host',      ru: 'SMTP хост',      type: 'text' },
    { key: 'email_smtp_port', label: 'SMTP Port',      ru: 'SMTP порт',      type: 'number', step: 1 },
    { key: 'email_use_tls',   label: 'Use TLS',        ru: 'Использовать TLS', type: 'bool' },
    { key: 'email_user',      label: 'Email User',     ru: 'Email адрес',    type: 'text' },
    { key: 'email_password',  label: 'Email Password', ru: 'Email пароль',   type: 'password' },
    { key: 'email_to',        label: 'Email To',       ru: 'Получатель',     type: 'text' },
  ]},
]

let currentConfig = {}

const PROVIDER_NAMES = {
  anthropic:    'Anthropic (Claude)',
  openai:       'OpenAI',
  gemini:       'Google Gemini',
  openrouter:   'OpenRouter',
  azure_openai: 'Azure OpenAI',
}

const PROVIDER_KEY_FIELDS = {
  anthropic:    'anthropic_api_key',
  openai:       'openai_api_key',
  gemini:       'gemini_api_key',
  openrouter:   'openrouter_api_key',
  azure_openai: 'azure_openai_api_key',
}

const PROVIDER_HOST_FIELDS = {
  openai:       'openai_api_host',
  gemini:       'gemini_api_host',
  openrouter:   'openrouter_api_host',
  azure_openai: 'azure_openai_endpoint',
}

function updateProviderVisibility(form, provider) {
  form.querySelectorAll('[data-providers]').forEach(el => {
    const allowed = el.dataset.providers.split(',')
    el.style.display = allowed.includes(provider) ? '' : 'none'
  })
}

async function openConfig() {
  currentConfig = await api.readConfig()
  const form = $('config-form')
  form.innerHTML = ''

  for (const s of CONFIG_SCHEMA) {
    const sec = document.createElement('div'); sec.className = 'config-section'
    sec.innerHTML = `<div class="config-section-title">${currentLang === 'ru' && s.ru ? s.ru : s.section}</div>`
    const grid = document.createElement('div'); grid.className = 'config-grid'

    for (const f of s.fields) {
      const val = currentConfig[f.key]
      const group = document.createElement('div'); group.className = 'form-group'
      const flabel = currentLang === 'ru' && f.ru ? f.ru : f.label

      // Provider-specific fields: tag with data-providers for show/hide
      if (f.providers) {
        group.dataset.providers = f.providers.join(',')
      }

      if (f.type === 'provider-select') {
        group.innerHTML = `<label class="form-label">${flabel}</label>
          <select class="form-input" data-key="${f.key}">${
            Object.entries(PROVIDER_NAMES).map(([k, v]) =>
              `<option value="${k}" ${(val || 'anthropic') === k ? 'selected' : ''}>${v}</option>`
            ).join('')
          }</select>`
        const sel = group.querySelector('select')
        sel.addEventListener('change', () => updateProviderVisibility(form, sel.value))

      } else if (f.type === 'model-select') {
        const currentModel = val || currentConfig.claude_model || ''
        group.innerHTML = `<label class="form-label">${flabel}</label>
          <div style="display:flex;gap:6px;align-items:center">
            <select class="form-input" data-key="${f.key}" style="flex:1;min-width:0">
              <option value="${escHtml(currentModel)}" selected>${escHtml(currentModel) || '(enter or load)'}</option>
            </select>
            <button class="btn btn-secondary btn-sm" id="load-models-btn" style="white-space:nowrap;flex-shrink:0">↺ Load</button>
          </div>`
        const btn = group.querySelector('#load-models-btn')
        const sel = group.querySelector('select')
        // loadFrom = fixed provider for this field; fallback = currently selected provider
        const fieldProvider = f.loadFrom || null
        btn.addEventListener('click', async () => {
          btn.textContent = '⟳'; btn.disabled = true
          const provider = fieldProvider || form.querySelector('[data-key="ai_provider"]')?.value || 'anthropic'
          // Always use the key/host fields for the specific provider being loaded
          const keyField = PROVIDER_KEY_FIELDS[provider] || 'anthropic_api_key'
          const hostField = PROVIDER_HOST_FIELDS[provider] || null
          const apiKey = document.querySelector(`[data-key="${keyField}"]`)?.value || ''
          const host = hostField ? (document.querySelector(`[data-key="${hostField}"]`)?.value || '') : ''
          const deployment = document.querySelector('[data-key="azure_openai_deployment"]')?.value || ''
          const apiVersion = document.querySelector('[data-key="azure_openai_api_version"]')?.value || ''
          try {
            const result = await api.fetchAiModels({ provider, apiKey, host, deployment, apiVersion })
            if (result.error) { btn.textContent = '✗'; setTimeout(() => { btn.textContent = '↺ Load'; btn.disabled = false }, 2000); return }
            const prev = sel.value
            sel.innerHTML = result.models.map(m =>
              `<option value="${escHtml(m.id)}" ${m.id === prev ? 'selected' : ''}>${escHtml(m.name)}</option>`
            ).join('')
            // Keep current value even if not in list
            if (prev && !result.models.find(m => m.id === prev))
              sel.insertAdjacentHTML('afterbegin', `<option value="${escHtml(prev)}" selected>${escHtml(prev)}</option>`)
            btn.textContent = `✓ ${result.models.length}`
          } catch { btn.textContent = '✗' }
          setTimeout(() => { btn.textContent = '↺ Load'; btn.disabled = false }, 2000)
        })

      } else if (f.type === 'bool') {
        const checked = Boolean(val)
        group.innerHTML = `<div class="form-toggle-row">
          <label class="form-label">${flabel}</label>
          <label class="toggle-switch">
            <input type="checkbox" data-key="${f.key}" ${checked ? 'checked' : ''}>
            <span class="toggle-slider"></span>
          </label></div>`
        if (f.danger) {
          const cb = group.querySelector('input')
          cb.addEventListener('change', () => {
            if (cb.checked && !confirm('⚠️  LIVE TRADING will place REAL orders with REAL money.\n\nAre you sure?')) cb.checked = false
          })
        }
      } else {
        group.innerHTML = `<label class="form-label">${flabel}</label>
          <input class="form-input" type="${f.type === 'password' ? 'password' : f.type === 'number' ? 'number' : 'text'}"
            data-key="${f.key}" value="${escHtml(String(val ?? ''))}" step="${f.step || 'any'}" autocomplete="off"
            ${f.danger ? 'data-danger="true"' : ''}>`
        if (f.type === 'password') {
          const inp = group.querySelector('input')
          const eye = document.createElement('button')
          eye.className = 'btn btn-ghost btn-xs'; eye.style.marginTop = '3px'; eye.textContent = '👁 show'
          eye.addEventListener('click', () => { inp.type = inp.type === 'password' ? 'text' : 'password'; eye.textContent = inp.type === 'password' ? '👁 show' : '🙈 hide' })
          group.appendChild(eye)
        }
      }
      grid.appendChild(group)
    }
    sec.appendChild(grid); form.appendChild(sec)
  }

  // Apply initial provider visibility
  const initialProvider = currentConfig.ai_provider || 'anthropic'
  updateProviderVisibility(form, initialProvider)

  $('config-modal').classList.remove('hidden')
}

async function saveConfig() {
  const newConfig = { ...currentConfig }
  for (const { fields } of CONFIG_SCHEMA) {
    for (const f of fields) {
      const el = document.querySelector(`[data-key="${f.key}"]`); if (!el) continue
      if (f.type === 'bool') newConfig[f.key] = el.checked
      else if (f.type === 'number') newConfig[f.key] = parseFloat(el.value)
      else newConfig[f.key] = el.value   // text, password, provider-select, model-select
    }
  }
  await api.writeConfig(newConfig)
  $('config-modal').classList.add('hidden')
}

// ── Start modal ───────────────────────────────────────────────────────────
async function startBot() {
  if (botRunning) {
    if (confirm('Stop the running bot?')) { await api.stopBot(); botRunning = false; updateBotStatusBadge() }
    return
  }
  // Restore last-used settings
  const savedMode    = getSetting('bot-mode',    'dotnet')
  const savedVerbose = getSetting('bot-verbose', false)
  const savedConsole = getSetting('bot-console', false)
  const modeInput = document.querySelector(`input[name="bot-mode"][value="${savedMode}"]`)
  if (modeInput) modeInput.checked = true
  $('opt-verbose').checked = savedVerbose
  $('opt-console').checked = savedConsole
  $('start-modal').classList.remove('hidden')
}

async function confirmStart() {
  const mode = document.querySelector('input[name="bot-mode"]:checked')?.value || 'python'
  const verbose = $('opt-verbose').checked, consoleFl = $('opt-console').checked
  setSetting('bot-mode',    mode)
  setSetting('bot-verbose', verbose)
  setSetting('bot-console', consoleFl)
  $('start-modal').classList.add('hidden')
  const result = await api.startBot({ mode, verbose, console: consoleFl })
  if (result.error) { alert(t('startError', result.error)); return }

  // New session — clear log display so we only see this run
  logClearedAt = 0
  extraLogLines = []
  logs = []
  $('log-container').innerHTML = ''

  botRunning = true; updateBotStatusBadge()
  appendLogLine({ level: 'INFO', message: t('botStarted', result.pid, mode), timestamp: new Date().toISOString() })
}

// ── Theme toggle ──────────────────────────────────────────────────────────
function initTheme() {
  if (getSetting('theme', 'dark') === 'light') document.body.classList.add('light')
  const btn = $('btn-theme')
  if (btn) {
    btn.textContent = document.body.classList.contains('light') ? '🌙' : '☀'
    btn.addEventListener('click', () => {
      document.body.classList.toggle('light')
      const isLight = document.body.classList.contains('light')
      setSetting('theme', isLight ? 'light' : 'dark')
      btn.textContent = isLight ? '🌙' : '☀'
    })
  }
}

// ── Language toggle ────────────────────────────────────────────────────────
function initLang() {
  applyLang()
  const btn = $('btn-lang')
  if (btn) {
    btn.addEventListener('click', () => {
      currentLang = currentLang === 'ru' ? 'en' : 'ru'
      setSetting('lang', currentLang)
      applyLang()
      // Re-render dynamic content with new language
      renderStats()
      renderRiskMeters()
      renderExitBreakdown()
      renderPositions()
      renderTrades()
      renderLog()
      updateBotStatusBadge()
    })
  }
}

// ── Modal setup ───────────────────────────────────────────────────────────
function initModals() {
  $('btn-config').addEventListener('click', openConfig)
  $('btn-close-config').addEventListener('click', () => $('config-modal').classList.add('hidden'))
  $('btn-save-config').addEventListener('click', saveConfig)
  $('cfg-browse-btn').addEventListener('click', async () => {
    const d = await api.browseDataDir(); if (!d) return
    $('cfg-datadir-val').textContent = d; $('data-dir-label').textContent = d; await refresh()
  })
  $('config-modal').addEventListener('click', e => { if (e.target === $('config-modal')) $('config-modal').classList.add('hidden') })

  $('btn-start-stop').addEventListener('click', startBot)
  $('btn-close-start').addEventListener('click', () => $('start-modal').classList.add('hidden'))
  $('btn-confirm-start').addEventListener('click', confirmStart)
  $('start-modal').addEventListener('click', e => { if (e.target === $('start-modal')) $('start-modal').classList.add('hidden') })

  $('btn-browse-dir').addEventListener('click', async () => {
    const d = await api.browseDataDir(); if (!d) return
    $('data-dir-label').textContent = d; $('cfg-datadir-val').textContent = d; await refresh()
  })

  $('btn-open-logs-dir').addEventListener('click', () => api.openLogsDir())
  $('btn-export-log').addEventListener('click', exportLog)
  $('btn-copy-log').addEventListener('click', () => {
    const isVisible = l => parseTs(l.timestamp) > logClearedAt
    const text = [...logs.filter(isVisible), ...extraLogLines.filter(isVisible)]
      .sort((a, b) => parseTs(a.timestamp) - parseTs(b.timestamp))
      .map(l => `${l.timestamp}\t${(l.level||'').padEnd(8)}\t${l.message||''}`)
      .join('\n')
    navigator.clipboard.writeText(text).then(() => {
      const btn = $('btn-copy-log')
      const orig = btn.querySelector('span').textContent
      btn.querySelector('span').textContent = '✓'
      setTimeout(() => { btn.querySelector('span').textContent = orig }, 1500)
    })
  })
  $('btn-clear-log').addEventListener('click', () => {
    logClearedAt = Date.now()
    extraLogLines = []
    $('log-container').innerHTML = ''
  })

  document.addEventListener('keydown', e => {
    if (e.key === 'Escape') { $('config-modal').classList.add('hidden'); $('start-modal').classList.add('hidden') }
    if (e.key === 'r' && !e.ctrlKey && !e.metaKey && document.activeElement.tagName !== 'INPUT') refresh()
  })
}

// ── Start ─────────────────────────────────────────────────────────────────
init()
