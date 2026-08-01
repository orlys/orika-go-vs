# 踩坑記錄:在 Visual Studio 上做 Go 開發平台

這份文件記錄實際踩過的坑——**症狀、根因、解法、以及當初是怎麼查出來的**。目的不是描述現在的架構(那在 README),而是讓下一次不要重走同樣的死路。

規則:每一條都是真的踩過並解決(或明確卡住)的,不是理論上的注意事項。

---

## 一、VSIX 命令表與套件註冊

### 1. 命令永遠不出現,而且幾乎沒有錯誤訊息

一個右鍵命令從「寫完」到「真的出現在選單」,中間有**三道獨立關卡**,少任何一道都是靜默失敗:

| 關卡 | 症狀 | 解法 |
|---|---|---|
| vsct 沒嵌進組件 | ActivityLog 只有一行 `Error loading UI library ... 0x800a006f` | 必須有 `VSPackage.resx`,並以 `MergeWithCTO=true` + `ManifestResourceName=VSPackage` 的 `EmbeddedResource` 宣告;VSSDK 才會把編譯後的 `.cto` 併進 `VSPackage.resources` 的 `Menus.ctmenu` |
| 套件本身載不起來 | 完全無訊息;命令不在命令表中 | csproj 加 `<RegisterWithCodebase>true</RegisterWithCodebase>`。預設產生的 pkgdef 只寫組件顯示名稱(`PublicKeyToken=null`),非 GAC 的擴充組件無從解析。**注意:C# attribute 上的 `RegisterUsing = RegistrationMethod.CodeBase` 不會改變 CreatePkgDef 的輸出**,只有 csproj 屬性有效 |
| 選單合併結果被快取 | 修好了前兩項仍看不到命令 | shell 依 `ProvideMenuResource("Menus.ctmenu", N)` 的**版本號**快取合併結果,同版本不重讀。改 vsct 後把 N +1;`install-vsix.ps1` 另外每次安裝都跑 `devenv /updateconfiguration` |

**驗證方式**(不要靠肉眼看選單):
```powershell
$dte.Commands.Item('{命令集GUID}', 命令ID)   # 解析得到就代表在命令表裡
```
成功時會回傳像 `OtherContextMenus.GoDependencies.AddGoModuleReference` 這樣的完整名稱。

### 2. 內建命令無法逐項從共用選單移除

Dependencies 節點的右鍵選單其實是 shell 的 `IDM_VS_CTXT_REFERENCEROOT`(0x0450),「加入專案參考」「管理 NuGet 套件」都是**別人放在同一個共用選單上的 placement**,無法一個個拿掉。

解法:用 `IProjectItemContextMenuProvider`(`AppliesTo("OrikaGo")` + 較高 `[Order]`)把該節點**整個換成自己的私有 context menu**——這正是 managed 專案系統自己的做法(`DependenciesContextMenuProvider`,它以較低 Order 把樹節點映射到 shell 選單)。子節點不處理就會自然落回預設 provider。

### 3. VSCT 多語系

屬性名是**小寫** `language`,同一個 Button 下可以有多個 `<Strings>` 區塊:

```xml
<Strings><ButtonText>Add Go &amp;Module Reference...</ButtonText></Strings>
<Strings language="zh-TW"><ButtonText>加入 Go 模組參考(&amp;M)...</ButtonText></Strings>
```
無 `language` 的那組是 fallback,必須存在。程式碼側的字串則跟隨 `CultureInfo.CurrentUICulture`。

---

## 二、CPS / MSBuild SDK capability

### 4. Solution Explorer 一片空白,要按「顯示所有檔案」才看得到

**根因極度反直覺**:`Microsoft.NET.Sdk.DefaultItems.props` 先 `None Include="**/*"`,再 `None Remove="**/*$(DefaultLanguageSourceExtension)"` 想移除語言原始碼。非語言專案(`.goproj`)沒有語言 props,該屬性是**空字串**,於是那行 Remove 變成 `Remove="**/*"`——把剛建好的 None 清單整個抹掉,專案評估後**零個項目**。

解法:在巢狀 import 之後(抹除發生之後)以相同 Exclude 重跑一次 None glob。

驗證:`dotnet msbuild x.goproj -getItem:None` 應該回完整清單而非 `[]`。

### 5. 移除 capability 的連鎖後果

| 移除的 capability | 後果 | 對策 |
|---|---|---|
| `PackageReferences` | VS 不再對專案執行 NuGet restore → 每次建置 `NETSDK1004: Assets file ... not found` | 設 `SkipResolvePackageAssets=true`(官方開關);Go 專案本來就不需要 assets file |
| `LaunchProfiles` | **`Debug.Start` 命令整個消失**(F5 的底層設施就在那個子系統) | 不要移除。改用 `IDebugProfileLaunchTargetsProvider` 掛進它的管線 |
| `AssemblyReferences` / `COMReferences` / `WinRTReferences` | 相依性節點下的 .NET 參考子節點消失(想要的效果) | 安全。但 `ProjectReferences` 要保留,goproj 之間的專案參考是支援的 |

