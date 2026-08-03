'use strict'
const assert = require('assert')
const path = require('path')
const { execFileSync } = require('child_process')
const { toWslPath } = require('./dashboard-model')

const script = toWslPath(path.join(__dirname, 'vpn-runner.sh'))
const output = execFileSync('wsl.exe', ['-d', 'Ubuntu', '--', 'bash', script, '--self-test'], { encoding: 'utf8' })
assert.match(output, /vpn runner self-checks passed/)
console.log('vpn runner integration self-check passed')
