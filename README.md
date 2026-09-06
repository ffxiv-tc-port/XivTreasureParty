# XIV 寶物地圖工具小幫手

FFXIV 繁體中文服的寶物地圖協作 Dalamud 插件。與網頁版 [xiv-tc-treasure-finder](https://cycleapple.github.io/xiv-tc-treasure-finder/) 共用同一個 Firebase 後端，**遊戲內與網頁使用者可混合組隊**，即時同步同一份寶物地圖清單。

## 功能

- 建立／加入隊伍（8 碼代碼），即時同步成員狀態、寶物地圖清單、過期時間
- 手動新增寶物地圖（等級 → 地圖 → 字母編號位置）
- **打開寶物地圖時自動選取** — 讀取剛解碼的那張圖，自動預選下方欄位（仍需按「加入清單」才會推送到隊伍）
- **地圖按鈕** — 直接在遊戲內開啟地圖並將旗標設在寶物點
- **發送按鈕** — 以自訂頻道（例如 `/p`）發送「地圖 ( X , Y )」並附最近傳送水晶，含可點擊的地圖連結
- **聊天訊息座標自動轉換** — 收到隊友傳的純文字座標時，自動轉成可點擊的地圖連結
- 完成勾選、一鍵路線最佳化（地圖分組＋最近鄰居法，與網頁版一致）、順序調整（房主可鎖定）、備註、負責玩家
- 選配整合 **Mappy**：把隊伍清單中尚未完成的寶物點「全部」同時畫在地圖上（未安裝時不受影響）
- 暱稱自動取自遊戲角色名稱 + 伺服器（例如 `玩家@利維坦`）

## 安裝

1. 安裝 [XIVTCLauncher](https://github.com/ffxiv-tc-port/XIVTCLauncher)
2. 遊戲中輸入 `/xlsettings` 開啟 Dalamud 設定
3. 到「實驗性功能」的分頁
4. 找到「自定義插件倉庫」區塊，輸入：
   ```
   https://raw.githubusercontent.com/ffxiv-tc-port/DalamudPluginsTC/main/repo.json
   ```
5. 按下「+」並儲存

遊戲中輸入 `/xlplugins`，到「已安裝」的區塊即可找到並啟用 **寶物地圖工具小幫手**。

## 使用

- 遊戲內輸入 `/pth` — 切換主視窗開關

## 作者與支援

Made by cycleapple。與 [xiv-tc-treasure-finder](https://github.com/cycleapple/xiv-tc-treasure-finder) 共用同一個 Firebase schema。
加入 Discord 社群：<https://discord.gg/MR6W2EU2g6>
