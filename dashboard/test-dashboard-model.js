'use strict'
const assert = require('assert')
const { buildAttention, buildProviderHealth, buildHistoryPoint } = require('./dashboard-model')

const now = Date.parse('2026-07-31T20:00:00Z')
const portfolio = {
  bankroll: 8, high_water_mark: 20, daily_api_cost: 9, total_api_cost: 12,
  last_updated: now / 1000, is_halted: true,
  positions: [{ question: 'Thin market', shares: 10, current_price: .4,
    liquidation_limit_price: 0, book_depth_complete: false, quote_failures: 2,
    quote_age_seconds: 30 }],
}
const attention = buildAttention({
  portfolio,
  pendingOrders: [{ intent_id: 'pending-1', status: 'submitted' }],
  logs: [{ timestamp: '2026-07-31T19:55:00Z', level: 'ERROR', message: 'provider failed' }],
  config: { max_quote_age_seconds: 15, max_daily_api_cost_usd: 10 }, now,
})
assert(attention.some(x => x.code === 'halted' && x.severity === 'critical'))
assert(attention.some(x => x.code === 'pending_order'))
assert(attention.some(x => x.code === 'quote_health'))
assert(attention.some(x => x.code === 'api_budget'))

const estimates = [
  { record_type: 'evaluation', timestamp: 10, condition_id: 'a', provider_estimates: { openai: .8, gemini: .6 } },
  { record_type: 'evaluation', timestamp: 11, condition_id: 'b', provider_estimates: { openai: .3, gemini: .4 } },
  { record_type: 'resolution', timestamp: 12, condition_id: 'a', actual_outcome: 1 },
  { record_type: 'resolution', timestamp: 13, condition_id: 'b', actual_outcome: 0 },
]
const health = buildProviderHealth(estimates, [], {
  openai_enabled: true, openai_api_key: 'secret', gemini_enabled: true, gemini_api_key: 'secret',
  calibration_weighting_enabled: true, calibration_min_samples: 2,
  calibration_shrinkage: .25, calibration_max_provider_weight: .7,
}, now)
assert.strictEqual(health.filter(x => x.configured).length, 2)
assert.strictEqual(health.find(x => x.provider === 'openai').sampleCount, 2)
assert(Math.abs(health.find(x => x.provider === 'openai').brier - .065) < 1e-9)
assert(Math.abs(health.filter(x => x.configured).reduce((s, x) => s + x.weight, 0) - 1) < 1e-9)

assert.deepStrictEqual(buildHistoryPoint(portfolio), {
  timestamp: now / 1000, equity: 8, bankroll: 8, liquidation: 0,
  drawdown: .6, daily_api_cost: 9, total_api_cost: 12,
})
console.log('dashboard model self-checks passed')
