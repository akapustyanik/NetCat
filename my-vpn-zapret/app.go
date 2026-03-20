package main

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
	"syscall"
	"time"
)

// App struct
type App struct {
	ctx           context.Context
	zapretRunning bool
	v2raynRunning bool
	zapretPath    string
	v2raynPath    string
	zapretCmd     *exec.Cmd
	v2raynCmd     *exec.Cmd
	currentConfig string
}

// V2RayNConfig структура для VLESS конфигурации
type V2RayNConfig struct {
	Server     string `json:"server"`
	Port       int    `json:"port"`
	UUID       string `json:"uuid"`
	Flow       string `json:"flow"`
	Security   string `json:"security"`
	SNI        string `json:"sni"`
	PublicKey  string `json:"publicKey"`
	ShortID    string `json:"shortId"`
	SpiderX    string `json:"spiderX"`
	Encryption string `json:"encryption"`
	Remark     string `json:"remark"`
}

// WhitelistApp структура для белого списка приложений
type WhitelistApp struct {
	Name    string `json:"name"`
	Enabled bool   `json:"enabled"`
}

// WhitelistDomain структура для белого списка доменов
type WhitelistDomain struct {
	Domain  string `json:"domain"`
	Enabled bool   `json:"enabled"`
}

// NewApp creates a new App application struct
func NewApp() *App {
	zapretPath := ""
	v2raynPath := ""

	wd, _ := os.Getwd()

	// Prefer external zapret folder (repo root /zapret) if present
	externalZapret := filepath.Clean(filepath.Join(wd, "..", "zapret"))
	if _, err := os.Stat(externalZapret); err == nil {
		zapretPath = externalZapret
	}

	// Zapret path
	if zapretPath == "" {
		testPath := filepath.Join(wd, "resources", "zapret")
		if _, err := os.Stat(testPath); err == nil {
			zapretPath = testPath
		}
	}

	// V2RayN path
	testPath := filepath.Join(wd, "resources", "v2rayn")
	if _, err := os.Stat(testPath); err == nil {
		v2raynPath = testPath
	}

	currentConfig := "general.bat"
	if zapretPath != "" {
		if _, err := os.Stat(filepath.Join(zapretPath, currentConfig)); err != nil {
			matches, _ := filepath.Glob(filepath.Join(zapretPath, "*.bat"))
			sort.Strings(matches)
			for _, match := range matches {
				name := filepath.Base(match)
				if strings.EqualFold(name, "service.bat") {
					continue
				}
				currentConfig = name
				break
			}
		}
	}

	return &App{
		zapretPath:    zapretPath,
		v2raynPath:    v2raynPath,
		currentConfig: currentConfig,
	}
}

func (a *App) startup(ctx context.Context)  { a.ctx = ctx }
func (a *App) domReady(ctx context.Context) {}
func (a *App) beforeClose(ctx context.Context) (prevent bool) {
	a.StopZapret()
	a.StopV2RayN()
	return false
}
func (a *App) shutdown(ctx context.Context) {}

// ==================== ZAPRET ФУНКЦИИ ====================

func (a *App) getGameFilter() (string, string) {
	gameFlagFile := filepath.Join(a.zapretPath, "utils", "game_filter.enabled")
	if _, err := os.Stat(gameFlagFile); err == nil {
		data, _ := os.ReadFile(gameFlagFile)
		mode := strings.TrimSpace(string(data))
		switch strings.ToLower(mode) {
		case "all":
			return "1024-65535", "1024-65535"
		case "tcp":
			return "1024-65535", "12"
		case "udp":
			return "12", "1024-65535"
		}
	}
	return "12", "12"
}

