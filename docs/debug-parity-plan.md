# Go 偵錯功能規劃表(VS 18 + DAH + dlv dap)

> **實作進度附註(2026-08-01)**
>
> - **P0 快贏批次:已完成並驗證**——例外設定(panic 中斷)、條件/函式/命中次數/位址/呼叫堆疊中斷點、`hideSystemGoroutines`、Locals scope 名稱、delve 升級 1.27.0、Go 工具鏈升至 1.25.12。
> - **Attach to Process:未完成(卡住,需要新資訊)**。已完成的部分:`GoAdapterLauncher`(`IAdapterLauncher`,`UpdateLaunchOptions` 產生 `{"$debugServer":port,"request":"attach","mode":"local","processId":N}`)、`DelveServer` 抽出供 F5/attach 共用、COM CLSID 註冊。**卡點**:透過 Go engine attach 一律以 `HRESULT 0x8971001E` 失敗,且失敗發生在 **adapter launcher 被呼叫之前**——dlv 從未啟動、ActivityLog 無記錄、DAH 協定無流量。對照組:同一個行程用 **Native engine attach 成功**,證明目標與 attach 管線本身沒問題。已嘗試且皆無效的註冊變體:(1) `AdapterLauncher` metric 值、(2) `ExtensibilityObjects` 編號子鍵、(3) `PortSupplier` 由單值改為編號子鍵列表。
>   **下一步方向**:0x8971001E 未見於 msdbg.h,需要更底層的診斷——建議用 VS 的 debugger ETW/DebugDiag 追 `IDebugEngine2::Attach` 呼叫鏈,或改以最小 DAH sample engine 反推缺少的 metric(可能與 `Programs`/`ProgramProvider`/`CodeType` 宣告有關,DAH 的 attach 是否需要額外的 program provider 尚未查清)。
>   目前 `Attach=0`,避免在「附加至處理序」給出一個必定失敗的程式碼類型。
> - **反組譯視窗:可用**(以 DAP client 實測:`disassemble` 回傳真實 Go 組語)。無須額外註冊——dlv 回報 `supportsDisassembleRequest=true`,DAH 於 initialize 後執行期開啟 `Disassembly`;`AddressBP=1` 已靜態開啟,可在反組譯視窗內下中斷點。
> - **記憶體視窗:部分可用(dlv 的設計限制,非我方缺陷)**。實測 dlv 1.27.0 的 `readMemory`:
>   - `name`(string)→ `memoryReference=0x7ff76062bfc5` → 讀取成功(回傳 base64 = 字串內容)✔
>   - `numbers`([]int)→ `memoryReference=0xc0000b6000` → 有效 ✔
>   - `counter`(int)→ **完全沒有 memoryReference** ✘
>   - 任意原始位址(= 在記憶體視窗手動輸入位址)→ `Unable to read memory: unknown memoryReference` ✘
>
>   根因在 `service/dap/server.go`:`readMemory` 只接受 dlv 自己登記過的參考(`referencesCollection.get`),而 `isAddressable()` **只對 `reflect.Slice` 與 `reflect.String` 回 true**——只有這兩類變數會在 `variables` 回應中得到 `memoryReference`。VS 記憶體視窗手動輸入的位址是 VS 自行計算的,不在 dlv 表中,必然被拒。
>   **使用方式**:對 string 或 slice 變數在 Locals/監看視窗右鍵→「檢視記憶體」才有效;手動輸入位址與其他型別皆不支援。要放寬需上游 PR(讓 dlv 接受任意位址,DAP 規範本身是允許的)。

## 一、中斷點類

