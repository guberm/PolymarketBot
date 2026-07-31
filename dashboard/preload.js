'use strict'
const { contextBridge, ipcRenderer } = require('electron')

contextBridge.exposeInMainWorld('api', {
  // Data directory
  getDataDir:    ()      => ipcRenderer.invoke('get-data-dir'),
  setDataDir:    (dir)   => ipcRenderer.invoke('set-data-dir', dir),
  browseDataDir: ()      => ipcRenderer.invoke('browse-data-dir'),

  // Data files
  readPortfolio: ()      => ipcRenderer.invoke('read-portfolio'),
  readTrades:    ()      => ipcRenderer.invoke('read-trades'),
  readLogs:      (n)     => ipcRenderer.invoke('read-logs', n),
  readEstimates: ()      => ipcRenderer.invoke('read-estimates'),
  readPendingOrders: ()  => ipcRenderer.invoke('read-pending-orders'),
  readEquityHistory: ()  => ipcRenderer.invoke('read-equity-history'),

  // Config
  readConfig:    ()      => ipcRenderer.invoke('read-config'),
  writeConfig:   (cfg)   => ipcRenderer.invoke('write-config', cfg),

  // Bot process
  botStatus:     ()      => ipcRenderer.invoke('bot-status'),
  startBot:      (opts)  => ipcRenderer.invoke('start-bot', opts),
  stopBot:       ()      => ipcRenderer.invoke('stop-bot'),

  // File export + folder
  saveFile:      (opts)  => ipcRenderer.invoke('save-file', opts),
  openLogsDir:   ()      => ipcRenderer.invoke('open-logs-dir'),
  copyText:      (text)  => ipcRenderer.invoke('copy-text', text),

  // UI settings
  readSettings:  ()      => ipcRenderer.invoke('read-settings'),
  writeSettings: (data)  => ipcRenderer.invoke('write-settings', data),

  // AI provider model listing
  fetchAiModels: (opts)  => ipcRenderer.invoke('fetch-ai-models', opts),

  // Events from main process
  onFileChanged: (cb)    => ipcRenderer.on('file-changed', (_, f) => cb(f)),
  onBotOutput:   (cb)    => ipcRenderer.on('bot-output',   (_, d) => cb(d)),
  onBotStopped:  (cb)    => ipcRenderer.on('bot-stopped',  (_, d) => cb(d)),
})