func (a *App) getWinwsArgs(configName string) ([]string, error) {
	binPath := filepath.Join(a.zapretPath, "bin")
	listsPath := filepath.Join(a.zapretPath, "lists")
	gameFilterTCP, gameFilterUDP := a.getGameFilter()

	var args []string

	switch configName {
	case "general (ALT10).bat":
		args = []string{
			"--wf-tcp=80,443,2053,2083,2087,2096,8443," + gameFilterTCP,
			"--wf-udp=443,19294-19344,50000-50100," + gameFilterUDP,
			"--filter-udp=443",
			"--hostlist=" + listsPath + "\\list-general.txt",
			"--dpi-desync=fake",
			"--dpi-desync-repeats=6",
			"--dpi-desync-fake-quic=" + binPath + "\\quic_initial_www_google_com.bin",
		}
	case "general (ALT11).bat":
		args = []string{
			"--wf-tcp=80,443,2053,2083,2087,2096,8443," + gameFilterTCP,
			"--wf-udp=443,19294-19344,50000-50100," + gameFilterUDP,
			"--filter-udp=443",
			"--hostlist=" + listsPath + "\\list-general.txt",
			"--dpi-desync=fake",
			"--dpi-desync-repeats=11",
			"--dpi-desync-fake-quic=" + binPath + "\\quic_initial_www_google_com.bin",
		}
	case "general (ALT8).bat":
		args = []string{
			"--wf-tcp=80,443,2053,2083,2087,2096,8443," + gameFilterTCP,
			"--wf-udp=443,19294-19344,50000-50100," + gameFilterUDP,
			"--filter-udp=443",
			"--hostlist=" + listsPath + "\\list-general.txt",
			"--dpi-desync=fake",
			"--dpi-desync-repeats=6",
			"--dpi-desync-fake-quic=" + binPath + "\\quic_initial_www_google_com.bin",
		}
	default:
		args = []string{
			"--wf-tcp=80,443,2053,2083,2087,2096,8443," + gameFilterTCP,
			"--wf-udp=443,19294-19344,50000-50100," + gameFilterUDP,
			"--filter-udp=443",
			"--hostlist=" + listsPath + "\\list-general.txt",
			"--dpi-desync=multisplit",
			"--dpi-desync-split-seqovl=568",
			"--dpi-desync-split-pos=1",
		}
	}

	return args, nil
}

func (a *App) runZapretBat(configName string) error {
	if a.zapretPath == "" {
		return fmt.Errorf("zapret path not configured")
	}

	batPath := filepath.Join(a.zapretPath, configName)
	if _, err := os.Stat(batPath); os.IsNotExist(err) {
		return fmt.Errorf("bat file not found: %s", configName)
	}

	cmd := exec.Command("cmd", "/C", batPath)
	cmd.SysProcAttr = &syscall.SysProcAttr{
		HideWindow:    true,
		CreationFlags: 0x08000000,
	}
	cmd.Dir = a.zapretPath

	if err := cmd.Start(); err != nil {
		return fmt.Errorf("failed to start bat: %v", err)
	}

	return nil
}

func (a *App) StartZapret() error {
	if a.zapretRunning {
		return fmt.Errorf("zapret already running")
	}
	if a.zapretPath == "" {
		return fmt.Errorf("zapret path not configured")
	}

	if strings.HasSuffix(strings.ToLower(a.currentConfig), ".bat") {
		if err := a.runZapretBat(a.currentConfig); err != nil {
			return err
		}
		a.zapretRunning = true
		return nil
	}

	winwsPath := filepath.Join(a.zapretPath, "bin", "winws.exe")
	if _, err := os.Stat(winwsPath); os.IsNotExist(err) {
		return fmt.Errorf("winws.exe not found at %s", winwsPath)
	}

	args, err := a.getWinwsArgs(a.currentConfig)
	if err != nil {
		return fmt.Errorf("failed to get args: %v", err)
	}

	cmd := exec.Command(winwsPath, args...)
	cmd.SysProcAttr = &syscall.SysProcAttr{
		HideWindow:    true,
		CreationFlags: 0x08000000,
	}
	cmd.Dir = filepath.Join(a.zapretPath, "bin")

	if err := cmd.Start(); err != nil {
		return fmt.Errorf("failed to start winws: %v", err)
	}

	a.zapretCmd = cmd
	a.zapretRunning = true
	return nil
}

