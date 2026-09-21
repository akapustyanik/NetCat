# Third Party Notices

NetCat integrates and interacts with several third-party open-source components and datasets. Each component is governed by its respective upstream license.

---

### 1. sing-box
- **Upstream:** [https://github.com/SagerNet/sing-box](https://github.com/SagerNet/sing-box)
- **License:** GNU General Public License v3.0 (or later) with Open Source Exception
- **Usage:** Standalone binary (`modules/sing-box/sing-box.exe`). Used for universal proxy core and TUN network routing.

---

### 2. Xray-core
- **Upstream:** [https://github.com/XTLS/Xray-core](https://github.com/XTLS/Xray-core)
- **License:** Mozilla Public License 2.0 (MPL-2.0)
- **Usage:** Standalone binary (`modules/xray/xray.exe`). Used for VLESS, Trojan, and advanced proxy protocols.

---

### 3. Zapret / Flowseal zapret-discord-youtube
- **Upstream:** [https://github.com/bol-van/zapret](https://github.com/bol-van/zapret) and [https://github.com/Flowseal/zapret-discord-youtube](https://github.com/Flowseal/zapret-discord-youtube)
- **License:** MIT License
- **Usage:** Standalone binaries and strategy scripts (`modules/zapret/winws.exe`, `modules/zapret/*.bat`). Used for DPI circumvention for YouTube and Discord.

---

### 4. WinDivert
- **Upstream:** [https://github.com/basil00/Divert](https://github.com/basil00/Divert)
- **License:** GNU Lesser General Public License v3.0 (LGPL-3.0) / GNU General Public License v2.0 (GPL-2.0)
- **Usage:** Driver and library (`modules/zapret/WinDivert64.sys`, `modules/zapret/WinDivert.dll`). Used by `winws.exe` for packet capture and diversion.

---

### 5. OpenVPN
- **Upstream:** [https://github.com/OpenVPN/openvpn](https://github.com/OpenVPN/openvpn)
- **License:** GNU General Public License v2.0 (GPL-2.0)
- **Usage:** Standalone binary (`modules/openvpn/openvpn.exe`, version 2.6 branch with Wintun support). Used for OpenVPN protocol connections.

---

### 6. Wintun
- **Upstream:** [https://www.wintun.net/](https://www.wintun.net/)
- **License:** MIT License
- **Usage:** Dynamic link library (`modules/wintun/wintun.dll`, `modules/sing-box/wintun.dll`). Used for high-performance Windows TUN interface creation.

---

### 7. GeoIP / GeoSite Data (Loyalsoldier)
- **Upstream:** [https://github.com/Loyalsoldier/v2ray-rules-dat](https://github.com/Loyalsoldier/v2ray-rules-dat)
- **License:** CC-BY-SA 4.0 (Creative Commons Attribution-ShareAlike 4.0 International)
- **Usage:** Rule data files (`modules/geoip/geoip.dat`, `modules/geosite/geosite.dat`). Used for domain and IP geolocation routing.

---

### 8. tg-ws-proxy & Embedded Python Runtime
- **Upstream:** [https://github.com/Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy) and [https://www.python.org/](https://www.python.org/)
- **License:** MIT License (tg-ws-proxy) / Python Software Foundation License (Python 3.12 embedded)
- **Usage:** Standalone runtime directory (`modules/tg-runtime/`, `modules/tg-ws-proxy/`). Used as an optional local WebSocket proxy for Telegram MTProto.