**教訓**:每移除一個 capability,先在真實 VS 裡開一次專案並建置,再往下做。這裡曾因為連續改動而誤以為專案載入壞了。

### 6. F5 永遠走不到自訂的 launch provider

有 `LaunchProfiles` capability 時,managed 管線**只諮詢 `IDebugProfileLaunchTargetsProvider`**;純 `IDebugLaunchProvider` 匯出永遠不會被問到,結果就是「F5 有反應但跑的是預設引擎、中斷點不綁定」。

解法:同時實作並匯出兩者,`[Order]` 要蓋過內建的 `ProjectLaunchTargetsProvider`。所需組件(`Microsoft.VisualStudio.ProjectSystem.Managed.VS`)**NuGet 上沒有可用版本**(停在 2017 年的 2.x),要從 VS 安裝目錄直接參考(`Private=false`)。

---

## 三、delve / DAP 偵錯

### 7. `dlv dap` 只講 TCP

沒有 stdio 模式。所以 **不能**在 engine 註冊裡寫 `"Adapter"` 讓 Debug Adapter Host 去 spawn 它(會在握手時卡死)。正確做法:自己啟動 `dlv dap --listen=127.0.0.1:<port>`,再用 launch 設定的 `$debugServer` 把 port 交給 host,host 會改為「連線」而非「啟動」。

### 8. 不要用 TCP 試連來偵測 dlv 就緒

`dlv dap` 只接受**單一** client,試連會把 session 吃掉。用 OS 的 listener 表判斷:

```csharp
IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
```

另外:port 要**自己先挑**(bind :0 拿到後釋放),因為 dlv 必須在無重導 stdio 的情況下啟動,沒有管道可讀它印出的 port。

### 9. F5 沒有主控台視窗

`CreateNoWindow = true` + 重導管線會讓 debuggee 完全無 stdio,程式的 `fmt.Println` 只會被塞進 Output 視窗、`fmt.Scan` 無從輸入。debuggee 繼承 dlv 的主控台,所以 **F5 時 dlv 必須帶可見主控台啟動**(attach 則相反,目標行程已有自己的主控台)。

### 10. delve 硬拒較舊 Go 工具鏈建置的二進位

症狀:F5 跳 modal 錯誤把 VS 卡住,訊息是 `Go version go1.21.5 is too old for this version of Delve (minimum supported version 1.25)`。

雙管齊下:
- dlv 啟動帶 `--check-go-version=false`(版本相符時無作用)
- 真正的解法是把工具鏈升上去:`go env -w GOTOOLCHAIN=go1.25.12+auto`(官方機制,不必重裝 Go)

### 11. GOTOOLCHAIN 切換不會觸發重建

切換工具鏈**不碰任何輸入檔**,MSBuild 判定最新 → 靜默沿用舊工具鏈建置的二進位。解法:把 `go version` 的輸出寫入 `go.build.args`,讓它參與最新性判斷。雙向切換都要驗證(切過去、切回來,各自都應重建)。

### 12. 升級工具鏈的連鎖反應

`golang.org/x/tools v0.24.0` 在 go1.25 下**無法編譯**(`tokeninternal: invalid array length` —— token 內部布局改變)。升到 v0.48.0 解決。同理 gopls 0.14.2 與 1.25 不匹配,要一起升。

### 13. 每次中斷都跳 `unknown memoryReference` 錯誤

這是本 session 最難纏的一個,**表面症狀完全誤導**。

- 表面:看起來像記憶體視窗的問題
- 實際:VS 在**每一次中斷**都會用當下的**指令指標位址**送一個 `readMemory`(`count=0`)例行探測。協定記錄的因果鏈:`stopped` → `threads` → `stackTrace` ×2 → `readMemory(PC, count=0)` → 失敗 → `ERROR: Unexpected error`
- 根因:delve 的 `readMemory` **只接受它自己發出過的參考**(`referencesCollection.get`),而 `isAddressable()` 只對 `reflect.Slice` 與 `reflect.String` 回 true。VS 自算的原始位址不在表中,必然被拒
- 無效的嘗試:engine metric `MemoryReferencesAreAddresses=0`(探測照送,實測)
- 解法:`DelveProxy`——在 host 與 dlv 之間插一層單連線 DAP relay,雙向逐位元組轉發,**只**把「`readMemory` 失敗且訊息為 unknown memoryReference」改寫成 delve 自己對合法零長度讀取所回的成功空回應。真實讀取仍原封交給 delve
- 效果:同情境 session ERROR `5 → 0`