func (a *App) StopZapret() error {
	if !a.zapretRunning {
		return nil
	}

	exec.Command("taskkill", "/IM", "winws.exe", "/F").Run()

	if a.zapretCmd != nil {
		a.zapretCmd.Process.Kill()
	}

	a.zapretRunning = false
	return nil
}

// ==================== V2RAYN ФУНКЦИИ ====================

func (a *App) GetV2RayNProfilesPath() string {
	if a.v2raynPath == "" {
		return ""
	}
	return filepath.Join(a.v2raynPath, "guiConfigs", "profiles")
}

func (a *App) StartV2RayN() error {
	if a.v2raynRunning {
		return fmt.Errorf("v2rayn already running")
	}

	if a.v2raynPath == "" {
		return fmt.Errorf("v2rayn path not configured")
	}

	v2raynExe := filepath.Join(a.v2raynPath, "v2rayN.exe")
	if _, err := os.Stat(v2raynExe); os.IsNotExist(err) {
		return fmt.Errorf("v2rayN.exe not found at %s", v2raynExe)
	}

	if err := a.UpdateRoutingRules(); err != nil {
		return fmt.Errorf("failed to apply routing rules: %v", err)
	}

	cmd := exec.Command("tasklist", "/FI", "IMAGENAME eq v2rayN.exe")
	output, _ := cmd.Output()
	if len(output) > 20 {
		a.v2raynRunning = true
		return nil
	}

	cmd = exec.Command(v2raynExe)
	cmd.SysProcAttr = &syscall.SysProcAttr{
		HideWindow:    true,
		CreationFlags: 0x08000000,
	}
	cmd.Dir = a.v2raynPath

	if err := cmd.Start(); err != nil {
		return fmt.Errorf("failed to start v2rayN: %v", err)
	}

	a.v2raynCmd = cmd
	a.v2raynRunning = true
	time.Sleep(5 * time.Second)

	if err := a.EnableSystemProxy(); err != nil {
		return fmt.Errorf("failed to enable proxy: %v", err)
	}

	return nil
}

func (a *App) StopV2RayN() error {
	if !a.v2raynRunning {
		return nil
	}

	a.DisableSystemProxy()
	exec.Command("taskkill", "/IM", "v2rayN.exe", "/F").Run()
	exec.Command("taskkill", "/IM", "v2ray.exe", "/F").Run()

	if a.v2raynCmd != nil {
		a.v2raynCmd.Process.Kill()
	}

	a.v2raynRunning = false
	return nil
}

func (a *App) EnableSystemProxy() error {
	configPath := filepath.Join(a.v2raynPath, "guiConfigs", "config.json")
	data, err := os.ReadFile(configPath)
	if err != nil {
		data = []byte(`{"systemProxyEnabled": true, "tunModeEnabled": false}`)
	}

	var config map[string]interface{}
	if err := json.Unmarshal(data, &config); err != nil {
		config = make(map[string]interface{})
	}

	config["systemProxyEnabled"] = true
	config["tunModeEnabled"] = false

	newData, err := json.MarshalIndent(config, "", "  ")
	if err != nil {
		return err
	}

	return os.WriteFile(configPath, newData, 0644)
}

func (a *App) DisableSystemProxy() error {
	configPath := filepath.Join(a.v2raynPath, "guiConfigs", "config.json")
	data, err := os.ReadFile(configPath)
	if err != nil {
		return nil
	}

	var config map[string]interface{}
	if err := json.Unmarshal(data, &config); err != nil {
		return nil
	}

	config["systemProxyEnabled"] = false

	newData, err := json.MarshalIndent(config, "", "  ")
	if err != nil {
		return nil
	}

	return os.WriteFile(configPath, newData, 0644)
}

