'use strict'
const assert = require('assert')
const fs = require('fs')
const { buildAttention, buildProviderHealth, buildHistoryPoint, buildHistorySeries, buildBotEnvironment, buildVpnLaunch, clampPaneSize, connectionMode, parseProcessLogChunk, dedupeLogs, formatLogText, toWslPath } = require('./dashboard-model')

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
const circuitHealth = buildProviderHealth([], [
  { timestamp: '2026-07-31T19:59:00Z', level: 'WARNING', message: 'openai circuit opened for 5 minutes' },
], { openai_enabled: true, openai_api_key: 'secret' }, now)
assert.strictEqual(circuitHealth.find(x => x.provider === 'openai').degraded, true)

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

const proxyEnv = buildBotEnvironment({ PATH: 'kept' }, {
  proxy_enabled: true,
  proxy_url: 'http://user:pass@127.0.0.1:8080',
})
assert.strictEqual(proxyEnv.PATH, 'kept')
assert.strictEqual(proxyEnv.HTTP_PROXY, 'http://user:pass@127.0.0.1:8080/')
assert.strictEqual(proxyEnv.HTTPS_PROXY, proxyEnv.HTTP_PROXY)
assert.strictEqual(proxyEnv.http_proxy, proxyEnv.HTTP_PROXY)
assert.strictEqual(proxyEnv.https_proxy, proxyEnv.HTTP_PROXY)
assert.strictEqual(proxyEnv.ALL_PROXY, proxyEnv.HTTP_PROXY)
assert.strictEqual(proxyEnv.all_proxy, proxyEnv.HTTP_PROXY)
const connectionEnv = buildBotEnvironment({}, {
  proxy_enabled: true,
  proxy_type: 'https',
  proxy_host: 'proxy.example.com',
  proxy_port: 8443,
  proxy_username: 'user@example.com',
  proxy_password: 'p@ss word',
  proxy_bypass: 'localhost,127.0.0.1',
})
assert.strictEqual(connectionEnv.HTTPS_PROXY, 'https://user%40example.com:p%40ss%20word@proxy.example.com:8443/')
assert.strictEqual(connectionEnv.NO_PROXY, 'localhost,127.0.0.1')
assert.strictEqual(connectionEnv.no_proxy, connectionEnv.NO_PROXY)
assert.deepStrictEqual(buildBotEnvironment({ PATH: 'kept' }, { proxy_enabled: false }), { PATH: 'kept' })
assert.throws(
  () => buildBotEnvironment({}, { proxy_enabled: true, proxy_url: 'socks5://127.0.0.1:1080' }),
  /HTTP or HTTPS/,
)
assert.throws(() => buildBotEnvironment({}, { proxy_enabled: true, proxy_url: '' }), /Proxy URL/)
assert.throws(() => buildBotEnvironment({}, { proxy_enabled: true, proxy_type: 'socks5', proxy_host: 'localhost' }), /HTTP or HTTPS/)
assert.throws(() => buildBotEnvironment({}, { proxy_enabled: true, proxy_host: 'localhost', proxy_port: 70000 }), /port/)

assert.strictEqual(connectionMode({}), 'direct')
assert.strictEqual(connectionMode({ proxy_enabled: true }), 'proxy')
assert.strictEqual(connectionMode({ network_mode: 'wireguard', proxy_enabled: true }), 'wireguard')
assert.throws(() => connectionMode({ network_mode: 'system-vpn' }), /network mode/i)
assert.strictEqual(toWslPath('C:\\Users\\me\\vpn files\\surfshark.conf'), '/mnt/c/Users/me/vpn files/surfshark.conf')
assert.throws(() => toWslPath('relative.conf'), /absolute Windows path/i)
const vpnLaunch = buildVpnLaunch({
  distro: 'Ubuntu', scriptPath: 'C:\\bot\\dashboard\\vpn-runner.sh',
  configPath: 'C:\\bot\\polymarket_bot_config.json', dataDir: 'C:\\bot\\data',
  root: 'C:\\bot', mode: 'dotnet', verbose: true, console: false,
})
assert.strictEqual(vpnLaunch.cmd, 'wsl.exe')
assert.strictEqual(vpnLaunch.useShell, false)
assert.deepStrictEqual(vpnLaunch.args, [
  '-d', 'Ubuntu', '-u', 'root', '--', 'bash', '/mnt/c/bot/dashboard/vpn-runner.sh',
  '--config', '/mnt/c/bot/polymarket_bot_config.json', '--data', '/mnt/c/bot/data',
  '--root', '/mnt/c/bot', '--mode', 'dotnet', '--verbose',
])
assert(!vpnLaunch.args.join(' ').includes('private'))

const chunk = parseProcessLogChunk('', '{"timestamp":"2026-07-31T20:00:00Z","level":"ERROR","message":"blocked"}\npartial', 'INFO', 'fallback')
assert.deepStrictEqual(chunk.entries, [{ timestamp: '2026-07-31T20:00:00Z', level: 'ERROR', message: 'blocked' }])
assert.strictEqual(chunk.remaining, 'partial')
const structuredChunk = parseProcessLogChunk('', '{"timestamp":"2026-07-31T20:00:00Z","level":"INFO","message":"done","properties":{"run_id":"r1","cycle":2}}\n')
assert.deepStrictEqual(structuredChunk.entries[0].properties, { run_id: 'r1', cycle: 2 })
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
assert(fs.readFileSync(require.resolve('./renderer.js'), 'utf8').includes("key: 'network_mode'"))
assert(fs.readFileSync(require.resolve('./renderer.js'), 'utf8').includes("key: 'vpn_config_path'"))
assert(fs.readFileSync(require.resolve('./renderer.js'), 'utf8').includes("key: 'wireguard_private_key'"))
assert(fs.readFileSync(require.resolve('./renderer.js'), 'utf8').includes("key: 'wireguard_public_key'"))
assert(fs.readFileSync(require.resolve('./renderer.js'), 'utf8').includes("key: 'email_security'"))
assert(fs.readFileSync(require.resolve('./renderer.js'), 'utf8').includes("['auto', 'Auto"))
assert(fs.readFileSync(require.resolve('./preload.js'), 'utf8').includes('browseVpnConfig:'))
assert(fs.readFileSync(require.resolve('./main.js'), 'utf8').includes("ipcMain.handle('browse-vpn-config'"))
console.log('dashboard model self-checks passed')
