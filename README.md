# ClassVista 課堂全景視界

Windows 本機多攝影機取像與全景應用，依 [執行計畫](plan.md) 分階段開發。

## 開發

- Windows x64、.NET 10 LTS SDK。
- 若原生 OpenCV 載入失敗，安裝 Microsoft Visual C++ 2015–2022 x64 Redistributable。

```powershell
dotnet build ClassVista.sln -c Release
dotnet test ClassVista.sln -c Release
dotnet run --project src/ClassVista.App -c Release
```

測試會開啟使用暫存設定的 WPF 視窗，只使用模擬來源，不會取用實體攝影機或修改使用者設定。
截圖及短程診斷報告位於 `artifacts/validation/`。無桌面工作階段的 CI 可用
`dotnet test ClassVista.sln -c Release --filter FullyQualifiedName!~PreviewTests` 執行非 UI 測試；不等同完成 UI 驗證。

## 操作

1. 初次啟動預選左右模擬來源，按「開始取像」即可確認雙路影像與診斷。
2. 停止後按「重新列舉」，分別選擇兩台不同的實體 Camera。程式不會自動開啟實體攝影機。
3. 選擇 MJPG 或 YUY2；目前 UI 固定要求 1920×1080、30 FPS。完整模式列舉尚未實作。
4. 依原廠驅動的單位與有效範圍填入 Exposure、White Balance、Focus。留空不更動該設定，不能視為已鎖定。
5. 啟動後查看每路狀態及 FPS；停止後從「報告資料夾」取得 JSONL 紀錄。

設定儲存在 `%LOCALAPPDATA%/ClassVista/cameras.json`，報告位於同目錄的 `Reports/`。
設定使用暫存檔替換；格式錯誤時顯示提示並回到模擬預設。再次成功啟動時會儲存目前設定。
裝置路徑是 Windows DirectShow 識別資訊，換 USB 孔或驅動後仍可能變更，需重新指定；不會自動改接其他裝置。
手動控制的 `Set` 成功不代表硬體確實鎖定，必須在實機確認。未接受的設定會顯示警告。

## 診斷

- `Frames`、`AverageFps`：自開始至目前的成功擷取數與平均 FPS，包含中斷及重連耗時。
- `RecentFps`、`ReadP95Ms`：最多最近 120 次成功讀取的 FPS 與讀取耗時 P95。
- `FrameAgeMs`：最後成功讀取的主機到達時間距今多久，不是感光或顯示延遲。
- `ReadFailures`：開啟／讀取嘗試失敗次數，不等於遺失的硬體影格數。
- `PreviewQueueDiscards`：佇列滿載、取最新影格及結束清空時的軟體丟棄數，不可直接當 G1 硬體丟幀率。
- 每個影格具有序號及單調時鐘到達時間，報告只保存每秒彙總，不逐幀落盤。
- JSONL 第一行為設定／平台資訊，後續為每秒 CPU、記憶體及各路指標，正常結束有 `final` 行。

CPU 是本程式用量除以邏輯處理器數；未量測 GPU 或 USB 控制器頻寬。
報告包含本機裝置識別資訊，分享前請檢查。沒有錄影、音訊、遠端傳輸或自動刪除報告；請自行管理保存期間。

## 封裝

```powershell
dotnet publish src/ClassVista.App/ClassVista.App.csproj -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```

將整個輸出資料夾部署到 Windows x64，執行 `ClassVista.App.exe`。不只複製 EXE；OpenCV 原生 DLL 也必須保留。
自含部署不需要另裝 .NET Desktop Runtime；仍需符合 OpenCV 原生執行環境需求。

## 故障排除

- 找不到攝影機：檢查 USB、Windows 桌面應用程式相機權限，再重新列舉。
- 開啟失敗：關閉可能占用裝置的會議軟體，確認兩路沒有選到同一個 Camera。
- 解析度錯誤：目前原型會拒絕不符合輸入尺寸的影格並重試；先確認硬體支援 1080p。
- 幀率偏低：檢查驅動回報、MJPG/YUY2 及 USB Host Controller；依硬體驗證表記錄。
- 停止超過 5 秒：OpenCV 的原生讀取不保證支援取消。介面保持回應但等待驅動返回，可嘗試拔除裝置；仍卡住時需終止程式，報告可能缺少 `final`。正式強制中止保障需後續行程隔離或替換後端。
- 不可將模擬測試 FPS 或佇列丟棄率作為 G1 驗收證據。

## 目前範圍

0.2.0 是 Phase 0／1 的取像驗證原型，不是完整 Panorama MVP。
核心支援多路來源，目前 UI 提供兩路。佇列接收影格後擁有其生命週期；取出的影格改由消費者釋放。

| 模組 | 狀態 |
|---|---|
| Camera.Abstractions | 影格、設定、介面及有界佇列 |
| Camera.Windows | DirectShow/OpenCV、模擬來源、背景工作、診斷報告 |
| Diagnostics | 有界指標樣本及快照 |
| App | WPF 雙路預覽及操作 |
| FrameSync / Calibration / Panorama.Core | 未實作，待 G1 實機閘門 |

到達時間使用 `Stopwatch` 單調時鐘；這不是攝影機感光時間，不能據此宣稱已量測端對端延遲。
G1 需要真實雙攝影機 30 分鐘報告，通過前不進入拼接實作。尚未完成 Media Foundation 後端比較、硬體格式能力列舉、USB 頻寬量測及硬體丟幀計數。
完整流程、待驗證條件及後續範圍見 [硬體驗證表](docs/HARDWARE-VALIDATION.md) 與 [執行計畫](plan.md)。