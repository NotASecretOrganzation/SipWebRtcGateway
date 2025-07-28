# SIP-WebRTC Gateway

這是一個 SIP-WebRTC 網關系統，允許瀏覽器客戶端通過 WebRTC 與 SIP 系統進行通話，並支援客戶端之間的橋接通話。

## 功能特性

### 1. Session 管理
- 每個客戶端連接時會建立唯一的 session ID
- SIP transport 會綁定到對應的 session
- 伺服器允許客戶端進行 SIP 通話

### 2. 通話類型

#### 一般 SIP 通話
- 客戶端可以撥打外部 SIP URI（如 `sip:user@domain.com`）
- 伺服器建立 WebRTC session 與客戶端連接
- 伺服器建立 SIP session 與外部端點連接
- RTP 在 WebRTC 和 SIP 之間轉發

#### Alice 到 Bob 橋接通話
- 當 Alice 撥打 Bob 的 session ID（如 `sip:sessionId@domain.com`）時
- 伺服器建立兩個 WebRTC session：
  - Alice <-> 伺服器
  - 伺服器 <-> Bob
- RTP 在兩個 WebRTC session 之間轉發

## 系統架構

```
[Alice Browser] <--WebRTC--> [Gateway Server] <--WebRTC--> [Bob Browser]
                                    |
                                    v
                            [External SIP System]
```

## 安裝和運行

### 前置需求
- .NET 9.0
- 支援 WebRTC 的現代瀏覽器

### 安裝依賴
```bash
dotnet restore
```

### 運行伺服器
```bash
dotnet run
```

伺服器將在 `ws://localhost:8080/sip` 啟動 WebSocket 服務。

### 使用 Web 介面
1. 開啟瀏覽器訪問 `index.html`
2. 允許麥克風和攝影機權限
3. 輸入 SIP URI 進行通話

## 通話範例

### 撥打外部 SIP 端點
```
sip:user@example.com
```

### 撥打另一個 WebRTC 客戶端
```
sip:sessionId@domain.com
```
其中 `sessionId` 是目標客戶端的 session ID。

## 技術細節

### WebSocket 訊息類型
- `offer`: WebRTC offer
- `answer`: WebRTC answer  
- `ice-candidate`: ICE candidate
- `make-call`: 發起 SIP 通話
- `hang-up`: 掛斷通話
- `bridge-call`: 橋接通話通知
- `bridge-offer`: 橋接通話 offer
- `bridge-answer`: 橋接通話 answer
- `bridge-ice-candidate`: 橋接通話 ICE candidate
- `accept-bridge-call`: 接受橋接通話
- `reject-bridge-call`: 拒絕橋接通話

### RTP 轉發
- 音訊：PCMU 格式
- 視訊：H.263 格式
- 支援雙向即時轉發

### ICE 伺服器配置
- TURN 伺服器：`turn:172.27.200.242:3478`
- 使用者名稱：`username1`
- 密碼：`password1`

## 日誌
系統會將日誌寫入到 `logs/log.txt` 檔案中，並同時在控制台顯示。

## 故障排除

### 常見問題
1. **WebSocket 連接失敗**
   - 檢查伺服器是否正在運行
   - 確認端口 8080 未被佔用

2. **媒體設備無法存取**
   - 確認瀏覽器已授予麥克風和攝影機權限
   - 檢查是否有其他應用程式正在使用媒體設備

3. **通話無法建立**
   - 檢查 SIP URI 格式是否正確
   - 確認目標端點是否可用
   - 檢查網路連接和防火牆設定

### 除錯模式
啟動時會顯示詳細的連接和通話狀態日誌，可用於診斷問題。