import {useState, useEffect} from 'react'
import {
  StartZapret,
  StopZapret,
  StartV2RayN,
  StopV2RayN,
  GetStatus,
  GetZapretFiles,
  GetAvailableConfigs,
  SetConfig,
  TestZapretConfig,
  TestYouTube,
  TestDiscord,
  ImportVlessLink,
  AddWhitelistApp,
  RemoveWhitelistApp,
  AddWhitelistDomain,
  RemoveWhitelistDomain,
  GetWhitelistApps,
  GetWhitelistDomains,
  EnableTUNMode,
  DisableTUNMode,
  GetTUNModeStatus,
  GetV2RayNProfiles,
  SetActiveProfile
} from '../wailsjs/go/main/App'
import './App.css'

function App() {
  const [status, setStatus] = useState({
    zapretRunning: false,
    v2raynRunning: false,
    zapretPath: '',
    v2raynPath: '',
    currentConfig: 'general.bat'
  })
  const [vpnStatus, setVpnStatus] = useState({ connected: false, profile: '' })
  useEffect(() => {
    setVpnStatus({
      connected: tunModeEnabled || status.v2raynRunning,
      profile: activeProfile
    })
  }, [tunModeEnabled, status.v2raynRunning, activeProfile])

  const handleToggleTUNMode = async () => {
    if (tunModeEnabled) {
      await DisableTUNMode()
    } else {
      await EnableTUNMode()
    }
    setTunModeEnabled(!tunModeEnabled)
  }

  const loadTUNMode = async () => {
    const t = await GetTUNModeStatus()
    setTunModeEnabled(t.enabled)
  }
  const [files, setFiles] = useState({})
  const [configs, setConfigs] = useState([])
  const [loading, setLoading] = useState(false)
  const [message, setMessage] = useState('')
  const [youtubeTest, setYoutubeTest] = useState(null)
  const [discordTest, setDiscordTest] = useState(null)
  const [vlessLink, setVlessLink] = useState('')
  const [profiles, setProfiles] = useState([])
  const [activeProfile, setActiveProfile] = useState('')
  const [tunModeEnabled, setTunModeEnabled] = useState(false)
  const [whitelistApps, setWhitelistApps] = useState([])
  const [whitelistDomains, setWhitelistDomains] = useState([])
  const [newApp, setNewApp] = useState('')
  const [newDomain, setNewDomain] = useState('')
  const [zapretTest, setZapretTest] = useState(null)

  const presetApps = [
    {name: 'chrome.exe', label: 'Google Chrome'},
    {name: 'msedge.exe', label: 'Microsoft Edge'},
    {name: 'firefox.exe', label: 'Firefox'},
    {name: 'steam.exe', label: 'Steam'},
    {name: 'Discord.exe', label: 'Discord'},
    {name: 'spotify.exe', label: 'Spotify'},
    {name: 'telegram.exe', label: 'Telegram'},
  ]

  const presetDomains = [
    {domain: 'geosite:category-ru', label: 'Российские сайты'},
    {domain: 'geosite:private', label: 'Локальная сеть'},
    {domain: 'sberbank.ru', label: 'Сбербанк'},
    {domain: 'tinkoff.ru', label: 'Тинькофф'},
    {domain: 'gosuslugi.ru', label: 'Госуслуги'},
    {domain: 'mail.ru', label: 'Mail.ru'},
    {domain: 'yandex.ru', label: 'Yandex'},
  ]

  useEffect(() => {
    loadStatus()
    loadConfigs()
    loadProfiles()
    loadWhitelist()
    loadTUNMode()
    const interval = setInterval(loadStatus, 2000)
    return () => clearInterval(interval)
  }, [])

  const loadStatus = async () => {
    try {
      const s = await GetStatus()
      setStatus(s)
      const f = await GetZapretFiles()
      setFiles(f)
    } catch (e) {
      console.error(e)
    }
  }

  const loadConfigs = async () => {
    try {
      const c = await GetAvailableConfigs()
      setConfigs(c)
    } catch (e) {
      console.error(e)
    }
  }

  const loadProfiles = async () => {
    try {
      const profs = await GetV2RayNProfiles()
      setProfiles(profs)
    } catch (e) {
      console.error(e)
    }
  }

  const loadWhitelist = async () => {
    try {
      const apps = await GetWhitelistApps()
      const domains = await GetWhitelistDomains()
      setWhitelistApps(apps)
      setWhitelistDomains(domains)
    } catch (e) {
      console.error(e)
    }
  }

  const handleStartZapret = async () => {
    setLoading(true)
    setMessage('')
    try {
      await StartZapret()
      setMessage('✅ Zapret started!')
      await loadStatus()
    } catch (e) {
      setMessage('❌ Error: ' + e)
    }
    setLoading(false)
  }

  const handleStopZapret = async () => {
    setLoading(true)
    setMessage('')
    await StopZapret()
    setMessage('⏹ Zapret stopped')
    await loadStatus()
    setLoading(false)
  }

  const handleStartV2RayN = async () => {
    setLoading(true)
    setMessage('')
    try {
      await StartV2RayN()
      setMessage('✅ v2rayN started!')
      await loadStatus()
    } catch (e) {
      setMessage('❌ Error: ' + e)
    }
    setLoading(false)
  }

  const handleStopV2RayN = async () => {
    setLoading(true)
    setMessage('')
    await StopV2RayN()
    setMessage('⏹ v2rayN stopped')
    await loadStatus()
    setLoading(false)
  }

  const handleConnectVPN = async () => {
    setLoading(true)
    setMessage('')
    try {
      await EnableTUNMode()
      setTunModeEnabled(true)
      setMessage('✅ VPN connected (TUN enabled)')
    } catch (e) {
      setMessage('❌ Error: ' + e)
    }
    setLoading(false)
  }

  const handleDisconnectVPN = async () => {
    setLoading(true)
    setMessage('')
    try {
      await DisableTUNMode()
      setTunModeEnabled(false)
      setMessage('⏹ VPN disconnected (TUN disabled)')
    } catch (e) {
      setMessage('❌ Error: ' + e)
    }
    setLoading(false)
  }

  const handleConfigChange = async (configName) => {
    if (status.zapretRunning) {
      setMessage('⚠️ Stop Zapret before changing config')
      return
    }
    setLoading(true)
    try {
      await SetConfig(configName)
      setMessage(`✅ Config changed to ${configName}`)
      setStatus({...status, currentConfig: configName})
      await loadConfigs()
    } catch (e) {
      setMessage('❌ Error: ' + e)
    }
    setLoading(false)
  }

  const handleTestSelectedConfig = async () => {
    if (!status.currentConfig) {
      setMessage('⚠️ Select a config first')
      return
    }
    if (status.zapretRunning) {
      setMessage('⚠️ Stop Zapret before testing config')
      return
    }
    setLoading(true)
    try {
      const result = await TestZapretConfig(status.currentConfig)
      setZapretTest(result)
    } catch (e) {
      setZapretTest({success: false, message: 'Error: ' + e, status: 'error', config: status.currentConfig})
    }
    setLoading(false)
  }

  const handleImportVlessLink = async () => {
    if (!vlessLink.trim()) {
      setMessage('⚠️ Please enter a VLESS link')
      return
    }
    setLoading(true)
    try {
      await ImportVlessLink(vlessLink.trim())
      setMessage('✅ VLESS link imported!')
      setVlessLink('')
      await loadProfiles()
    } catch (e) {
      setMessage('❌ Error: ' + e)
    }
    setLoading(false)
  }

  const handleSelectProfile = async (profileId) => {
    setLoading(true)
    try {
      await SetActiveProfile(profileId)
      setActiveProfile(profileId)
      setMessage('✅ Profile activated!')
    } catch (e) {
      setMessage('❌ Error: ' + e)
    }
    setLoading(false)
  }

  const handleTestYouTube = async () => {
    setLoading(true)
    try {
      const result = await TestYouTube()
      setYoutubeTest(result)
    } catch (e) {
      setYoutubeTest({success: false, message: 'Error: ' + e, status: 'error'})
    }
    setLoading(false)
  }

  const handleTestDiscord = async () => {
    setLoading(true)
    try {
      const result = await TestDiscord()
      setDiscordTest(result)
    } catch (e) {
      setDiscordTest({success: false, message: 'Error: ' + e, status: 'error'})
    }
    setLoading(false)
  }

  const handleAddPresetApp = async (appName) => {
    setLoading(true)
    try {
      await AddWhitelistApp(appName)
      setMessage(`✅ Added ${appName} to whitelist`)
      await loadWhitelist()
    } catch (e) {
      setMessage('❌ Error: ' + e)
    }
    setLoading(false)
  }

  const handleAddCustomApp = async () => {
    if (!newApp.trim()) {
      setMessage('⚠️ Please enter application name')
      return
    }
    setLoading(true)
    try {
      await AddWhitelistApp(newApp.trim())
      setMessage(`✅ Added ${newApp} to whitelist`)
      setNewApp('')
      await loadWhitelist()
    } catch (e) {
      setMessage('❌ Error: ' + e)
    }
    setLoading(false)
  }

  const handleRemoveApp = async (appName) => {
    setLoading(true)
    try {
      await RemoveWhitelistApp(appName)
      setMessage(`✅ Removed ${appName} from whitelist`)
      await loadWhitelist()
    } catch (e) {
      setMessage('❌ Error: ' + e)
    }
    setLoading(false)
  }

  const handleAddPresetDomain = async (domain) => {
    setLoading(true)
    try {
      await AddWhitelistDomain(domain)
      setMessage(`✅ Added ${domain} to whitelist`)
      await loadWhitelist()
    } catch (e) {
      setMessage('❌ Error: ' + e)
    }
    setLoading(false)
  }

  const handleAddCustomDomain = async () => {
    if (!newDomain.trim()) {
      setMessage('⚠️ Please enter domain')
      return
    }
    setLoading(true)
    try {
      await AddWhitelistDomain(newDomain.trim())
      setMessage(`✅ Added ${newDomain} to whitelist`)
      setNewDomain('')
      await loadWhitelist()
    } catch (e) {
      setMessage('❌ Error: ' + e)
    }
    setLoading(false)
  }

  const handleRemoveDomain = async (domain) => {
    setLoading(true)
    try {
      await RemoveWhitelistDomain(domain)
      setMessage(`✅ Removed ${domain} from whitelist`)
      await loadWhitelist()
    } catch (e) {
      setMessage('❌ Error: ' + e)
    }
    setLoading(false)
  }

  return (
    <div className="App">
      <header className="App-header">
        <h1>🔐 My VPN + Zapret</h1>
        <p>Full Control VPN Manager</p>
      </header>

      <main>
        {message && (
          <div className={`message ${message.includes('Error') ? 'error' : 'success'}`}>
            {message}
            <button className="message-close" onClick={() => setMessage('')}>×</button>
          </div>
        )}

        {/* Status Section */}
        <section className="card status-section">
          <h2>📊 Status</h2>
          <div className="status-grid">
            <div className={`status-card ${status.zapretRunning ? 'active' : 'inactive'}`}>
              <span className="status-icon">{status.zapretRunning ? '✅' : '⏸️'}</span>
              <span>Zapret</span>
            </div>
            <div className={`status-card ${status.v2raynRunning ? 'active' : 'inactive'}`}>
              <span className="status-icon">{status.v2raynRunning ? '✅' : '⏸️'}</span>
              <span>v2rayN</span>
            </div>
            <div className={`status-card ${vpnStatus.connected ? 'active' : 'inactive'}`}>
              <span className="status-icon">{vpnStatus.connected ? '✅' : '⏸️'}</span>
              <span>VPN Connected</span>
            </div>
          </div>
        </section>

        {/* Zapret Config */}
        <section className="card config-section">
          <h2>⚙️ Zapret Configuration</h2>
          <div className="config-grid">
            {configs.map((cfg) => (
              <button
                key={cfg.name}
                className={`config-btn ${status.currentConfig === cfg.name ? 'active' : ''}`}
                onClick={() => handleConfigChange(cfg.name)}
                disabled={status.zapretRunning}
              >
                <span>{cfg.label}</span>
                {status.currentConfig === cfg.name && <span className="current-badge">Active</span>}
              </button>
            ))}
          </div>
          <div className="config-actions">
            <button className="btn btn-test" onClick={handleTestSelectedConfig} disabled={loading || status.zapretRunning}>
              🧪 Test Selected Config
            </button>
            {zapretTest && (
              <div className={`test-result ${zapretTest.status}`}>
                <span>{zapretTest.success ? '✅' : '❌'}</span>
                <span>
                  {zapretTest.config ? `${zapretTest.config}: ` : ''}
                  {zapretTest.message}
                  {zapretTest.time ? ` (${zapretTest.time})` : ''}
                </span>
              </div>
            )}
          </div>
        </section>

        {/* Controls */}
        <section className="card controls-section">
          <h2>🎮 Controls</h2>
          <div className="button-grid">
            {!status.zapretRunning ? (
              <button className="btn btn-start" onClick={handleStartZapret} disabled={loading}>
                ▶ Start Zapret
              </button>
            ) : (
              <button className="btn btn-stop" onClick={handleStopZapret} disabled={loading}>
                ⏹ Stop Zapret
              </button>
            )}
            
            {!status.v2raynRunning ? (
              <button className="btn btn-start" onClick={handleStartV2RayN} disabled={loading}>
                ▶ Start v2rayN
              </button>
            ) : (
              <button className="btn btn-stop" onClick={handleStopV2RayN} disabled={loading}>
                ⏹ Stop v2rayN
              </button>
            )}

            {!vpnStatus.connected ? (
              <button className="btn btn-start" onClick={handleConnectVPN} disabled={loading || !status.v2raynRunning}>
                🔌 Connect VPN
              </button>
            ) : (
              <button className="btn btn-stop" onClick={handleDisconnectVPN} disabled={loading}>
                🔌 Disconnect VPN
              </button>
            )}
          </div>
        </section>

        {/* VLESS Configuration */}
        <section className="card vless-section">
          <h2>🔐 VLESS Configuration</h2>
          
          <div className="form-group">
            <label>Import VLESS Link:</label>
            <div className="input-group">
              <input 
                type="text" 
                value={vlessLink}
                onChange={e => setVlessLink(e.target.value)}
                placeholder="vless://uuid@server:port?..."
              />
              <button onClick={handleImportVlessLink} className="btn btn-import" disabled={loading}>
                📥 Import
              </button>
            </div>
          </div>

          {profiles.length > 0 && (
            <div className="profiles-list">
              <h3>📋 Profiles:</h3>
              {profiles.map(profile => (
                <div 
                  key={profile.index} 
                  className={`profile-item ${activeProfile === profile.index ? 'active' : ''}`}
                  onClick={() => handleSelectProfile(profile.index)}
                >
                  <span>{profile.remarks || profile.address}</span>
                  {activeProfile === profile.index && <span className="badge">Active</span>}
                </div>
              ))}
            </div>
          )}
        </section>

        {/* Whitelist Section */}
        <section className="card whitelist-section">
          <h2>🛡️ Whitelist Manager</h2>
          
          <div className="whitelist-group">
            <h3>📁 Applications</h3>
            <div className="preset-list">
              {presetApps.map(app => (
                <button
                  key={app.name}
                  className="btn btn-preset"
                  onClick={() => handleAddPresetApp(app.name)}
                  disabled={loading || whitelistApps.some(a => a.name === app.name)}
                >
                  {app.label}
                  {whitelistApps.some(a => a.name === app.name) && ' ✅'}
                </button>
              ))}
            </div>

            <div className="custom-input">
              <input 
                type="text" 
                value={newApp}
                onChange={e => setNewApp(e.target.value)}
                placeholder="chrome.exe"
              />
              <button className="btn btn-add" onClick={handleAddCustomApp} disabled={loading}>
                Add App
              </button>
            </div>

            {whitelistApps.length > 0 && (
              <div className="whitelist-items">
                {whitelistApps.map((app, idx) => (
                  <div key={idx} className="whitelist-item">
                    <span>{app.name}</span>
                    <button className="btn btn-remove" onClick={() => handleRemoveApp(app.name)}>✕</button>
                  </div>
                ))}
              </div>
            )}
          </div>

          <div className="whitelist-group">
            <h3>🌐 Domains</h3>
            <div className="preset-list">
              {presetDomains.map(domain => (
                <button
                  key={domain.domain}
                  className="btn btn-preset"
                  onClick={() => handleAddPresetDomain(domain.domain)}
                  disabled={loading || whitelistDomains.some(d => d.domain === domain.domain)}
                >
                  {domain.label}
                  {whitelistDomains.some(d => d.domain === domain.domain) && ' ✅'}
                </button>
              ))}
            </div>

            <div className="custom-input">
              <input 
                type="text" 
                value={newDomain}
                onChange={e => setNewDomain(e.target.value)}
                placeholder="example.com"
              />
              <button className="btn btn-add" onClick={handleAddCustomDomain} disabled={loading}>
                Add Domain
              </button>
            </div>

            {whitelistDomains.length > 0 && (
              <div className="whitelist-items">
                {whitelistDomains.map((domain, idx) => (
                  <div key={idx} className="whitelist-item">
                    <span>{domain.domain}</span>
                    <button className="btn btn-remove" onClick={() => handleRemoveDomain(domain.domain)}>✕</button>
                  </div>
                ))}
              </div>
            )}
          </div>
        </section>

        {/* Connection Tests */}
        <section className="card test-section">
          <h2>🧪 Connection Tests</h2>
          <div className="test-grid">
            <div className="test-card">
              <h3>📺 YouTube</h3>
              <button onClick={handleTestYouTube} disabled={loading} className="btn btn-test">
                Test Connection
              </button>
              {youtubeTest && (
                <div className={`test-result ${youtubeTest.status}`}>
                  <span>{youtubeTest.success ? '✅' : '❌'}</span>
                  <span>{youtubeTest.message}</span>
                </div>
              )}
            </div>
            
            <div className="test-card">
              <h3>💬 Discord</h3>
              <button onClick={handleTestDiscord} disabled={loading} className="btn btn-test">
                Test Connection
              </button>
              {discordTest && (
                <div className={`test-result ${discordTest.status}`}>
                  <span>{discordTest.success ? '✅' : '❌'}</span>
                  <span>{discordTest.message}</span>
                </div>
              )}
            </div>
          </div>
        </section>
      </main>

      <footer>
        <p>Built with Wails + Go + React + v2rayN</p>
      </footer>
    </div>
  )
}

export default App
