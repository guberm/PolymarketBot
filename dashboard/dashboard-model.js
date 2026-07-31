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
  if (dailyBudget > 0 && dailyCost >= dailyBudget * .8)
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
    const degraded = recentLogs.some(log => new RegExp(provider.replace('_', '[ _-]?'), 'i').test(String(log.message || '')) && /\b429\b|rate.?limit|failed|error/i.test(String(log.message || '')))
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

const dashboardModel = { buildAttention, buildProviderHealth, buildHistoryPoint }
if (typeof module !== 'undefined') module.exports = dashboardModel
if (typeof window !== 'undefined') window.DashboardModel = dashboardModel
