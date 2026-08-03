'use strict'

const PROVIDERS = ['anthropic', 'openai', 'gemini', 'openrouter', 'azure_openai']

function asTime(value) {
  if (typeof value === 'number') return value > 1e12 ? value : value * 1000
  return Date.parse(String(value || '').replace(/(\.\d{3})\d+/, '$1')) || 0
}

function configuredProviders(config = {}) {
  return PROVIDERS.filter(provider => {
    if (config[`${provider}_enabled`] === false || !config[`${provider}_api_key`]) return false
    return provider !== 'azure_openai' || Boolean(config.azure_openai_deployment)
  })
}

function connectionMode(config = {}) {
  const mode = String(config.network_mode || (config.proxy_enabled ? 'proxy' : 'direct')).toLowerCase()
  if (!['direct', 'proxy', 'wireguard', 'openvpn'].includes(mode)) throw new Error(`Unsupported network mode: ${mode}`)
  return mode
}

function toWslPath(value) {
  const match = String(value || '').match(/^([a-z]):[\\/](.*)$/i)
  if (!match) throw new Error('VPN launch requires an absolute Windows path')
  return `/mnt/${match[1].toLowerCase()}/${match[2].replace(/\\/g, '/')}`
}

function buildVpnLaunch({ distro = 'Ubuntu', scriptPath, configPath, dataDir, root, mode, verbose, console }) {
  const args = [
    '-d', String(distro || 'Ubuntu'), '-u', 'root', '--', 'bash', toWslPath(scriptPath),
    '--config', toWslPath(configPath), '--data', toWslPath(dataDir), '--root', toWslPath(root),
    '--mode', mode === 'dotnet' ? 'dotnet' : 'python',
  ]
  if (verbose) args.push('--verbose')
  if (console) args.push('--console')
  return { cmd: 'wsl.exe', args, cwd: root, useShell: false }
}

function buildBotEnvironment(baseEnv = {}, config = {}) {
  const env = { ...baseEnv }
  if (connectionMode(config) !== 'proxy') return env

  let proxy
  try {
    if (String(config.proxy_host || '').trim()) {
      const protocol = String(config.proxy_type || 'http').toLowerCase().replace(/:$/, '')
      if (!['http', 'https'].includes(protocol)) throw new Error('protocol')
      const host = String(config.proxy_host).trim()
      const port = String(config.proxy_port ?? '').trim()
      if (port && (!/^\d+$/.test(port) || Number(port) < 1 || Number(port) > 65535))
        throw new Error('port')
      proxy = new URL(`${protocol}://${host.includes(':') && !host.startsWith('[') ? `[${host}]` : host}`)
      if (port) proxy.port = port
      proxy.username = String(config.proxy_username || '')
      proxy.password = String(config.proxy_password || '')
    } else {
      proxy = new URL(String(config.proxy_url || ''))
    }
  }
  catch (e) {
    if (e.message === 'port') throw new Error('Proxy port must be between 1 and 65535')
    if (e.message === 'protocol') throw new Error('Proxy URL must use HTTP or HTTPS')
    throw new Error('Proxy URL is required and must be valid')
  }
  if (!['http:', 'https:'].includes(proxy.protocol))
    throw new Error('Proxy URL must use HTTP or HTTPS')

  for (const key of ['HTTP_PROXY', 'HTTPS_PROXY', 'ALL_PROXY', 'http_proxy', 'https_proxy', 'all_proxy']) env[key] = proxy.toString()
  if (String(config.proxy_bypass || '').trim())
    env.NO_PROXY = env.no_proxy = String(config.proxy_bypass).trim()
  return env
}

function buildAttention({ portfolio, pendingOrders = [], logs = [], config = {}, now = Date.now() }) {
  const items = []
  if (portfolio?.is_halted)
    items.push({ code: 'halted', severity: 'critical', title: 'Trading halted', detail: 'A portfolio risk limit stopped new trading.' })

  for (const order of pendingOrders) {
    const id = order.intent_id || order.order_id || order.condition_id || 'unknown'
    items.push({ code: 'pending_order', severity: 'critical', title: 'Order needs reconciliation', detail: `Pending live order ${id}.` })
  }

  const maxAge = Number(config.max_quote_age_seconds ?? 15)
  for (const position of portfolio?.positions || []) {
    const reasons = []
    if (Number(position.quote_failures || 0) > 0) reasons.push(`${position.quote_failures} quote failures`)
    if (Number(position.quote_age_seconds || 0) > maxAge) reasons.push(`quote age ${Number(position.quote_age_seconds).toFixed(1)}s`)
    if (position.book_depth_complete === false || Number(position.liquidation_limit_price || 0) <= 0) reasons.push('liquidation depth unavailable')
    if (reasons.length)
      items.push({ code: 'quote_health', severity: 'warning', title: position.question || 'Position quote degraded', detail: reasons.join(', ') + '.' })
  }

  const dailyCost = Number(portfolio?.daily_api_cost || 0)
  const dailyBudget = Number(config.max_daily_api_cost_usd || 0)
  if (config.llm_cost_tracking_enabled !== false && dailyBudget > 0 && dailyCost >= dailyBudget * .8)
    items.push({ code: 'api_budget', severity: dailyCost >= dailyBudget ? 'critical' : 'warning', title: 'API budget nearly exhausted', detail: `$${dailyCost.toFixed(2)} of $${dailyBudget.toFixed(2)} used today.` })

  const recent = logs.filter(log => now - asTime(log.timestamp) <= 60 * 60 * 1000)
  const errors = recent.filter(log => ['ERROR', 'CRITICAL'].includes(String(log.level || '').toUpperCase()))
  if (errors.length) {
    const last = errors[errors.length - 1]
    items.push({ code: 'recent_error', severity: String(last.level).toUpperCase() === 'CRITICAL' ? 'critical' : 'warning', title: `${errors.length} recent error${errors.length === 1 ? '' : 's'}`, detail: String(last.message || '').slice(0, 180) })
  }
  const rateLimits = recent.filter(log => /\b429\b|rate.?limit/i.test(String(log.message || '')))
  if (rateLimits.length)
    items.push({ code: 'rate_limit', severity: 'warning', title: 'Provider rate limiting', detail: `${rateLimits.length} rate-limit event${rateLimits.length === 1 ? '' : 's'} in the last hour.` })

  return items
}