| 功能 | 目前 Go 狀態 | 缺口層 | 具體行動 | 優先級 |
|---|---|---|---|---|
| 一般中斷點 | 已可用 | — | 無 | — |
| 條件中斷點 | 已可用(dlv≥1.5.1 有 `supportsConditionalBreakpoints`,DAH 執行期自動翻開 `ConditionalBP`) | — | 建議 pkgdef 仍靜態寫 `ConditionalBP=1` 保險 | P0 |
| 命中次數中斷點 | 需設定 | engine 註冊 + delve 版本 | pkgdef 加 `HitCountBP=1` + `HitCountBreakpointExpressions` 區段(=、>=、% 樣板);1.21.2 功能在但 capability 旗標不在(1.26.1 #4230 才加)→ 同步升級 delve ≥1.26.1 以免 DAH 依旗標 gate | P0 |
| 篩選條件(ThreadId 等) | 受限 | DAH/DAP | DAP 無對應;dlv 條件式可用 `runtime.curg.goid==N` 部分模擬,無 UI。不做 | P3 |
| 函式中斷點 | 需設定 | 我們的 engine 註冊 | pkgdef 加 `FunctionBP=1`(dlv≥1.6.1 已支援 `setFunctionBreakpoints`,1.21.2 可用) | P0 |
| 資料中斷點(值變更時中斷) | 不可能(Windows) | delve 根本性 | dlv 1.26.2 起有 watchpoints 但**僅 linux/darwin**(server.go L1018-1024),Windows 任何版本皆 false。無解 | P3 |
| 相依中斷點 / 暫時中斷點 / 只中斷一次 | 已可用(推定) | — | SDM 客戶端邏輯(以一般 BP + 自動移除/啟用實作),不經 DAP。驗證即可 | P0(驗證) |
| 追蹤點 / Tracepoint | 已可用(推定) | — | DAH 從不送 `logMessage`;VS 追蹤點由 SDM 以一般 BP + `evaluate` 客戶端實作,dlv 的 evaluate 已驗證可用。驗證 `{運算式}` 插值即可 | P0(驗證) |
| 中斷點標籤/匯出匯入/視窗管理 | 已可用 | — | 純 SDM 客戶端功能 | — |
| 位址(反組譯/呼叫堆疊)中斷點 | 需設定 | 我們的 engine 註冊 | `AddressBP=1`(不在 DAH 執行期覆寫的 9 項內,須靜態開);`CallStackBP` 會被 `supportsInstructionBreakpoints=true` 執行期翻開,靜態寫 1 亦可。dlv≥1.7.3,1.21.2 可用 | P1 |
| 內嵌(同行子陳述式)中斷點 | 不可能 | delve | dlv 不支援 `breakpointLocations`(回 unsupported)。無解 | P3 |

## 二、執行控制

| 功能 | 目前 Go 狀態 | 缺口層 | 具體行動 | 優先級 |
|---|---|---|---|---|
| F5 / 逐步 / Step Out / Break All / Stop | 已可用 | — | 無 | — |
| Ctrl+F5(開始但不偵錯) | 需設定 | 我們的 engine 註冊 | pkgdef 加 `UseEngineForNonDebugLaunch=1`,launch config 走 `noDebug:true`(dlv 支援 noDebug) | P0 |
| 重新啟動偵錯(Ctrl+Shift+F5) | 已可用 | — | DAH 不轉發 `restart`;VS 重啟=SDM 整個 session 砍掉重來,與 delve restart 支援無關 | — |
| 執行至游標處 / Run to Click | 已可用(推定) | — | SDM 以暫時中斷點實作,驗證即可 | P0(驗證) |
| **設定下一個陳述式(拖移黃箭頭)** | **不可能** | **delve(唯一缺口)** | 見下方專節。維持 `SetNextStatement=0` | P3 |
| Step Into Specific | 不可能 | DAH + delve 雙重 | DAH 的 `stepInTargets` 只進遙測從不發送;dlv 也回 unsupported。雙層無解 | P3 |
| 顯示下一個陳述式 | 已可用 | — | SDM 客戶端 | — |
| 僅我的程式碼(JMC) | 不可能 | DAH/delve | `JustMyCodeStepping` 為傳統 metric,DAH 無實作;dlv 無 JMC 概念。折衷:launch 設定 `hideSystemGoroutines:true` 隱藏系統 goroutine(≥1.7.3) | P3(折衷 P0) |
| 指令級步進 | 已可用 | — | `supportsSteppingGranularity=true`(1.21.2),反組譯視窗內步進可動 | P1(隨反組譯) |
| 凍結/解凍執行緒下單執行緒步進 | 不可能 | DAH 根本性 | wiki 明文:全執行緒必須同進同出 break mode。無解 | P3 |
| 倒退執行 / Step Back | 不可能(Windows) | delve(rr backend 僅 Linux) | 無解 | P3 |

## 三、檢視類

| 功能 | 目前 Go 狀態 | 缺口層 | 具體行動 | 優先級 |
|---|---|---|---|---|
| 區域變數 / Autos / Watch / QuickWatch / DataTips / 懸停 | 已可用 | — | `supportsEvaluateForHovers=true`;可加 `LocalsScopeName`/`ArgsScopeName` 對映 dlv 的 Locals/Arguments scope 讓 Args 正確分欄 | P0(微調) |
| 改變數值(Locals/Watch 內) | 已可用 | — | `supportsSetVariable=true`(1.21.2);`setExpression` dlv 不支援 → 只能改「變數」不能改任意運算式 | — |
| 即時運算視窗 | 已可用(受限) | DAH | evaluate repl context 可用,含 `call f(x)` 函式呼叫注入(僅 topmost frame)與 `dlv <cmd>` console 命令;IntelliSense 不可能(DAH 的 `completions` 只進遙測) | — |
| 釘選 DataTips / 可釘選屬性 | 已可用(推定) | — | SDM 客戶端 + DAH `addFavorite/removeFavorite`(SupportsObjectFavorites 執行期覆寫)。驗證 | P1(驗證) |
| 視覺化檢視器(文字/JSON) | 受限 | DAH/SDM | 字串完整值可經 clipboard context 取得;IEnumerable 表格檢視為 .NET 專屬。不特別做 | P2 |
| 內嵌值顯示(行尾灰字) | 受限(待驗證) | DAH/語言服務 | 研究未見 DAH 支援證據;實測確認,不通則放棄 | P2 |
| 十六進位顯示 | 已可用 | — | SDM 格式化 | — |
| Make Object ID | 需驗證 | delve | DAH 有 VS 擴充 `createObjectId/destroyObjectId` + SupportsObjectId 執行期覆寫,但 dlv 未實作該 VS 擴充 → 預期不可用 | P3 |
| 記憶體視窗 | 需升級 | delve 版本 | `readMemory` 需 dlv ≥1.26.0(#4083);寫入需 1.27.0(#4364)。升級 delve 即通(DAH initialize 已送 `SupportsMemoryReferences=true`) | P1 |
| 反組譯視窗 | 需設定 | engine 註冊(執行期會翻開) | `supportsDisassembleRequest=true`(1.21.2)→ DAH 執行期覆寫 `Disassembly`;驗證 + 搭配 AddressBP | P1 |
| 暫存器視窗 | 不可能 | DAH 根本性 | DAH 完全無實作,`Registers=1` 無效;DAP 亦無此 request。無解 | P3 |
| DebuggerDisplay 類自訂顯示 | 不適用 | — | Go 無屬性標註;dlv 有 config 內建格式 | — |

## 四、狀態類

| 功能 | 目前 Go 狀態 | 缺口層 | 具體行動 | 優先級 |
|---|---|---|---|---|
| 呼叫堆疊 / 執行緒(goroutines) | 已可用 | — | 加 launch 選項 `hideSystemGoroutines`、`goroutineFilters`(≥1.8.0)、`showPprofLabels`(≥1.22.0)提升體驗 | P0 |
| 平行堆疊 | 已可用(推定) | — | SDM 以全執行緒 stackTrace 繪製;goroutine 多時 dlv 只載入部分。驗證大量 goroutine 情境 | P1(驗證) |
| 工作(Tasks)/ 平行監看 | 不適用 / 受限 | 根本性 | Tasks 為 TPL 專屬;平行監看理論上可動(逐執行緒 evaluate),驗證 | P2 |
| 模組視窗 | 不可能 | delve | dlv 對 `modules` request 回 unsupported(任何版本)。無解;可設 `SuppressModulesRequestOnAttach` 減噪 | P3 |
| 處理序視窗 | 已可用(多 session 時) | — | SDM 管理 | — |
| 例外設定(panic/fatal throw) | 需設定 | 我們的 engine 註冊 | pkgdef 加 `Exceptions=1` + `ExceptionBreakpointCategory`/`ExceptionBreakpointMappings`(對映 `unrecovered-panic`、`fatal-throw`,兩者 dlv 預設 true)+ `AD7Metrics\Exception\{分類}` 註冊;Exception Helper 用 `exceptionInfo`(1.21.2 已支援) | **P0** |
| 例外條件(依模組略過) | 不可能 | delve | 需 `SupportsExceptionConditions`,dlv 無 filter options(`supportsExceptionFilterOptions=false`)。維持 `ExceptionConditions=0` | P3 |
| 診斷工具(CPU/記憶體圖表) | 受限 | 根本性 | .NET profiler 掛勾;Go 應改推 pprof 整合(另案) | P2 |
| 輸出視窗 | 已可用 | — | OutputEvent 已通 | — |

## 五、編輯類

| 功能 | 目前 Go 狀態 | 缺口層 | 具體行動 | 優先級 |
|---|---|---|---|---|
| Edit and Continue / Hot Reload | 不可能 | 根本性(DAH 無 ENC 實作 + Go 編譯模型無此能力) | 無解;折衷=快速重啟(delve ≥1.25.1 restart+rebuild 對 DAH 也用不到,VS 重啟已夠快) | P3 |

## 六、其他

| 功能 | 目前 Go 狀態 | 缺口層 | 具體行動 | 優先級 |
|---|---|---|---|---|
| 附加至處理序 | 需實作 | 我們的 engine 註冊 + 程式碼 | pkgdef `Attach=1` + `PortSupplier`(本機 PID 可用預設)+ IAdapterLauncher(經 `ExtensibilityObjects` 區段,`AdapterLauncher` metric 已 deprecated)實作 `UpdateLaunchOptions` 產生 `{"request":"attach","mode":"local","processId":N}`。dlv local attach 自 1.6.0 起 Windows 可用(1.21.2 OK);升級 ≥1.23.0 加碼 `waitFor` | **P1** |
| 重新附加(Shift+Alt+P) | 隨 Attach | 同上 | Attach 通了即有 | P1 |
| 多目標啟動 | 已可用(推定) | — | 方案層級,SDM 起多 session。驗證 | P1(驗證) |
| 子處理序偵錯 | 受限 | DAH | dlv 1.26.0 有 follow-exec(`dlv target` console 命令),但 DAH 無自動附加子行程管線;僅 console 手動 | P2 |
| 遠端偵錯 | 需設定 | launch 設定 | launch profile 支援 `mode:"remote"` 連 headless dlv(`--headless --accept-multiclient`,≥1.7.3);做一個 launchSettings profile 樣板 | P2 |
| Docker/WSL/SSH | 需實作 | 我們+launch | 以 remote attach 為基礎包裝 | P2 |
| 傾印檔偵錯 | 不可能(Windows 實務上) | delve + DAH 雙重 | dlv core 模式 Windows minidump 支援有限,且 DAH 無 Dump metric 實作 | P3 |
| TTD / 快照偵錯 | 不可能 | 根本性 | rr 僅 Linux;無解 | P3 |
| Source Link / 反編譯 | 不適用 | — | Go module cache 原始碼皆在;做好 dlv `substitutePath` 對映即可 | P2 |
| JIT(當機自動附加) | 不可能 | DAH 無實作 | 無解 | P3 |
| launchSettings profiles(引數/環境變數/工作目錄) | 已可用/需設定 | 我們的 launch provider | 確保 profile → launch JSON 對映 `args`/`env`/`cwd`/`buildFlags` | P0 |

---

## 專節:「拖移黃箭頭(Set Next Statement)」能不能做?

**不能(目前與可見未來都不能),缺口 100% 在 delve,且僅在 delve。**

- DAH 側**完整實作**:VS 拖黃箭頭 → `gotoTargets` → `goto`(ThreadManager.cs:65、DebuggedThread.cs:326-334),甚至有反組譯層級的 instructionReference goto 擴充。DAH 不是缺口。
- delve 側:`GotoRequest`/`GotoTargetsRequest` 在 master(1.27.0)仍回 `sendUnsupportedErrorResponse`(server.go L916、L922),`supportsGotoTargetsRequest=false`,任何版本皆同。
- 就算 pkgdef 硬寫 `SetNextStatement=1` 也沒用:DAH 收到 initialize response 後會以 `AD7EngineMetricsUpdatedEvent` 依 `supportsGotoTargetsRequest=false` **執行期覆寫回 0**。
- 根因在 delve 底層:delve 連 CLI 都沒有 jump/set-pc-to-line 功能(改 PC 會破壞 Go runtime 的 goroutine 堆疊/GC 假設,上游多年未做)。唯一路徑是**上游貢獻 delve**(先實作 debugger 層 jump,再接 DAP goto),工程量大、風險高。
- **結論:維持 `SetNextStatement=0`,列 P3 不做;若要投資,方向是開 delve upstream issue/PR,而非本專案內能解。**

---

## 立即可做的快贏清單(只改 engine 註冊 / launch 設定,不寫新程式碼)

| # | 動作 | 效果 |
|---|---|---|
| 1 | pkgdef:`Exceptions=1` + `ExceptionBreakpointCategory`/`ExceptionBreakpointMappings` + `AD7Metrics\Exception` 註冊 unrecovered-panic、fatal-throw | 例外設定視窗 + panic 時 Exception Helper(1.21.2 即可) |
| 2 | pkgdef:`FunctionBP=1` | 函式中斷點(Ctrl+K,B)(1.21.2 即可) |
| 3 | pkgdef:`HitCountBP=1` + `HitCountBreakpointExpressions` 區段 | 命中次數中斷點(1.21.2 功能在;為避免旗標 gate,建議搭配升級) |
| 4 | pkgdef:`ConditionalBP=1`、`AddressBP=1`、`CallStackBP=1`(後兩項搭反組譯) | 條件 BP 保險、呼叫堆疊/反組譯設 BP |
| 5 | pkgdef:`UseEngineForNonDebugLaunch=1` + launch `noDebug:true` | Ctrl+F5 正常走 dlv 啟動 |
| 6 | pkgdef:`LocalsScopeName`/`ArgsScopeName` 對映 dlv scopes | Locals/引數正確分欄 |
| 7 | launch 設定:`hideSystemGoroutines:true`(預設開)、暴露 `goroutineFilters` | 執行緒視窗不被 runtime goroutine 淹沒 |
| 8 | launch 設定:profile → `args`/`env`/`cwd`/`buildFlags` 對映補齊 | launchSettings 體驗對齊 C# |
| 9 | 驗證(零成本):追蹤點、Run to Cursor、暫時 BP、BP 標籤/匯出、平行堆疊 | 這些是 SDM 客戶端實作,理論上已通 |

## 建議實作順序

1. **P0 批次(上表快贏 1–9)**——純 pkgdef/launch 設定 + 驗證,一次 PR。
2. **升級 delve 1.21.2 → 1.27.0**——解鎖 hitCondition capability 旗標(1.26.1)、readMemory(1.26.0)、writeMemory(1.27.0)、waitFor attach(1.23.0)、examinemem/target console 命令;回歸測試既有 F5 流程。
3. **Attach(P1 最大缺口)**——`Attach=1` + PortSupplier + ExtensibilityObjects 的 IAdapterLauncher 產 attach JSON;附帶 Reattach、waitFor。
4. **反組譯 + 記憶體視窗 + 指令級步進(P1)**——升級後驗證 Disassembly/readMemory 全鏈路,靜態開 AddressBP。
5. **P2 批次**——remote attach profile 樣板、substitutePath、平行監看/內嵌值驗證、pprof 整合評估。
6. **不做(P3,明文記錄原因)**——Set Next Statement(delve 無 goto)、EnC/Hot Reload(根本性)、資料中斷點(Windows 永不支援)、暫存器視窗(DAH 無實作)、Step Into Specific(雙層缺)、單執行緒步進(DAH 根本限制)、模組視窗(delve 無 modules)、TTD/StepBack(rr 僅 Linux)、JIT attach、傾印檔。