// ==================== VLESS ФУНКЦИИ ====================

func (a *App) ParseVlessLink(link string) map[string]interface{} {
	result := map[string]interface{}{
		"success": false,
		"message": "",
		"config":  map[string]interface{}{},
	}

	if !strings.HasPrefix(link, "vless://") {
		result["message"] = "Invalid VLESS link format"
		return result
	}

	parsed, err := url.Parse(link)
	if err != nil {
		result["message"] = fmt.Sprintf("Failed to parse URL: %v", err)
		return result
	}

	uuid := parsed.User.String()
	server := parsed.Hostname()
	port := 0
	fmt.Sscanf(parsed.Port(), "%d", &port)

	params := parsed.Query()
	security := params.Get("security")
	sni := params.Get("sni")
	publicKey := params.Get("pbk")
	shortID := params.Get("sid")
	spiderX := params.Get("spx")
	flow := params.Get("flow")
	encryption := params.Get("encryption")

	name := ""
	if parsed.Fragment != "" {
		decoded, err := url.QueryUnescape(parsed.Fragment)
		if err == nil {
			name = decoded
		} else {
			name = parsed.Fragment
		}
	}

	result["success"] = true
	result["message"] = "Link parsed successfully: " + name
	result["config"] = map[string]interface{}{
		"server":     server,
		"port":       port,
		"uuid":       uuid,
		"flow":       flow,
		"security":   security,
		"sni":        sni,
		"publicKey":  publicKey,
		"shortId":    shortID,
		"spiderX":    spiderX,
		"encryption": encryption,
		"name":       name,
	}

	return result
}

func (a *App) ImportVlessLink(link string) error {
	result := a.ParseVlessLink(link)
	if !result["success"].(bool) {
		return fmt.Errorf("%s", result["message"])
	}

	config := result["config"].(map[string]interface{})

	cfg := &V2RayNConfig{
		Server:     config["server"].(string),
		Port:       config["port"].(int),
		UUID:       config["uuid"].(string),
		Flow:       config["flow"].(string),
		Security:   config["security"].(string),
		SNI:        config["sni"].(string),
		PublicKey:  config["publicKey"].(string),
		ShortID:    config["shortId"].(string),
		SpiderX:    config["spiderX"].(string),
		Encryption: config["encryption"].(string),
		Remark:     config["name"].(string),
	}

	return a.SaveV2RayNProfile(cfg)
}

func (a *App) SaveV2RayNProfile(cfg *V2RayNConfig) error {
	profilesPath := a.GetV2RayNProfilesPath()
	if profilesPath == "" {
		return fmt.Errorf("profiles path not configured")
	}

	os.MkdirAll(profilesPath, 0755)

	profileID := fmt.Sprintf("%d", time.Now().UnixNano())
	profileFile := filepath.Join(profilesPath, fmt.Sprintf("%s.json", profileID))

	v2raynProfile := map[string]interface{}{
		"index":      profileID,
		"remarks":    cfg.Remark,
		"address":    cfg.Server,
		"port":       cfg.Port,
		"id":         cfg.UUID,
		"alterId":    0,
		"security":   "auto",
		"network":    "tcp",
		"configType": 5,
		"flow":       cfg.Flow,
		"sni":        cfg.SNI,
		"publicKey":  cfg.PublicKey,
		"shortId":    cfg.ShortID,
		"spiderX":    cfg.SpiderX,
		"headerType": "none",
	}

	data, err := json.MarshalIndent(v2raynProfile, "", "  ")
	if err != nil {
		return err
	}

	return os.WriteFile(profileFile, data, 0644)
}