function calibrationWeights(stats, providers, config) {
  const minSamples = Number(config.calibration_min_samples ?? 20)
  if (!config.calibration_weighting_enabled || !providers.length || providers.some(p => (stats[p]?.count || 0) < minSamples)) return {}
  if (providers.length === 1) return { [providers[0]]: 1 }
  const inverse = Object.fromEntries(providers.map(p => [p, 1 / Math.max(stats[p].sse / stats[p].count, .01)]))
  const inverseTotal = Object.values(inverse).reduce((a, b) => a + b, 0)
  const equal = 1 / providers.length
  const shrinkage = Math.min(1, Math.max(0, Number(config.calibration_shrinkage ?? .25)))
  const desired = Object.fromEntries(providers.map(p => [p, shrinkage * equal + (1 - shrinkage) * inverse[p] / inverseTotal]))
  const cap = Math.max(equal, Math.min(1, Number(config.calibration_max_provider_weight ?? .5)))
  const fixed = new Set()
  while (true) {
    const remaining = providers.filter(p => !fixed.has(p))
    const mass = 1 - cap * fixed.size
    const scale = mass / remaining.reduce((sum, p) => sum + desired[p], 0)
    const newlyFixed = remaining.filter(p => desired[p] * scale > cap)
    if (!newlyFixed.length)
      return Object.fromEntries(providers.map(p => [p, fixed.has(p) ? cap : desired[p] * scale]))
    newlyFixed.forEach(p => fixed.add(p))
  }
}

function buildProviderHealth(estimates = [], logs = [], config = {}, now = Date.now()) {
  const outcomes = {}
  for (const row of estimates)
    if (row.record_type === 'resolution' && Number.isFinite(Number(row.actual_outcome))) outcomes[String(row.condition_id)] = Number(row.actual_outcome)

  const stats = {}, latest = {}
  for (const row of estimates) {
    if (row.record_type && row.record_type !== 'evaluation') continue
    for (const [provider, raw] of Object.entries(row.provider_estimates || {})) {
      const probability = Number(raw)
      if (!Number.isFinite(probability)) continue
      if (!latest[provider] || Number(row.timestamp || 0) >= latest[provider].timestamp)
        latest[provider] = { timestamp: Number(row.timestamp || 0), probability }
      const outcome = outcomes[String(row.condition_id)]
      if (outcome === undefined) continue
      stats[provider] ||= { count: 0, sse: 0 }
      stats[provider].count++
      stats[provider].sse += (probability - outcome) ** 2
    }
  }

  const configured = configuredProviders(config)
  const weights = calibrationWeights(stats, configured, config)
  const recentLogs = logs.filter(log => now - asTime(log.timestamp) <= 60 * 60 * 1000)
  return PROVIDERS.map(provider => {
    const degraded = recentLogs.some(log => new RegExp(provider.replace('_', '[ _-]?'), 'i').test(String(log.message || '')) && /\b429\b|rate.?limit|circuit.?open|failed|error/i.test(String(log.message || '')))
    return {
      provider,
      enabled: config[`${provider}_enabled`] !== false,
      configured: configured.includes(provider),
      degraded,
      lastProbability: latest[provider]?.probability ?? null,
      lastTimestamp: latest[provider]?.timestamp ?? 0,
      sampleCount: stats[provider]?.count || 0,
      brier: stats[provider]?.count ? stats[provider].sse / stats[provider].count : null,
      weight: weights[provider] ?? null,
    }
  })
}