**教訓**:使用者回報的錯誤訊息本身就是最好的診斷起點;而「我證明了 dlv 能讀 string」跟「VS 送的東西 dlv 收得到」是**兩件不同的事**,不要用前者推論後者(這個錯誤結論被使用者當場抓到)。

### 14. Set Next Statement(拖移黃箭頭)不可能

缺口 100% 在 delve:`goto`/`gotoTargets` 在任何版本都回 unsupported,delve 連 CLI 都沒有 jump。**就算 pkgdef 硬寫 `SetNextStatement=1` 也沒用**——host 會依 initialize 回應在執行期把 metric 覆寫回 0。唯一路徑是上游貢獻 delve。

### 15. Attach to Process 的 `0x8971001E`:缺的是 **ProgramProvider**

症狀:透過自訂 engine attach 一律 `HRESULT 0x8971001E`,且**失敗發生在 adapter launcher 被呼叫之前**——dlv 從未啟動、ActivityLog 無記錄、DAH 協定零流量。對照組:同一行程用 Native engine attach 成功。

**根因**:attach 流程需要 `IDebugProgramProvider2` 回答「這個行程裡有沒有屬於你這個 engine 的 program」。沒註冊 program provider,shell 找不到可附加的 program,在任何自訂程式碼執行前就放棄。

**曾經浪費時間的三個錯誤方向**(都與根因無關):
1. `"AdapterLauncher"` metric 值 → 它只是 deprecated 拼法,換成 `ExtensibilityObjects` 也一樣失敗
2. `ExtensibilityObjects` 編號子鍵 → 正確的現行拼法,但不是缺口
3. 把 `PortSupplier` 從單值改成**編號子鍵列表** → **這是走錯方向**:子鍵形狀屬於遠端/vsdbg 註冊,本機 attach 用的是**單值**

**正確做法**(全部照 JavaScript/TypeScript adapter 抄——它是唯一做本機 attach 的 in-box DAH engine):

```
"Attach"=dword:00000001
"AutoSelectPriority"=dword:00000004
"PortSupplier"="{708C1ECA-FF48-11D2-904F-00C04FA302A1}"   ; 單值,shell 預設本機 supplier
"ProgramProvider"="{你的 provider CLSID}"
"AlwaysLoadProgramProviderLocal"=dword:00000001
```
再加上 provider 的 COM CLSID 註冊(`Assembly`/`Class`/`InprocServer32`=mscoree/`CodeBase`/`ThreadingModel`=Free)。

實作只要兩個小類別:`IDebugProgramProvider2`(`GetProviderProcessData` 在 `PFLAG_GET_PROGRAM_NODES`(0x10)查詢時回傳一個 program node,`Fields` 設 `PFIELD_PROGRAM_NODES`(0x1),其餘方法回 `E_FAIL`/`S_OK`)與 `IDebugProgramNode2`(只有 `GetEngineInfo` 回 engine GUID、`GetHostPid` 回 PID 有意義,`_V7` 系列全部 `E_FAIL`)。

**找到答案的方法**:先在所有 pkgdef 中搜尋 DAH 的固定 CLSID `{DAB324E9-...}` 列出全部 DAH engine,比對誰有 `Attach=1`,發現 JS 的 `{3FBCC828}` 是唯一本機 attach 的例子,其註解直接寫著 `Debug attach and program provider`;再用 `ilspycmd -t ...AD7JSTSProgramProvider` 反編譯拿到精確契約。**「找一個做同樣事情的 in-box 元件並反編譯它」比猜 metric 有效得多。**

附帶:讓 provider 只對真正的 Go 行程回報 program node,做法是掃描目標執行檔裡 Go linker 寫入的 build-info magic(`\xff Go buildinf:`),否則 code type 清單會對每個無關行程都出現 Go 偵錯器。

---

### 15b. NuGet 判斷專案「支援與否」的實際運算式

想讓 NuGet 的 UI 對自訂專案類型消失時,不必猜。`NuGet.VisualStudio.Common.dll` 的 `VsHierarchyUtility`(反編譯可得)是唯一權威:

```csharp
IsSupported(projectKind, hierarchy):
    if (IsProjectCapabilityCompliant(hierarchy)) return true;
    if (projectKind != null && ProjectType.IsSupported(projectKind))
        return !HasUnsupportedProjectCapability(hierarchy);
    return false;

IsProjectCapabilityCompliant = IsCapabilityMatch(
    "(AssemblyReferences + DeclaredSourceItems + UserSourceItems) | PackageReferences")   // + 是 AND
HasUnsupportedProjectCapability = IsCapabilityMatch("SharedAssetsProject")
```