func (a *App) GetV2RayNProfiles() []map[string]interface{} {
	profilesPath := a.GetV2RayNProfilesPath()
	if profilesPath == "" {
		return []map[string]interface{}{}
	}

	profiles := []map[string]interface{}{}

	files, err := os.ReadDir(profilesPath)
	if err != nil {
		return profiles
	}

	for _, file := range files {
		if strings.HasSuffix(file.Name(), ".json") {
			data, err := os.ReadFile(filepath.Join(profilesPath, file.Name()))
			if err != nil {
				continue
			}

			var profile map[string]interface{}
			if err := json.Unmarshal(data, &profile); err != nil {
				continue
			}

			profiles = append(profiles, profile)
		}
	}

	return profiles
}

func (a *App) SetActiveProfile(profileID string) error {
	configPath := filepath.Join(a.v2raynPath, "guiConfigs", "config.json")
	data, err := os.ReadFile(configPath)
	if err != nil {
		data = []byte(`{}`)
	}

	var config map[string]interface{}
	if err := json.Unmarshal(data, &config); err != nil {
		config = make(map[string]interface{})
	}

	config["indexId"] = profileID

	newData, err := json.MarshalIndent(config, "", "  ")
	if err != nil {
		return err
	}

	return os.WriteFile(configPath, newData, 0644)
}

// ==================== WHITELIST ФУНКЦИИ ====================

func (a *App) AddWhitelistApp(appName string) error {
	configPath := filepath.Join(a.v2raynPath, "whitelist-apps.json")

	var apps []WhitelistApp
	data, err := os.ReadFile(configPath)
	if err == nil {
		json.Unmarshal(data, &apps)
	}

	for _, app := range apps {
		if app.Name == appName {
			return fmt.Errorf("application %s already in whitelist", appName)
		}
	}

	apps = append(apps, WhitelistApp{Name: appName, Enabled: true})
	data, _ = json.MarshalIndent(apps, "", "  ")
	if err := os.WriteFile(configPath, data, 0644); err != nil {
		return err
	}
	return a.UpdateRoutingRules()
}

func (a *App) RemoveWhitelistApp(appName string) error {
	configPath := filepath.Join(a.v2raynPath, "whitelist-apps.json")

	var apps []WhitelistApp
	data, err := os.ReadFile(configPath)
	if err != nil {
		return nil
	}

	json.Unmarshal(data, &apps)

	newApps := []WhitelistApp{}
	for _, app := range apps {
		if app.Name != appName {
			newApps = append(newApps, app)
		}
	}

	data, _ = json.MarshalIndent(newApps, "", "  ")
	if err := os.WriteFile(configPath, data, 0644); err != nil {
		return err
	}
	return a.UpdateRoutingRules()
}

func (a *App) GetWhitelistApps() []map[string]interface{} {
	configPath := filepath.Join(a.v2raynPath, "whitelist-apps.json")

	var apps []WhitelistApp
	data, err := os.ReadFile(configPath)
	if err != nil {
		return []map[string]interface{}{}
	}

	json.Unmarshal(data, &apps)

	result := []map[string]interface{}{}
	for _, app := range apps {
		result = append(result, map[string]interface{}{
			"name":    app.Name,
			"enabled": app.Enabled,
		})
	}

	return result
}

func (a *App) AddWhitelistDomain(domain string) error {
	configPath := filepath.Join(a.v2raynPath, "whitelist-domains.json")

	var domains []WhitelistDomain
	data, err := os.ReadFile(configPath)
	if err == nil {
		json.Unmarshal(data, &domains)
	}

	for _, d := range domains {
		if d.Domain == domain {
			return fmt.Errorf("domain %s already in whitelist", domain)
		}
	}

	domains = append(domains, WhitelistDomain{Domain: domain, Enabled: true})
	data, _ = json.MarshalIndent(domains, "", "  ")
	if err := os.WriteFile(configPath, data, 0644); err != nil {
		return err
	}
	return a.UpdateRoutingRules()
}