function buildHistoryPoint(portfolio) {
  if (!portfolio) return null
  const liquidation = (portfolio.positions || []).reduce((sum, position) => {
    if (position.book_depth_complete === false) return sum
    const price = Number(position.liquidation_limit_price || position.current_price || 0)
    return sum + Number(position.shares || 0) * price
  }, 0)
  const bankroll = Number(portfolio.bankroll || 0)
  const equity = bankroll + liquidation
  const high = Number(portfolio.high_water_mark || 0)
  return {
    timestamp: Number(portfolio.last_updated || Date.now() / 1000),
    equity, bankroll, liquidation,
    drawdown: high > 0 ? Math.max(0, (high - equity) / high) : 0,
    daily_api_cost: Number(portfolio.daily_api_cost || 0),
    total_api_cost: Number(portfolio.total_api_cost || 0),
  }
}

function buildHistorySeries(points = [], requestedMode = 'equity') {
  const modes = {
    equity: { label: 'Equity ($)', color: '#10b981', background: 'rgba(16,185,129,.10)', decimals: 2, prefix: '$', value: point => Number(point.equity || 0) },
    drawdown: { label: 'Drawdown (%)', color: '#ef4444', background: 'rgba(239,68,68,.10)', decimals: 1, suffix: '%', beginAtZero: true, value: point => Number((Number(point.drawdown || 0) * 100).toFixed(6)) },
    api: { label: 'API today ($)', color: '#8b5cf6', background: 'rgba(139,92,246,.10)', decimals: 3, prefix: '$', beginAtZero: true, value: point => Number(point.daily_api_cost || 0) },
  }
  const mode = modes[requestedMode] ? requestedMode : 'equity'
  const config = modes[mode]
  return { mode, ...config, values: points.map(config.value) }
}

function clampPaneSize(value, min, max) {
  min = Number(min)
  max = Math.max(min, Number(max))
  value = Number(value)
  return Number.isFinite(value) ? Math.min(max, Math.max(min, value)) : min
}

function parseProcessLogChunk(buffer = '', chunk = '', fallbackLevel = 'INFO', fallbackTimestamp = new Date().toISOString()) {
  const lines = (buffer + String(chunk)).split(/\r?\n/)
  const remaining = lines.pop() || ''
  const entries = lines.filter(line => line.trim()).map(line => {
    line = line.replace(/^\uFEFF/, '')
    try {
      const parsed = JSON.parse(line)
      if (parsed && typeof parsed === 'object' && parsed.message !== undefined)
        return {
          timestamp: parsed.timestamp || fallbackTimestamp,
          level: parsed.level || fallbackLevel,
          message: String(parsed.message),
          ...(parsed.properties && typeof parsed.properties === 'object' ? { properties: parsed.properties } : {}),
        }
    } catch {}
    const consoleLine = line.match(/^\[(\d{2}):(\d{2}):(\d{2})\]\s+(trce|dbug|info|warn|fail|crit):\s+\S+\[\d+\]\s+(.*)$/i)
    if (consoleLine) {
      const timestamp = new Date(fallbackTimestamp)
      timestamp.setHours(Number(consoleLine[1]), Number(consoleLine[2]), Number(consoleLine[3]), 0)
      const levels = { trce: 'TRACE', dbug: 'DEBUG', info: 'INFORMATION', warn: 'WARNING', fail: 'ERROR', crit: 'CRITICAL' }
      return { timestamp: timestamp.toISOString(), level: levels[consoleLine[4].toLowerCase()], message: consoleLine[5].trim() }
    }
    return { timestamp: fallbackTimestamp, level: fallbackLevel, message: line.trim() }
  })
  return { entries, remaining }
}

function dedupeLogs(entries = []) {
  const seen = new Map()
  return entries.filter(entry => {
    const level = String(entry.level || '').toUpperCase()
    const key = `${({ INFORMATION: 'INFO', WARNING: 'WARN' }[level] || level)}\u0000${cleanLogMessage(entry.message)}`
    const timestamp = asTime(entry.timestamp)
    const prior = seen.get(key) || []
    if (prior.some(value => timestamp && value ? Math.abs(timestamp - value) <= 2000 : timestamp === value)) return false
    prior.push(timestamp)
    seen.set(key, prior)
    return true
  })
}

function cleanLogMessage(message) {
  return String(message || '').replace(/\x1b\[[0-?]*[ -/]*[@-~]/g, '').trim()
}

function formatLogText(entries = []) {
  return dedupeLogs(entries)
    .sort((a, b) => asTime(a.timestamp) - asTime(b.timestamp))
    .map(entry => `${entry.timestamp || ''}\t${String(entry.level || '').padEnd(8)}\t${cleanLogMessage(entry.message)}`)
    .join('\n')
}

const dashboardModel = { buildAttention, buildProviderHealth, buildHistoryPoint, buildHistorySeries, buildBotEnvironment, buildVpnLaunch, clampPaneSize, connectionMode, parseProcessLogChunk, dedupeLogs, formatLogText, toWslPath }
if (typeof module !== 'undefined') module.exports = dashboardModel
if (typeof window !== 'undefined') window.DashboardModel = dashboardModel
