const fs = require('fs')

function readLastLines(file, count) {
  if (count <= 0) return []
  const fd = fs.openSync(file, 'r')
  try {
    let position = fs.fstatSync(fd).size
    let newlines = 0
    const chunks = []
    while (position > 0 && newlines <= count) {
      const length = Math.min(64 * 1024, position)
      position -= length
      const chunk = Buffer.allocUnsafe(length)
      fs.readSync(fd, chunk, 0, length, position)
      chunks.unshift(chunk)
      for (const byte of chunk) if (byte === 10) newlines++
    }
    return Buffer.concat(chunks).toString('utf8').trimEnd().split(/\r?\n/).filter(Boolean).slice(-count)
  } finally {
    fs.closeSync(fd)
  }
}

module.exports = { readLastLines }