func (a *App) RemoveWhitelistDomain(domain string) error {
	configPath := filepath.Join(a.v2raynPath, "whitelist-domains.json")

	var domains []WhitelistDomain
	data, err := os.ReadFile(configPath)
	if err != nil {
		return nil
	}

	json.Unmarshal(data, &domains)

	newDomains := []WhitelistDomain{}
	for _, d := range domains {
		if d.Domain != domain {
			newDomains = append(newDomains, d)
		}
	}

	data, _ = json.MarshalIndent(newDomains, "", "  ")
	if err := os.WriteFile(configPath, data, 0644); err != nil {
		return err
	}
	return a.UpdateRoutingRules()
}

func (a *App) GetWhitelistDomains() []map[string]interface{} {
	configPath := filepath.Join(a.v2raynPath, "whitelist-domains.json")

	var domains []WhitelistDomain
	data, err := os.ReadFile(configPath)
	if err != nil {
		return []map[string]interface{}{}
	}

	json.Unmarshal(data, &domains)

	result := []map[string]interface{}{}
	for _, d := range domains {
		result = append(result, map[string]interface{}{
			"domain":  d.Domain,
			"enabled": d.Enabled,
		})
	}

	return result
}

func (a *App) UpdateRoutingRules() error {
	if a.v2raynPath == "" {
		return fmt.Errorf("v2rayn path not configured")
	}

	configPath := filepath.Join(a.v2raynPath, "binConfigs", "config.json")
	data, err := os.ReadFile(configPath)
	if err != nil {
		return err
	}

	var cfg map[string]interface{}
	if err := json.Unmarshal(data, &cfg); err != nil {
		return err
	}

	routing := map[string]interface{}{}
	if existing, ok := cfg["routing"].(map[string]interface{}); ok {
		routing = existing
	}

	rules := []map[string]interface{}{}

	apps := a.loadEnabledWhitelistApps()
	if len(apps) > 0 {
		rules = append(rules, map[string]interface{}{
			"type":        "field",
			"outboundTag": "direct",
			"process":     apps,
		})
	}

	domains := []string{
		"geosite:youtube",
		"domain:youtube.com",
		"domain:youtu.be",
		"domain:googlevideo.com",
		"domain:ytimg.com",
		"domain:ggpht.com",
		"domain:youtubei.googleapis.com",
	}
	domains = append(domains, a.loadEnabledWhitelistDomains()...)
	domains = uniqueStrings(domains)
	if len(domains) > 0 {
		rules = append(rules, map[string]interface{}{
			"type":        "field",
			"outboundTag": "direct",
			"domain":      domains,
		})
	}

	rules = append(rules, map[string]interface{}{
		"type":        "field",
		"outboundTag": "direct",
		"ip":          []string{"geoip:private"},
	})
	rules = append(rules, map[string]interface{}{
		"type":        "field",
		"outboundTag": "direct",
		"domain":      []string{"geosite:private"},
	})
	rules = append(rules, map[string]interface{}{
		"type":        "field",
		"port":        "0-65535",
		"outboundTag": "proxy",
	})

	if _, ok := routing["domainStrategy"]; !ok {
		routing["domainStrategy"] = "IPOnDemand"
	}
	routing["rules"] = rules
	cfg["routing"] = routing

	newData, err := json.MarshalIndent(cfg, "", "  ")
	if err != nil {
		return err
	}

	return os.WriteFile(configPath, newData, 0644)
}

func (a *App) loadEnabledWhitelistApps() []string {
	configPath := filepath.Join(a.v2raynPath, "whitelist-apps.json")
	data, err := os.ReadFile(configPath)
	if err != nil {
		return []string{}
	}

	var apps []WhitelistApp
	if err := json.Unmarshal(data, &apps); err != nil {
		return []string{}
	}

	result := []string{}
	for _, app := range apps {
		if app.Enabled && strings.TrimSpace(app.Name) != "" {
			result = append(result, strings.TrimSpace(app.Name))
		}
	}
	return result
}

