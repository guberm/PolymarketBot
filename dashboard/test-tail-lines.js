const assert = require('assert')
const fs = require('fs')
const os = require('os')
const path = require('path')
const { readLastLines } = require('./tail-lines')

const file = path.join(os.tmpdir(), `polymarket-tail-${process.pid}.txt`)
try {
  fs.writeFileSync(file, 'one\r\ntwo\nthree\nfour\n')
  assert.deepStrictEqual(readLastLines(file, 2), ['three', 'four'])
  assert.deepStrictEqual(readLastLines(file, 0), [])
  console.log('Tail-line self-test passed')
} finally {
  try { fs.unlinkSync(file) } catch {}
}
