# ClassVista 課堂全景視界

Windows 本機多攝影機取像與全景應用，依 [執行計畫](plan.md) 分階段開發。

## 開發

- Windows x64、.NET 10 LTS SDK。
- `dotnet test tests/ClassVista.Tests/ClassVista.Tests.csproj -c Release`

## 目前範圍

0.1.0 提供攝影機抽象、BGR 影格所有權、有界佇列與診斷核心。
核心介面不限制攝影機數量。佇列接收影格後擁有其生命週期；取出的影格改由消費者釋放。

到達時間使用 `Stopwatch` 單調時鐘；這不是攝影機感光時間，不能據此宣稱已量測端對端延遲。
G1 需要真實雙攝影機 30 分鐘報告，通過前不進入拼接實作。