func (a *App) loadEnabledWhitelistDomains() []string {
	configPath := filepath.Join(a.v2raynPath, "whitelist-domains.json")
	data, err := os.ReadFile(configPath)
	if err != nil {
		return []string{}
	}

	var domains []WhitelistDomain
	if err := json.Unmarshal(data, &domains); err != nil {
		return []string{}
	}

	result := []string{}
	for _, d := range domains {
		if d.Enabled && strings.TrimSpace(d.Domain) != "" {
			result = append(result, strings.TrimSpace(d.Domain))
		}
	}
	return result
}

func uniqueStrings(items []string) []string {
	seen := map[string]bool{}
	result := []string{}
	for _, item := range items {
		if item == "" || seen[item] {
			continue
		}
		seen[item] = true
		result = append(result, item)
	}
	return result
}

func (a *App) EnableTUNMode() error {
	configPath := filepath.Join(a.v2raynPath, "guiConfigs", "config.json")
	data, err := os.ReadFile(configPath)
	if err != nil {
		data = []byte(`{}`)
	}

	var config map[string]interface{}
	if err := json.Unmarshal(data, &config); err != nil {
		config = make(map[string]interface{})
	}

	config["tunModeEnabled"] = true

	newData, err := json.MarshalIndent(config, "", "  ")
	if err != nil {
		return err
	}

	return os.WriteFile(configPath, newData, 0644)
}

func (a *App) DisableTUNMode() error {
	configPath := filepath.Join(a.v2raynPath, "guiConfigs", "config.json")
	data, err := os.ReadFile(configPath)
	if err != nil {
		return nil
	}

	var config map[string]interface{}
	if err := json.Unmarshal(data, &config); err != nil {
		return nil
	}

	config["tunModeEnabled"] = false

	newData, err := json.MarshalIndent(config, "", "  ")
	if err != nil {
		return nil
	}

	return os.WriteFile(configPath, newData, 0644)
}

func (a *App) GetTUNModeStatus() map[string]interface{} {
	configPath := filepath.Join(a.v2raynPath, "guiConfigs", "config.json")
	data, err := os.ReadFile(configPath)
	if err != nil {
		return map[string]interface{}{"enabled": false}
	}

	var config map[string]interface{}
	if err := json.Unmarshal(data, &config); err != nil {
		return map[string]interface{}{"enabled": false}
	}

	enabled, ok := config["tunModeEnabled"].(bool)
	if !ok {
		enabled = false
	}

	return map[string]interface{}{"enabled": enabled}
}

// ==================== STATUS ФУНКЦИИ ====================

func (a *App) GetStatus() map[string]interface{} {
	return map[string]interface{}{
		"zapretRunning": a.zapretRunning,
		"v2raynRunning": a.v2raynRunning,
		"zapretPath":    a.zapretPath,
		"v2raynPath":    a.v2raynPath,
		"currentConfig": a.currentConfig,
	}
}

func (a *App) GetZapretFiles() map[string]interface{} {
	files := make(map[string]interface{})

	winwsPath := filepath.Join(a.zapretPath, "bin", "winws.exe")
	if _, err := os.Stat(winwsPath); err == nil {
		files["winws.exe"] = true
	} else {
		files["winws.exe"] = false
	}

	batPath := filepath.Join(a.zapretPath, a.currentConfig)
	if _, err := os.Stat(batPath); err == nil {
		files["currentConfig"] = true
	} else {
		files["currentConfig"] = false
	}

	sysPath := filepath.Join(a.zapretPath, "bin", "WinDivert64.sys")
	if _, err := os.Stat(sysPath); err == nil {
		files["WinDivert64.sys"] = true
	} else {
		files["WinDivert64.sys"] = false
	}

	return files
}

