'use strict'
const assert = require('assert')
const fs = require('fs')
const { buildAttention, buildProviderHealth, buildHistoryPoint, buildHistorySeries, clampPaneSize, parseProcessLogChunk, dedupeLogs, formatLogText } = require('./dashboard-model')

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
assert(!buildAttention({ portfolio, config: { llm_cost_tracking_enabled: false, max_daily_api_cost_usd: 10 }, now })
  .some(x => x.code === 'api_budget'))

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

const history = [
  { equity: 13.8, drawdown: .31, daily_api_cost: .12 },
  { equity: 14.1, drawdown: .28, daily_api_cost: .24 },
]
assert.deepStrictEqual(buildHistorySeries(history, 'equity').values, [13.8, 14.1])
assert.deepStrictEqual(buildHistorySeries(history, 'drawdown').values, [31, 28])
assert.deepStrictEqual(buildHistorySeries(history, 'api').values, [.12, .24])
assert.strictEqual(buildHistorySeries(history, 'unknown').mode, 'equity')
assert.strictEqual(clampPaneSize(50, 100, 300), 100)
assert.strictEqual(clampPaneSize(400, 100, 300), 300)
assert.strictEqual(clampPaneSize(180, 100, 300), 180)
assert.strictEqual(clampPaneSize('bad', 100, 300), 100)

const chunk = parseProcessLogChunk('', '{"timestamp":"2026-07-31T20:00:00Z","level":"ERROR","message":"blocked"}\npartial', 'INFO', 'fallback')
assert.deepStrictEqual(chunk.entries, [{ timestamp: '2026-07-31T20:00:00Z', level: 'ERROR', message: 'blocked' }])
assert.strictEqual(chunk.remaining, 'partial')
assert.strictEqual(dedupeLogs([chunk.entries[0], { ...chunk.entries[0] }]).length, 1)
const fallback = new Date('2026-07-31T20:00:00Z')
const localTime = [fallback.getHours(), fallback.getMinutes(), fallback.getSeconds()].map(value => String(value).padStart(2, '0')).join(':')
const consoleChunk = parseProcessLogChunk('', `[${localTime}] info: bot.main[0] Cycle 1 complete\n`, 'INFO', fallback.toISOString())
assert.deepStrictEqual(consoleChunk.entries, [{ timestamp: '2026-07-31T20:00:00.000Z', level: 'INFORMATION', message: 'Cycle 1 complete' }])
assert.strictEqual(dedupeLogs([
  consoleChunk.entries[0],
  { timestamp: '2026-07-31T20:00:01Z', level: 'INFORMATION', message: 'Cycle 1 complete' },
]).length, 1)
const bomChunk = parseProcessLogChunk('', '\ufeff{"timestamp":"2026-07-31T20:00:00Z","level":"ERROR","message":"blocked"}\n', 'INFO', 'fallback')
assert.deepStrictEqual(bomChunk.entries, chunk.entries)
assert.strictEqual(formatLogText([
  { timestamp: '2026-07-31T20:00:00Z', level: 'INFO', message: '\u001b[31mfailed\u001b[0m' },
  { timestamp: '2026-07-31T20:00:01Z', level: 'INFORMATION', message: '  failed  ' },
]), '2026-07-31T20:00:00Z\tINFO    \tfailed')
assert(fs.readFileSync(require.resolve('./renderer.js'), 'utf8').includes('api.copyText(text)'))
assert(fs.readFileSync(require.resolve('./preload.js'), 'utf8').includes("copyText:"))
assert(fs.readFileSync(require.resolve('./main.js'), 'utf8').includes("ipcMain.handle('copy-text'"))
console.log('dashboard model self-checks passed')