所以要讓 NuGet 認定「不支援」,必須同時**沒有** `PackageReferences`,而且 `AssemblyReferences`／`DeclaredSourceItems`／`UserSourceItems` 三者不齊(移除 `AssemblyReferences` 最無害——Go 專案本來就不參考 .NET 組件)。第二條分支對自訂專案類型 GUID 天然不成立。

驗證 capability 用 MSBuild 而非肉眼:
```powershell
msbuild x.goproj -getItem:ProjectCapability -restore
```

**但 capability 正確也不會讓「管理 NuGet 套件」從選單消失。** 反編譯 `NuGetPackage.BeforeQueryStatusForAddPackageDialog` 可以看到原因:

```csharp
val.Visible = GetIsSolutionOpen();                              // 只要方案開著就顯示
val2.Enabled = IsSolutionExistsAndNotDebuggingAndNotBuilding()
               && await HasActiveLoadedSupportedProjectAsync(); // capability 只影響這裡
```

`Visible` 與專案型別完全無關,所以 capability 做對的結果是「命令仍在,點下去回報 *The project ... is unsupported*」。

要真正移除,只能讓專案節點**不使用 shell 的共用選單**:NuGet 是把命令 placement 到 `IDM_VS_CTXT_PROJNODE`(其 group `IDG_VS_CTXT_PACKAGEMANAGEMENT` = 0x02F0 **不在** `SharedCmdPlace.vsct` 對 PROJNODE 的標準 placement 清單中)。因此做法是:自訂一個 context menu,用 `<CommandPlacements>` 把標準的 11 個 group(`IDG_VS_CTXT_PROJECT_BUILD`、`..._ADD`、`..._START`、`..._PROPERTIES` 等,見 `SharedCmdPlace.vsct`)重新掛上去,再用 `IProjectItemContextMenuProvider` 讓 `ProjectRoot` 節點指向它。placement 是「附加」而非「搬移」,其他專案型別不受影響。

**代價**:清單是固定的——任何第三方擴充 placement 到 PROJNODE 的命令、以及 VS 未來新增的 group,都不會出現在這個選單裡,需要手動補。

## 四、工具與環境

### 16. VSIXInstaller 退出碼 2004(BlockingProcesses)

不是只有 `devenv` 會擋。實際擋過的行程:
- `copilot-language-server.exe`(**最常見且最意外**,VS 關掉後仍殘留)
- `MSBuild.exe`、`VBCSCompiler.exe`、`PerfWatson2.exe`

診斷:`%TEMP%\dd_VSIXInstaller_*.log` 會逐行列出 `Blocking processes:`。

另外:`VSIXInstaller /quiet` 在**版本號相同**時會靜默 no-op(回報成功但沒換 DLL)——所以安裝腳本一律「先解除安裝再安裝」,並以檔案雜湊驗證部署結果。

### 17. gopls / dlv 執行檔換不掉

被執行中的 VS 鎖住。要先關 VS **並** kill 殘留的 `gopls.exe` 行程。還要注意 **PATH 順序**決定哪一份生效——本機 `D:\Go\bin` 排在 `%USERPROFILE%\go\bin` 之前,所以 `go install` 裝到後者不會生效,必須覆蓋前者。

### 18. DTE 自動化的陷阱

- COM 呼叫會**間歇性** `RPC_E_CALL_REJECTED (0x80010001)`,任何一次呼叫都要包重試迴圈(這曾造成「中斷點沒命中」的誤判——其實命中了,是輪詢讀取失敗)
- 直接開 `.goproj`(非 `.sln`)時,`Solution.Projects` 可能回報空/無名專案,那是**假的**「載入失敗」;用 `Build.BuildSolution` 加建置產物是否更新來實證
- `$args` 是 PowerShell 保留變數,拿來當參數名會炸
- UI Automation 找 VS 的子視窗常常撲空;`#32770` 對話框可以用 Win32 `EnumChildWindows` + `WM_GETTEXT` 讀出內容(這招救回過一次被 modal 卡死的 session,直接讀出了錯誤訊息)

### 19. 驗證方法論(最重要的一條)

**寫一個直接的 DAP client 去問 delve,比操作 VS UI 可靠一個數量級。** 本 session 中 VS 自動化反覆失敗(COM 拒接、視窗找不到、狀態誤判),而一支 200 行的 PowerShell DAP client 一次就給出確定答案:哪些變數有 `memoryReference`、`readMemory` 對原始位址回什麼、`disassemble` 是否真的回組語。

其次:**Debug Adapter Host 協定記錄是裁判**。
```
DebugAdapterHost.Logging /On:<檔案路徑>     (VS 命令視窗或 DTE.ExecuteCommand)
```
它會逐筆記錄 DAP 往返,能直接看出「VS 送了什麼」「delve 回了什麼」。注意這個檔案是**累加**的,分析時要先用 `DebugAdapterHost version:` 這行切出最新 session,否則會把舊 session 的警告當成新問題(這個坑踩過)。