func (a *App) GetAvailableConfigs() []map[string]interface{} {
	configs := []map[string]interface{}{}

	if a.zapretPath == "" {
		return configs
	}

	pattern := filepath.Join(a.zapretPath, "*.bat")
	matches, err := filepath.Glob(pattern)
	if err != nil {
		return configs
	}

	sort.Strings(matches)
	for _, match := range matches {
		name := filepath.Base(match)
		if strings.EqualFold(name, "service.bat") {
			continue
		}

		label := name
		if name == "general.bat" {
			label = "General (Default)"
		} else if name == "general (ALT10).bat" {
			label = "General ALT10 (Recommended)"
		} else if name == "general (ALT11).bat" {
			label = "General ALT11 (Max Repeats)"
		} else if name == "general (ALT8).bat" {
			label = "General ALT8 (BadSeq)"
		}

		configs = append(configs, map[string]interface{}{
			"name":   name,
			"label":  label,
			"exists": true,
		})
	}

	return configs
}

func (a *App) SetConfig(configName string) error {
	if a.zapretRunning {
		return fmt.Errorf("stop Zapret before changing config")
	}

	path := filepath.Join(a.zapretPath, configName)
	if _, err := os.Stat(path); os.IsNotExist(err) {
		return fmt.Errorf("config file not found: %s", configName)
	}

	a.currentConfig = configName
	return nil
}

func (a *App) TestZapretConfig(configName string) map[string]interface{} {
	result := map[string]interface{}{
		"success": false,
		"message": "",
		"time":    "",
		"status":  "",
		"config":  configName,
	}

	if a.zapretRunning {
		result["message"] = "stop Zapret before testing config"
		result["status"] = "warning"
		return result
	}

	if err := a.runZapretBat(configName); err != nil {
		result["message"] = fmt.Sprintf("failed to start config: %v", err)
		result["status"] = "error"
		return result
	}
	a.zapretRunning = true

	time.Sleep(3 * time.Second)
	testResult := a.TestYouTube()
	for k, v := range testResult {
		result[k] = v
	}

	a.StopZapret()
	return result
}

// ==================== TEST ФУНКЦИИ ====================

func (a *App) TestYouTube() map[string]interface{} {
	result := map[string]interface{}{
		"success": false,
		"message": "",
		"time":    "",
		"status":  "",
	}

	start := time.Now()
	client := &http.Client{Timeout: 15 * time.Second}

	resp, err := client.Get("https://www.youtube.com")
	if err != nil {
		result["message"] = fmt.Sprintf("Connection failed: %v", err)
		result["status"] = "error"
		return result
	}
	defer resp.Body.Close()

	elapsed := time.Since(start)
	result["time"] = fmt.Sprintf("%d ms", elapsed.Milliseconds())

	if resp.StatusCode == 200 {
		result["success"] = true
		result["message"] = "YouTube is accessible!"
		result["status"] = "ok"
	} else {
		result["message"] = fmt.Sprintf("HTTP Status: %d", resp.StatusCode)
		result["status"] = "warning"
	}

	return result
}

func (a *App) TestDiscord() map[string]interface{} {
	result := map[string]interface{}{
		"success": false,
		"message": "",
		"time":    "",
		"status":  "",
	}

	start := time.Now()
	client := &http.Client{Timeout: 15 * time.Second}

	resp, err := client.Get("https://discord.com")
	if err != nil {
		result["message"] = fmt.Sprintf("Connection failed: %v", err)
		result["status"] = "error"
		return result
	}
	defer resp.Body.Close()

	elapsed := time.Since(start)
	result["time"] = fmt.Sprintf("%d ms", elapsed.Milliseconds())

	if resp.StatusCode == 200 {
		result["success"] = true
		result["message"] = "Discord is accessible!"
		result["status"] = "ok"
	} else {
		result["message"] = fmt.Sprintf("HTTP Status: %d", resp.StatusCode)
		result["status"] = "warning"
	}

	return result
}
