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

## 離線 Calibration Profile

0.3.0 新增 `ClassVista.Calibration` 函式庫，不依賴 OpenCV；目前僅有資料保存與驗證，尚未接入 WPF。
建構 Profile 時所有欄位均需明確指定。JSON 屬性使用與 C# 相同的 PascalCase，大小寫敏感，拒絕未知／重複欄位、缺少必要欄位及非有限數值。

| 欄位 | Schema v1 契約 |
|---|---|
| SchemaVersion | 必填整數 `1`；缺省不自動補版本，未知版本拒絕且不自動遷移 |
| ProfileId / CreatedUtc | 非空白識別及非預設的 UTC 時間 |
| RigId | 人工維護的架設版本；移動支架、鏡頭或變更光學設定後必須更新並重新校正 |
| Projection | 僅接受 `planar`，只是目前資料契約，並非最終場域投影決策 |
| PanoramaWidth / PanoramaHeight | 正整數，全景畫布像素尺寸 |
| ValidRegion | `X`、`Y`、`Width`、`Height` 均必填；以左上角為原點，範圍不能超出畫布 |
| Cameras | 至少一筆，ID 不可空白／重複；保留陣列順序，但相容性依 ID 配對 |
| DeviceId / Width / Height | 與取像設定相同的裝置識別與輸入解析度 |
| IntrinsicMatrix | 逐列排列的 3×3 內參，以像素為單位；正焦距及標準齊次格式 |
| DistortionCoefficients | OpenCV 針孔模型順序；4／5／8／12／14 個值，不支援 fisheye 模型 |
| Homography | 逐列排列的非退化 3×3 矩陣；由去畸變後、沿用原內參的影像像素座標映射至全景座標 |

`ValidRegion` 只是宣告的矩形；驗證通過不保證實際有效像素、無黑邊、接縫品質或足以容納 1920×1080 Viewport。
目前沒有 Undistort／Warp Map、Mask、投影運算或校正演算法。矩陣只做基本結構與退化檢查，不評估數值條件或校正誤差。
`RigId` 不會自動偵測位移，裝置 ID 與解析度相同也不代表光學狀態未變。

使用 `CalibrationProfileStore.SaveAsync(path, profile)` 保存；以 `LoadAsync(path)` 讀取並驗證結構。
啟動檢查的呼叫端可使用 `LoadCompatibleAsync(path, currentCameraSettings, currentRigId)`，要求相機集合、各路尺寸與支架識別一致；目前 UI 尚未呼叫此 API。
呼叫期間不要並行修改 Profile 中的陣列。

寫入先驗證並序列化，再寫入同目錄的唯一暫存檔，完成後替換原檔；取消或失敗會清理暫存檔，不默默回到預設校正。
此流程不提供備份或斷電耐久性保證。檔案路徑由呼叫端提供，建議日後使用 `%LOCALAPPDATA%/ClassVista/Calibration/`；目前不會自動建立實拍 Profile。
`CalibrationProfileException.Error` 區分 `InvalidJson`、`UnsupportedVersion`、`InvalidProfile`、`IncompatibleSetup`，並提供繁體中文訊息；檔案不存在、權限及取消保留 .NET 原生例外供呼叫端處理。

```powershell
dotnet test tests/ClassVista.Tests/ClassVista.Tests.csproj -c Release --filter FullyQualifiedName~CalibrationProfileTests
```

測試資料全為人工建立，不能用於真實 Camera，也不能作為 G0／G1／G2 通過證據。

## 目前範圍

WPF 0.2.0 是 Phase 0／1 的取像驗證原型；0.3.0 增加離線校正資料函式庫，仍不是完整 Panorama MVP。
核心支援多路來源，目前 UI 提供兩路。佇列接收影格後擁有其生命週期；取出的影格改由消費者釋放。

| 模組 | 狀態 |
|---|---|
| Camera.Abstractions | 影格、設定、介面及有界佇列 |
| Camera.Windows | DirectShow/OpenCV、模擬來源、背景工作、診斷報告 |
| Diagnostics | 有界指標樣本及快照 |
| App | WPF 雙路預覽及操作 |
| Calibration | 0.3.0 離線 Profile、JSON 讀寫及相容性 API；尚無實拍校正或 UI 整合 |
| FrameSync / Panorama.Core | 未實作，待 G1 實機閘門 |

到達時間使用 `Stopwatch` 單調時鐘；這不是攝影機感光時間，不能據此宣稱已量測端對端延遲。
G1 需要真實雙攝影機 30 分鐘報告，通過前不進入拼接實作。尚未完成 Media Foundation 後端比較、硬體格式能力列舉、USB 頻寬量測及硬體丟幀計數。
完整流程、待驗證條件及後續範圍見 [硬體驗證表](docs/HARDWARE-VALIDATION.md) 與 [執行計畫](plan.md)。