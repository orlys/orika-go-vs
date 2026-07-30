# epic — Orika.NET.Sdk 範例專案

本專案示範如何使用 **Orika.NET.Sdk**（一個自訂的 MSBuild 專案 SDK）以 .NET CLI 工具鏈來建置、執行、測試與發佈 **Go** 程式。

## 什麼是 Orika.NET.Sdk？

Orika.NET.Sdk 是一個以 NuGet 套件形式散發的 **MSBuild 專案 SDK**（`PackageType=MSBuildSdk`）。它讓 `.goproj` 專案檔可以直接寫成：

```xml
<Project Sdk="Orika.NET.Sdk/1.0.0">

  <PropertyGroup>
    <LangVersion>1.13</LangVersion>
  </PropertyGroup>

</Project>
```

SDK 內部會匯入 `Microsoft.NET.Sdk`（讓 Visual Studio 能載入專案、`dotnet` CLI 能運作），同時停用 C# 編譯器與相關輸出，改由 Go 工具鏈（`go build` / `go test` / `go vet`）完成真正的編譯工作。輸出的執行檔放在 `bin/$(Configuration)/`（例如 `bin/Debug/epic.exe`）。`TargetFramework` 只是為了滿足 `Microsoft.NET.Sdk` 而存在，對 Go 二進位毫無意義，因此 SDK 設定 `AppendTargetFrameworkToOutputPath=false` 把它從路徑中拿掉。

## 支援的屬性

| 屬性 | 說明 |
|------|------|
| `LangVersion` | 對應 Go 的語言版本。SDK 會執行 `go mod edit -go=<版本>`，由 Go 工具鏈依 `go.mod` 的 `go` 指示詞把關語言功能。例如設定 `1.13` 後，使用泛型會得到編譯錯誤「requires go1.18 or later」——這是真實的語意，不是模擬。 |
| `OutputType` | `Exe`（預設）：`go build -o` 產生執行檔；`Library`：`go build ./...` 只做編譯檢查，不產生執行檔。 |
| `DefineConstants` | 以分號分隔的清單，會轉換為 Go 的組建標籤（build tags）：`a;b` → `go build -tags "a,b"`。 |
| `GoFlags` | 原封不動附加到 `go build` 命令列的額外旗標。 |
| `GoOs` / `GoArch` | 覆寫 `GOOS` / `GOARCH` 環境變數，手動指定交叉編譯目標。 |
| `RunGoVet` | 設為 `true` 時，建置完成後自動執行 `go vet`。 |
| `GoEnsureWorkspaceMembership` | 預設為 `true`；設為 `false` 可讓此專案不要自動加入 `go.work`（詳見下方「go.work 工作區成員自動註冊」）。 |

### 模組參考（GoModuleReference）

`GoModuleReference` 是 Go 世界的 `PackageReference`——在 `.goproj` 裡宣告相依模組，建置時 SDK 以 `go get` 解析並寫入 `go.mod` / `go.sum`（含傳遞相依）：

```xml
<ItemGroup>
  <GoModuleReference Include="rsc.io/quote" Version="v1.5.2" />
  <GoModuleReference Include="golang.org/x/text" /> <!-- 省略 Version = 最新版 -->
</ItemGroup>
```

冪等規則與 `LangVersion` 相同：`go.mod` 已含該模組（且版本吻合）時完全不執行 `go get`，`go.mod` 的 mtime 不變，不會破壞增量建置。改變 `Version` 會重新解析；**移除**參考不會從 `go.mod` 移除 require——那是 `go mod tidy` 的職責。

另外，`Configuration` 也會影響編譯旗標：

- **Debug**：`-gcflags "all=-N -l"`（停用最佳化與內嵌，利於除錯）。
- **Release**：`-trimpath -ldflags "-s -w"`（去除路徑資訊與符號表，縮小執行檔）。

## 常用命令

```powershell
# 建置（實際執行 go build）
dotnet build

# 建置並執行產生的執行檔
dotnet run

# 執行 go test ./...（測試失敗會使命令失敗）
dotnet test

# 交叉編譯發佈（RID 會對應到 GOOS/GOARCH）
dotnet publish -r linux-arm64
dotnet publish -r win-x64
dotnet publish -r osx-arm64

# 清除建置輸出
dotnet clean
```

RID 對應表：`win-x64`→`windows/amd64`、`win-arm64`→`windows/arm64`、`linux-x64`→`linux/amd64`、`linux-arm64`→`linux/arm64`、`osx-x64`→`darwin/amd64`、`osx-arm64`→`darwin/arm64`。未指定 RID 時使用主機平台；只有 `GOOS=windows` 的輸出才會加上 `.exe` 副檔名。

## Go 診斷送進 Visual Studio 錯誤清單（GoExec）

`go build` / `go vet` / `go test` 回報的錯誤形如 `main.go:6:14: undefined: x`，而 MSBuild 只認得標準格式 `檔案(行,欄): error 代碼: 訊息`。SDK 先前的六個 `<Exec>` 都沒有做任何轉換，結果是**整個建置只產生一筆 `MSB3073`**，而且它指向 NuGet 快取裡的 `Sdk.targets`——在錯誤清單裡雙擊建置錯誤，會把開發者帶到 `D:\.nuget\packages\…`，而不是自己的原始碼。gopls 沒有啟動時，這是唯一還活著的錯誤回報路徑，卻完全不能用。

**`Exec` 的 `CustomErrorRegularExpression` 解不了這個問題**：它只決定哪些行要被轉送到 `Log.LogError(string)`，而那個單一參數多載不帶檔案／行／欄資訊，MSBuild 只能把錯誤歸屬到**執行工作的位置**，也就是 `Sdk.targets` 本身。要填滿標準欄位，唯一的辦法是 10 參數多載 `Log.LogError(subcategory, code, helpKeyword, file, line, col, endLine, endCol, message)`，而那需要一個真正的工作（Task）。

因此新增 `sdk/Orika.NET.Sdk/Sdk/GoDiagnostics.targets`，以 **`RoslynCodeTaskFactory` 行內工作**定義 `<GoExec>`（`dotnet build` 的 MSBuild Core 與 Visual Studio 18 的 MSBuild.exe 都支援，SDK 套件依然只含 MSBuild 邏輯，不需要編譯、簽章或封裝任何組件）。`Sdk.targets` 中 `GoBuild`（Exe／Library）、`GoVet`、`VSTest`、`Publish`（Exe／Library）共六處 `<Exec>` 全部改用 `<GoExec>`，並各自帶 `ErrorCode="GOBUILD"` / `"GOVET"` / `"GOTEST"`。

`GoExec` 逐行解析工具鏈輸出，處理下列所有情況：

| 輸出樣態 | 處理方式 |
|----------|----------|
| `.\main.go:6:14: undefined: x` | 去掉 `.\`／`./` 前置詞，以 `WorkingDirectory` 解析成絕對路徑，發出 `main.go(6,14): error GOBUILD: undefined: x` |
| `sub\a.go:11:23: …` | 子套件的相對路徑同樣正確解析 |
| `# example.com/m/sub` | 套件標頭：以訊息輸出，不是錯誤 |
| 以 **Tab** 開頭的續行（`have Do(string) error` / `want Do(int) error`） | 併入前一筆診斷（以 `; ` 相接）。MSBuild 會為多行錯誤訊息的**每一行**重複前置 `檔案(行,欄): error 代碼:`，一筆診斷會看起來像三筆 |
| `vet: ` / `vet.exe: ` 前置詞 | 先剝除再比對 |
| `    a_test.go:8: B() = 1, want 2` | `testing` 套件只保留檔名（`file[lastIndexByte(file,'/')+1:]`），直接解析會指向不存在的根目錄檔案；解析失敗時改以工作目錄下原始檔**基底檔名索引**回填，且只接受唯一命中 |
| 其他行 | `Log.LogMessage(High)`，維持 `go test` 輸出的可讀性 |

兩個關鍵設計：

1. **`t.Logf` 與 `t.Errorf` 的輸出完全相同**，不能一律當成錯誤，否則通過的測試也會讓建置失敗。`go test`（非 verbose）先印 `--- FAIL: TestX` 再印該測試的紀錄行，`go test -v` 卻是**先紀錄、後判定**。因此在判定未知時，診斷會先暫存，由隨後的 `--- FAIL`（轉為錯誤）或 `--- PASS`／`--- SKIP`（維持訊息）決定。
2. **只有在完全沒有結構化診斷時**，才補上一筆通用的「命令結束代碼非 0」錯誤。否則那筆通用訊息會像從前的 `MSB3073` 一樣蓋掉真正的 Go 診斷。

驗證（`dotnet build`、`dotnet test`、VS 18 的 `MSBuild.exe` 與 `devenv /Rebuild` 皆已實測）：在 `main.go` 第 6 行放入 `undefinedThing()`，建置輸出為

```
<專案路徑>\main.go(6,14): error GOBUILD: undefined: undefinedThing
Build FAILED.
    1 Error(s)
```

沒有 `MSB3073`，路徑是使用者原始碼的絕對路徑。

> 已知限制：失敗測試中的 `t.Logf` 紀錄行仍會一併升級為錯誤（Go 的輸出無從分辨），它們是該次失敗的上下文。`go test -parallel` 的 verbose 輸出中多個測試的紀錄行會交錯，但歸屬不再靠猜——見下方「MSBuild SDK 的正確性修正」。**錯誤清單中「雙擊即跳到該行」屬於 Visual Studio UI 行為，只能在 IDE 內目視確認**；本文所述皆為可在無介面環境重現的建置輸出。

## go.work 工作區成員自動註冊

存放庫根目錄有一份 `go.work`。**`GOWORK` 會被所有子目錄繼承**，因此只要在這棵目錄樹底下新增一個模組，它就「位於工作區之內」，但在 `go.work` 的 `use` 區塊列出它之前並不是工作區的**成員**——而 Go 工具鏈會直接拒絕建置：

```
main module (epic) does not contain package epic/MyTool
```

這正是「新增專案」流程會產生的結果：範本建立的專案能通過 `dotnet new`，卻無法建置。SDK 因此新增 `GoEnsureWorkspace` 目標（`Sdk.targets`），在 `GoEnsureMod` 之後、`GoBuild`／`VSTest`／`Publish` 之前執行：

1. 先做一次不啟動行程的預先判斷——`GOWORK` 環境變數若已設定就採用它（`GOWORK` 只能來自環境變數，`go env -w GOWORK=…` 會回答 `go: GOWORK cannot be modified`），否則由專案目錄往上尋找 `go.work`。兩者皆無代表沒有工作區，直接跳過，**完全不付出啟動行程的代價**。
2. 確實有工作區時，才以 `go env GOWORK` 取得權威值，並用 `[MSBuild]::MakeRelative` 換算出專案相對於 `go.work` 的路徑。
3. **僅在該模組確實不在 `use` 清單中時**才執行 `go work use`。這個等冪性防護與 `GoEnsureMod` 對 `go mod edit` 的做法一致：否則 `go.work` 的修改時間每次建置都會被更新，破壞增量建置。比對時會接受 `use (…)` 區塊與單行 `use <path>` 兩種寫法、可有可無的 `./` 前置詞、含空白路徑的引號形式，以及絕對路徑寫法；路徑會先經 `Regex.Escape` 處理，避免 `v1.2` 這類名稱中的 `.` 被當成萬用字元。
4. 傳給 `go work use` 的是**相對路徑**。`go work use` 會原封不動記下你給的引數，因此若傳絕對路徑，就會把「這台機器專屬」且在 Windows 上以反斜線分隔的項目寫進一份通常要簽入版控的檔案；從 `go.work` 所在目錄以相對路徑執行，寫入的才是 go 自己慣用的 `./<path>` 形式。

設計階段建置（`DesignTimeBuild=true`）不會執行此目標——Visual Studio 載入專案時不應該改寫 `go.work`。若某個模組是**刻意**排除在工作區之外的，在該 `.goproj` 中設定：

```xml
<GoEnsureWorkspaceMembership>false</GoEnsureWorkspaceMembership>
```

## 重新建置 SDK

SDK 原始檔位於 `sdk/Orika.NET.Sdk/`。修改後執行：

```powershell
.\build-sdk.ps1
```

此指令碼會：

1. 執行 `dotnet pack`，把 SDK 打包成 `.nupkg` 輸出到本機摘要來源 `./packages/`；
2. 刪除 NuGet 全域快取中的舊版本（`%USERPROFILE%\.nuget\packages\orika.net.sdk`），確保重新打包後的內容立即生效。

`nuget.config` 已設定 `./packages` 為本機來源（另含 nuget.org），且 `.goproj` 直接以 `Sdk="Orika.NET.Sdk/1.0.0"` 內嵌版本參照，不需要 `global.json`。

## 誠實的限制（非目標）

- **Visual Studio 中沒有 Go 的 IntelliSense，也不能對 Go 程式碼設中斷點偵錯。** 這需要撰寫 VSIX 語言服務，不在本 SDK 的範圍內。
- Visual Studio 能做到的是：載入 `.goproj` 專案、在方案總管顯示 `.go` 檔與 `go.mod`、執行真實的建置／清除／啟動，以及透過 VSIX 取得 gopls 提供的 IntelliSense。
- 撰寫 Go 程式碼建議搭配 VS Code + Go 延伸模組等具備語言服務的編輯器。

## 專案範本（dotnet new）

`templates/` 提供 **Orika.Go.Templates** 範本套件，含兩個範本：

| 短名稱 | 範本 | 說明 |
|--------|------|------|
| `go-console` | Orika Go 主控台應用程式 | `.goproj` + `go.mod` + `main.go`，建置後產生可執行檔 |
| `go-lib` | Orika Go 類別庫 | `OutputType=Library`，`go build ./...` 只做編譯檢查；Go package 名稱為專案名稱的小寫 |

打包與安裝（於存放庫根目錄）：

```powershell
dotnet pack templates/Orika.Go.Templates.csproj -c Release -o packages
dotnet new install Orika.Go.Templates::1.0.0
```

使用方式：

```powershell
# 建立主控台專案（--langVersion 對應 go.mod 的 go 指示詞與 <LangVersion>，預設 1.21）
dotnet new go-console -n MyTool -o MyTool --langVersion 1.22
dotnet build MyTool/MyTool.goproj
dotnet run --project MyTool/MyTool.goproj      # => Hello, World!

# 建立類別庫（package 名稱 = 專案名稱小寫，例如 MyLib => package mylib）
dotnet new go-lib -n MyLib -o MyLib
```

注意事項：

- 產生的專案以 `Sdk="Orika.NET.Sdk/1.0.0"` 參照 SDK，因此**專案所在位置必須能透過 `nuget.config` 找到 `./packages` 本機摘要來源**（在本存放庫底下建立專案即可；在其他位置請於專案旁放一份指向該摘要來源的 `nuget.config`）。
- 專案名稱小寫後若不是合法的 Go 識別項（含連字號、空白、開頭為數字），`go-lib` 產生的 package／module 名稱會無效；範本引擎不會代為淨化。
- 修改範本後重新安裝前，請先 `dotnet new uninstall Orika.Go.Templates` 或調高 `PackageVersion`。

## 編譯器平台 API（Orika.Go.CodeAnalysis）

`compiler/` 提供仿 Roslyn 形狀的 Go 編譯器平台：

- **`compiler/orika-goc/`** — Go 邊車（sidecar）CLI，本身就是一個 `.goproj`（自我實踐 Orika.NET.Sdk）。以 `go/parser`、`go/types` 實作 `parse` / `check` / `symbol` 三類命令，全部輸出 JSON；即使原始碼有錯誤也回傳結束代碼 0（僅基礎設施錯誤回傳非零）。
- **`compiler/Orika.Go.CodeAnalysis/`** — net10.0 類別庫，透過邊車提供 `GoSyntaxTree`（語法樹）、`GoCompilation`（診斷與 `Emit`，實際執行 `go build -o`）、`GoSemanticModel`（語意查詢）。
- **`compiler/Orika.Go.CodeAnalysis.Tests/`** — xUnit 測試（`dotnet test`；23 項全數通過）。

邊車的尋找順序：明確路徑引數 > `ORIKA_GOC` 環境變數 > 與 `Orika.Go.CodeAnalysis` 組件相鄰 > `PATH`。先建置邊車並設定環境變數即可：

```powershell
dotnet build compiler/orika-goc/orika-goc.goproj
$env:ORIKA_GOC = "$PWD\compiler\orika-goc\bin\Debug\orika-goc.exe"
dotnet test compiler/Orika.Go.CodeAnalysis.Tests
```

短範例：

```csharp
using Orika.Go.CodeAnalysis;

// 語法樹：解析原始碼字串（也可用 GoSyntaxTree.ParseFile(path)）
var tree = GoSyntaxTree.ParseText("package main\n\nfunc main() {\n\tprintln(1)\n}\n");
var funcDecl = tree.Root!.FirstChild("FuncDecl");
Console.WriteLine(funcDecl!.FirstChild("Ident")!.Text);        // main

// 編譯：整個 Go 模組的診斷、語意查詢與真實建置
var compilation = GoCompilation.Create(@"D:\path\to\module");
foreach (var d in compilation.GetDiagnostics())                 // GOPARSE / GOTYPE
    Console.WriteLine(d);

var model = compilation.GetSemanticModel();
var symbol = model.GetSymbolAt(@"D:\path\to\module\main.go", 4, 2);
Console.WriteLine(symbol);                                      // 例如 "var x: int"

// Emit = 真正的 go build -o（可交叉編譯）
var result = compilation.Emit(@"D:\out\tool-linux",
    new GoEmitOptions { OS = "linux", Arch = "arm64", TrimPath = true });
Console.WriteLine(result.Success);
```

診斷代碼：`GOPARSE`（語法錯誤）、`GOTYPE`（型別檢查錯誤）、`GOBUILD`（`go build` 失敗）。位置皆為 1-based 行／欄；**欄號的單位是 UTF-16 字碼單位**（詳見下方「編譯器平台的正確性修正」）。

## gopls 在 .go 檔案上的啟用（LSP 內容類型接線）

VSIX 內的 `GoLanguageClient`（`ILanguageClient`，負責啟動 `gopls serve`）先前掛在 `[ContentType("go")]` 上，但**沒有任何組件匯出名為 `go` 的內容類型**，因此 Visual Studio 永遠不會呼叫 `ActivateAsync`，gopls 也從未被啟動。整個編輯／導覽／重構／診斷功能都卡在這一個缺口上。

`vsix/OrikaGo.LanguageService/GoContentTypeDefinitions.cs` 現在真正匯出內容類型與副檔名對應：

```csharp
public const string ContentTypeName = "OrikaGo";

[Export(typeof(ContentTypeDefinition))]
[Name(ContentTypeName)]
[BaseDefinition(CodeRemoteContentDefinition.CodeRemoteContentTypeName)]  // "code-languageserver-preview"
internal static ContentTypeDefinition GoContentType;

[Export(typeof(FileExtensionToContentTypeDefinition))]
[ContentType(ContentTypeName)]
[FileExtension(".go")]
internal static FileExtensionToContentTypeDefinition GoFileExtension;
```

為什麼是這個基底？（以下皆為實際反組譯 VS 18.5 組件中繼資料所得）

- `Microsoft.VisualStudio.LanguageServer.Client.dll` 中的 `CodeRemoteContentDefinition` 宣告 `code-languageserver-preview` → `code-languageserver-base` → `languageserver-base`。而 `Microsoft.VisualStudio.LanguageServer.Client.Implementation.dll` 正是以 `languageserver-base` 作為所有啟用進入點的判斷條件，**衍生自它才會被呼叫 `ActivateAsync`**。
- `code-languageserver-preview` 同時衍生自 `code-languageserver-textmate-color`／`-structure`／`-brace`／`-indentation` 與 `code-textmate-commentselection`。`Microsoft.VisualStudio.LanguageServices.LanguageExtension.VSCore.dll` 會為這類緩衝區依文件副檔名解析 TextMate 文法，因此 VS 內建的 Go 文法（`Common7\IDE\CommonExtensions\Microsoft\TextMate\Starterkit\Extensions\go\syntaxes\go.json`，`scopeName: source.go`、`fileTypes: ["go"]`）仍會為 `.go` 上色。一次修好啟用與著色。
- 名稱刻意不叫 `go`：VS 的 TextMate 內容類型是以程式碼註冊為 `code++` 與 `code++.<文法名稱>`（純 `.go` 緩衝區的類型是 `code++.Go`），VS 18 中並不存在名為 `go` 的內容類型；改用 `OrikaGo` 也避免與未來的內建名稱衝突。

`GoLanguageClient` 本身不需修改（它讀的就是這個常數）。更新 VSIX 後需**重新啟動 Visual Studio**（MEF 快取須重建）。若 gopls 未啟動，請確認 `gopls.exe` 在 `PATH`、`GOBIN`、`GOPATH\bin`（含 `go env -w` 持久化的值）或 `%USERPROFILE%\go\bin`：`go install golang.org/x/tools/gopls@latest`。

## gopls 伺服器設定與外部檔案變更感知（InitializationOptions / FilesToWatch）

gopls 啟動後，`GoLanguageClient` 的三個屬性原本都是 `null`。以 gopls v0.14.2 的預設值來說，這代表**語意著色、七種 inlay hint、staticcheck 與所有分析器開關全部關閉**。現在改為在 `initialize` 這一次就把設定全部推給伺服器：`ConfigurationSections` 維持 `null`（本用戶端不使用 `workspace/didChangeConfiguration`），所有設定一律走 `InitializationOptions`，因此 gopls 在 `initialize` 回傳時就已完成設定，不依賴啟動後的設定往返。

### InitializationOptions：扁平結構，且鍵名必須存在

gopls 在派發設定前會先把階層名稱攤平（`internal/lsp/source/options.go`）：

```go
split := strings.Split(name, ".")
name = split[len(split)-1]
```

所以 gopls 文件裡的 `ui.*` / `build.*` 前置詞只是**文件上的分組，不是傳輸格式**；線路上送的是扁平物件。另一個關鍵是**未知鍵不會被忽略**——它會走到 `default: result.unexpected()`，產生 Error 等級的 `window/showMessage`：

```
Invalid settings: unexpected gopls setting "..."
```

也就是每次開啟方案都會跳一次錯誤通知。因此送出的每個鍵都對照 `gopls api-json` 的 `.Options.User[].Name` 驗證過（9/9 全部命中）。實際送出的設定：

| 設定 | 值 | 作用 |
|------|-----|------|
| `semanticTokens` | `true` | 不開啟時 gopls 對 `textDocument/semanticTokens/full` 直接回 `semantictokens are disabled` |
| `staticcheck` | `true` | 在預設 vet 類分析器之上加上 staticcheck 的 SA/S/ST 檢查 |
| `usePlaceholders` | `true` | 補全函式時把參數插成可跳躍的預留位置 |
| `gofumpt` | `false` | gofumpt 比 gofmt 嚴格且會改寫程式碼，維持 opt-in |
| `hints` | 七種全開 | gopls 預設是空 map，等同完全關閉 inlay hint |
| `analyses` | `unusedparams=true`、`shadow=false` | `shadow` 雜訊偏高，明確寫出預設值表示是刻意不開 |
| `directoryFilters` | 排除 `node_modules`／`bin`／`obj` | gopls 預設只排除 `node_modules` |
| `buildFlags` / `env` | 空 | 保留給 `GoFlags`／建置標籤的接點 |

### FilesToWatch：外部改動的唯一通道

`FilesToWatch` 原本為 `null`，且註解宣稱「gopls 會自己註冊監看」。實際上有一整類改動是編輯器從未經手的：SDK 的 `GoEnsureMod` 會執行 `go mod init`／`go mod edit -go=<LangVersion>`，`GoEnsureWorkspace` 會執行 `go work use`（兩者都有等冪性防護，只在檔案確實需要改變時才動手——但調整 `LangVersion` 或新增專案就會在建置過程中改寫 `go.mod`／`go.work`）；`go build` 會更新 `go.sum`；從終端機執行的 `go get`／`go mod tidy` 則會改動 `go.mod`、`go.sum` 與 `.go` 檔。這些檔案沒有被開啟過，gopls 只能靠 `workspace/didChangeWatchedFiles` 得知。現在監看：

```csharp
public IEnumerable<string> FilesToWatch => new[]
{
    "**/*.go", "**/go.mod", "**/go.sum", "**/go.work",
};
```

### RPC 追蹤（預設關閉）

`gopls serve` 的引數改由 `BuildGoplsArguments()` 決定。RPC 追蹤很吵且影響吞吐量，因此是 opt-in：啟動 Visual Studio 前把環境變數 `ORIKA_GOPLS_RPCTRACE` 設為 `1`／`true`／`yes`／`on`，才會加上 `-rpc.trace`；未設定或無法辨識的值一律關閉。

### 驗證

以一支 LSP 探針（真的啟動 `gopls serve`，走完 `initialize` → `initialized` → `didOpen`，再發出真正的請求）對同一份 Go 原始碼比較兩組設定，**設定 JSON 是用反射從編譯後的 `OrikaGo.LanguageService.dll` 取出的**，不是手抄的副本：

| 請求 | `initializationOptions = {}`（修改前） | 本次設定 |
|------|--------------------------------------|----------|
| `textDocument/semanticTokens/full` | 伺服器錯誤 `semantictokens are disabled` | 45 個語意 token |
| `textDocument/inlayHint` | 0 筆 | 5 筆（型別與參數名稱） |
| `textDocument/publishDiagnostics` | 無 | `S1002`（staticcheck）＋ `unusedparams` |
| 被拒絕的設定 | — | 0 筆（對照組：故意送出不存在的鍵確實會觸發 Error 訊息） |

外部檔案感知也實測過：在磁碟上新建一個**從未 `didOpen`** 的 `.go` 檔（內容與既有函式重複），只送出 `workspace/didChangeWatchedFiles`，gopls 隨即回報 `helper redeclared in this block`——證明這條通道確實會讓伺服器看見編輯器外的改動。

> **只能在 IDE 內目視確認的部分**：上述驗證證明的是「gopls 收到這些設定後行為確實改變」，以及「gopls 會對 `didChangeWatchedFiles` 作出反應」。至於 **Visual Studio 是否真的依 `FilesToWatch` 的 glob 送出該通知**，以及語意色彩與 inlay hint 在 `.go` 編輯器中的實際呈現，屬於 VS UI 行為，無法在無介面環境下證明。

## 專案結構

```
epic/
├── epic.slnx                    # 方案檔（XML 新格式；Type="C#" 讓 VS 以 SDK 專案系統載入 .goproj）
├── epic.goproj                  # 使用 Orika.NET.Sdk 的專案檔
├── go.mod                      # Go 模組定義（go 指示詞由 SDK 依 LangVersion 管理）
├── main.go                     # 進入點
├── greeting.go                 # Greet 輔助函式
├── greeting_test.go            # go test 會執行的測試
├── Properties/launchSettings.json
├── sdk/Orika.NET.Sdk/          # SDK 本體（Sdk.props / Sdk.targets / 打包專案）
├── templates/                  # Orika.Go.Templates 範本套件（go-console / go-lib）
├── compiler/                   # 編譯器平台：orika-goc 邊車、Orika.Go.CodeAnalysis(+.Tests)
├── vsix/OrikaGo.LanguageService/ # VS 語言服務 VSIX（gopls LSP + goproj.pkgdef：獨立專案類型 GUID {8A0FBF95-...}，仿 msbuildproj 模式指向 CPS 套件）
├── packages/                   # 本機 NuGet 摘要來源（build-sdk.ps1 的輸出）
├── nuget.config
└── build-sdk.ps1
```

## 圖示

- **「新增專案」對話方塊**：`go-console` 與 `go-lib` 兩個範本各自帶有 `.template.config/icon.png`（32x32），並在 `.template.config/ide.host.json` 以 `"icon": "icon.png"` 宣告（相對路徑以 `.template.config` 為基準解析）。重新打包並安裝 `Orika.Go.Templates` 後，VS 的「新增專案」對話方塊即會顯示範本圖示。
- **方案總管**：VSIX 內的 `OrikaGoImages.imagemanifest` 向 VS 影像服務註冊 `GoProjectNode` 與 `GoFileNode` 兩組圖示（PNG 以 WPF 元件資源形式內嵌於 `OrikaGo.LanguageService.dll`），再由 `GoProjectTreeIconProvider`（`IProjectTreePropertiesProvider`，`[Order(1000)]`）套用到專案根節點與 `.go` 檔案節點。
- **OrikaGo 專案能力（ProjectCapability）**：`Orika.NET.Sdk` 的 `Sdk.props` 對每個 `.goproj` 專案宣告 `<ProjectCapability Include="OrikaGo" />`，VSIX 的 MEF 匯出即以 `[AppliesTo("OrikaGo")]` 只作用於 Go 專案。
- **注意**：更新範本或 VSIX 後需**重新啟動 Visual Studio**（範本快取與 MEF／影像庫快取須重建）才會看到新圖示。

## 編譯器平台的正確性修正

外部審查（codex）在 `compiler/` 找出三個真實缺陷，皆已修正並補上測試（測試總數 11 → 23）：

| 缺陷 | 症狀 | 修正 |
|------|------|------|
| 型別檢查不認識 Go 模組 | 邊車以 `go/importer` 的 source importer 檢查，不理解 `go.mod`／模組快取／`go.work`。匯入外部相依（例如 `github.com/google/uuid`）的模組 `go build` 成功，`GetDiagnostics()` 卻回報假的 `could not import ...`。 | 改用 `golang.org/x/tools/go/packages`（`packages.Load`，內部驅動真正的 `go list`），模組相依、工作區與 `vendor` 的解析方式與 `go build` 完全一致。JSON 協定形狀不變，「原始碼錯誤是資料（結束代碼 0）、僅基礎設施失敗回傳非零」的規則也不變。 |
| 檢查與建置看的檔案集合可能不同 | `Emit` 接受 `Tags`／`OS`／`Arch`，但 `GetDiagnostics()` 沒有對應選項，邊車固定以 `build.Default` 檢查。於是 `//go:build linux` 的程式碼在 Windows 上完全檢查不到，卻會被 `Emit(OS = "linux")` 編譯。 | 邊車的 `check`／`symbol` 新增 `-tags`／`-goos`／`-goarch`（透過 `packages.Config.Env` 設定 `GOOS`／`GOARCH`／`GOFLAGS=-tags=…`）。C# 端新增 `GoAnalysisOptions`，並讓 `GoEmitOptions` 由它衍生，因此**同一個選項物件**可同時交給 `GetDiagnostics(options)`、`GetSemanticModel(options)` 與 `Emit(path, options)`。無參數多載維持原樣。 |
| 欄號單位與編輯器不一致 | Go 的 `token.Position.Column` 是**行內位元組數**，而 .NET／Visual Studio／LSP 使用 **UTF-16 字碼單位**。含非 ASCII 字元的行（例如 `fmt.Println("你好世界", value)`）中，`GetSymbolAt` 以 VS 回報的欄號查詢會得到 `null`。 | 在邊車的協定邊界做轉換：輸出位置時位元組欄 → UTF-16 欄，`symbol` 接受位置時 UTF-16 欄 → 位元組欄（實際讀取該行的原始位元組換算）。`parse`、`check`、`symbol` 三個命令一致。`offset` 仍維持為位元組位移。C# 端的 XML 文件已明確標示單位。 |

驗證方式（`compiler/Orika.Go.CodeAnalysis.Tests/`）：`GoModuleResolutionTests` 以真實的 `go mod tidy`／`go build` 當作基準，要求 `GetDiagnostics()` 與 `go build` 的判斷一致；`GoBuildContextTests` 檢查 `//go:build linux` 與自訂標籤的檔案「預設看不到、指定建置內容才看得到」；`GoColumnUnitTests` 以含 `你好世界`／`名前` 的原始碼確認欄號為 UTF-16 單位（並確認舊的位元組欄號不再解析成功）。上述 11 項新測試在修正前的邊車上全數失敗。

> 邊車現在依賴 `golang.org/x/tools`（見 `compiler/orika-goc/go.mod`／`go.sum`）。由於 `check` 會透過 `go list` 從原始碼型別檢查相依套件，單次檢查約需數秒。

## MSBuild SDK 的正確性修正

外部審查（codex）在 `sdk/Orika.NET.Sdk/Sdk/` 找出三個真實缺陷，皆已修正：

| 缺陷 | 症狀 | 修正 |
|------|------|------|
| `go work use` 沒有跨行程鎖（`Sdk.targets`） | 一份 `go.work` 由多個 `.goproj` 共用，`/m` 平行建置下每個專案各自在獨立行程中判斷「不是成員」並同時改寫 `go.work`；`go work use` 是整檔的讀-改-寫，後寫者會**默默覆蓋**先寫者，把別的模組從 `use` 清單中抹掉（所有行程結束代碼仍為 0）。 | 新增行內工作 `<GoWorkUse>`（與 `GoExec` 同樣採 `RoslynCodeTaskFactory`）：以 `go.work` 完整路徑的雜湊命名具名系統 Mutex（`Global\OrikaGo.GoWork.<hash>`，不同工作區互不排隊；無 `SeCreateGlobalPrivilege` 時降級為 `Local\`），**在鎖內重新檢查成員資格**後才執行 `go work use`，並於 `finally` 釋放；`AbandonedMutexException` 視為取得所有權（前一持有者中途死亡，鎖內重讀即可自我修復）。等冪性、「沒有 `go.work` 不動作」、「`GOWORK=off` 不動作」、「不改寫使用者手寫的既有項目」全部維持不變。 |
| cgo 輸入檔的萬用字元不完整（`Sdk.props`） | `GoNativeCompile` 只涵蓋 `.c`／`.h`／`.s`／`.S`／`.syso`。只改了 `helper.cpp` 時 MSBuild 判定為最新，**`GoBuild` 整個被略過、`go build` 根本沒執行**，留下過期的二進位檔。 | 補齊 `go/build` 實際接受的完整集合：`.c .cc .cpp .cxx .m .mm .h .hh .hpp .hxx .s .S .sx .f .F .for .f90 .swig .swigcxx .syso`。同一份專案的 `@(GoNativeCompile)` 由 4 個項目變成 18 個；只碰 `helper.cpp` 後 `GoBuild` 由「Skipping target … up-to-date」變成確實重新執行並改寫執行檔。 |
| 測試輸出的判定歸屬錯誤（`GoDiagnostics.targets`） | 待判定的診斷行放在**單一共用緩衝區**，被「下一個抵達的判定」整批解決。`go test -v -parallel=2` 下，若某個平行測試先記錄並 `--- PASS`，失敗測試那筆可導覽的 `file.go:N:` 位置就會被當成一般訊息丟掉，錯誤清單只剩通用的「命令結束代碼非 0」；反之，`--- FAIL` 先抵達時，通過的子測試紀錄行會被誤升為錯誤。 | 改為**依測試名稱歸屬**：追蹤 `=== RUN`／`PAUSE`／`CONT`／`NAME` 所指的擁有者，把它戳記在每筆暫存診斷上，`--- FAIL`／`--- PASS`／`--- SKIP` 只解決**它所指名的那個測試**（含 `TestX/sub` 子測試）的項目；串流結束時仍無判定者維持輸出為訊息。非 verbose 的循序情境（`--- FAIL` 先印）與 `go test` 去掉目錄後的基底檔名回填索引都維持原行為。 |

驗證：`go.work` 競態以存放庫外的測試載具重現——6 個並行 `dotnet build -t:GoEnsureWorkspace` 行程共用一份 `go.work`，修正前（git HEAD 的目標）30 回合中 10 回合有 3 回合掉失模組，修正後 30 回合全數保住 6 個模組與使用者手寫項目，且 `go work edit -json` 皆可解析；真實方案以 `dotnet build epic.slnx -t:Rebuild -m` 從空白 `go.work` 重建，結果與簽入版本逐位元組相同。cgo 萬用字元以 `@(GoNativeCompile)` 傾印與「只碰 `.cpp` 後是否重新建置」比對（**本機沒有安裝 C/C++ 工具鏈，`CGO_ENABLED=1 go build` 會停在 `cgo: C compiler "gcc" not found`，因此驗證的是 MSBuild 的最新性判斷這一段機制，而非實際的 cgo 編譯**）。測試判定歸屬以兩個 `t.Parallel()` 測試（一個先記錄並通過、另一個稍後失敗）驗證，修正後輸出 `…\inner\race_test.go(11): error GOTEST: deliberate failure from the parallel test`，修正前同一情境只得到通用的 `exited with code 1`。

## 第三輪對抗性審查的修正

第三輪審查（內部多 agent 對抗性 workflow ＋ 外部 codex 並行、交叉比對）確認 15 個新缺陷，皆已修正並驗證。同輪新增功能：`GoModuleReference`（見「支援的屬性」）。

**MSBuild SDK（`sdk/Orika.NET.Sdk/Sdk/`）**：

| 缺陷 | 症狀 | 修正 |
|------|------|------|
| workspace 跨模組的過期二進位 | `_GoBuildInput` 只收專案自身檔案；`go.work` 下修改 sibling 模組後,importer 專案被判定最新而跳過 `GoBuild`,`bin` 裡是舊行為的執行檔（結束代碼 0）。 | 偵測到 `go.work`（`GOWORK` 或向上探測）時,在輸入清單放一個永不存在的檔案,使 `GoBuild` 永遠執行——增量交還給 go 自身的建置快取（無變更時只付一次行程啟動）。非 workspace 專案的 MSBuild 增量行為不變。 |
| `dotnet test`／`go vet` 缺 build context | `VSTest` 與 `GoVet` 不帶 `$(_GoBuildArgs)`,`DefineConstants` 的 `-tags` 與 `GoFlags` 只影響 build,tag-gated 程式碼測試時 `undefined`、vet 檢查錯誤檔案集合。 | 兩者皆帶上 `$(_GoBuildArgs)`（`go test`／`go vet` 都接受建置旗標）,與 `GoBuild` 看同一組檔案。 |
| `GoWorkUse` 硬編 `GOTOOLCHAIN=local` | `go work use` 會**載入**被加入模組的 `go.mod`,langVersion 比本機工具鏈新的專案永遠無法加入 workspace（範本明明提供這些選項）。 | 移除硬編值,交給預設的 `auto`（實測:go 1.21.5 下把 `go 1.22` 模組加入 workspace 時自動切換 go1.25.12 並成功）。原註解的理由對 `go work edit` 成立、對 `go work use` 不成立。 |
| `go mod init` 名稱未淨化＋錯誤歸屬 | 專案名含空白/非 ASCII（VS 完全合法）時 `go mod init "My App"` 以 `malformed module path` 失敗,且以 plain `Exec` 執行,錯誤指向 NuGet 快取裡的 `Sdk.targets`。 | `GoModuleName` 預設值套用與範本 `goModuleSafe` 相同的淨化（`My App` → `My_App`,實測建置成功）;`GoEnsureMod` 改用 `GoExec`（`GOMOD` 錯誤碼）。 |
| RID 驗證擋死 explicit override | `_GoValidatePublishRid` 只認內建六個 RID,`-r freebsd-x64 -p:GoOS=freebsd -p:GoArch=amd64` 這種明確指定也被拒。 | 只在**有效值**（RID 對應與 explicit `GoOS`/`GoArch` 合併後）仍為空時才報錯,實測 freebsd-x64 publish 成功。 |
| 診斷 parser 不認 cgo 副檔名 | `GoExec` 與 `GoCompilation` 的正規表示式只認 `.go/.s/.S/.c/.h`,`helper.cpp:3:5: error:` 這類 C/C++ 編譯錯誤無法從錯誤清單導覽。 | 兩處副檔名集合對齊 `@(GoNativeCompile)` 的完整清單。 |

**編譯器平台（`compiler/`）**：

| 缺陷 | 症狀 | 修正 |
|------|------|------|
| `Emit()` 欄號是位元組 | `ParseGoBuildOutput` 把 `go build` stderr 的位元組欄原樣塞進 `GoLocation`,違反「所有欄號一律 UTF-16」的文件契約,與 `GetDiagnostics()` 對同一錯誤回報不同欄號。 | C# 端實作與邊車 `toUTF16Col` 同規則的轉換（含 BOM 規則）,新增測試:`你好世界` 行的位元組欄 30 → UTF-16 欄 22。 |
| 相對路徑用 cwd 解析 | `go list` 的 ListError 位置相對於**模組目錄**,`addDiag` 卻用 `filepath.Abs`（行程 cwd,VS spawn 時不定）,診斷指向不存在的檔案且與 parse 路徑不一致造成重複。 | 相對路徑一律 join 到 loader 的模組目錄（`resolvePath`）。 |
| 缺 require 的匯入訊息無法行動 | 可行動的 `no required module provides package X; to add it: go get X` 附在**依賴 stub 套件**上,只走訪 top-level 的迴圈把它丟掉,只剩 `could not import X (invalid package name: "")`。 | 改用 `packages.Visit` 走訪整個圖;依賴套件只取**位置落在本模組內**的錯誤,避免第三方套件內部錯誤灌爆錯誤清單。 |
| 壞 `go.mod` 變 infra error | `go.mod` 打錯字（每次手邊編輯都會經過的狀態）使 `packages.Load` 硬錯,邊車 exit 1,C# 端 `GetDiagnostics()` 直接擲出例外——違反「壞原始碼是資料,exit 0」的契約。 | 工具鏈**有跑起來**的載入失敗轉為指向 `go.mod` 對應行的診斷（訊息內含 `go.mod:5:` 時取其行號）,exit 0;僅「go 指令不存在／目錄不存在」維持 infra error。 |
| BOM 檔第一行欄號右偏 1 | UTF-8 BOM 的 3 位元組計入 go/token 位元組欄,但 VS 緩衝區會剝掉 BOM;`utf16Len` 把 U+FEFF 算 1 單位,第一行所有欄號偏 1（`toByteCol` 為鏡像錯誤）。 | 行首 U+FEFF 計為 0 個 UTF-16 單位;`toByteCol` 先跳過 BOM 的 3 位元組再計數（editor 欄 1 ↔ 位元組欄 4）。實測 BOM 檔 `parse`:File 欄 1、`main` 識別項欄 9,與 VS 緩衝區一致。 |

**VSIX（`vsix/OrikaGo.LanguageService/`）**：

| 缺陷 | 症狀 | 修正 |
|------|------|------|
| solution 模式收不到 watched-file 事件 | `FilesToWatch` 只有 Open Folder host 消費;.sln/.slnx（主要模式）下終端機 `go get`、SDK 目標改寫 go.mod 後 gopls 持續用過期模組圖,假紅蚯蚓直到重啟 VS。VS 又硬編 dynamicRegistration=false,gopls 無法自行註冊 watcher。 | 實作 `ILanguageClientCustomMessage2.AttachForCustomMessageAsync` 取得 JsonRpc,`initialized` 後在 workspace root 掛 client 端 `FileSystemWatcher`（`*.go`/`go.mod`/`go.sum`/`go.work`,排除 bin/obj/node_modules 與 `FilesToWatch` 一致）,直接以 rpc 轉發 `workspace/didChangeWatchedFiles`（1=Created、2=Changed、3=Deleted,rename 拆成 3+1）。watcher 與 rpc 隨 server 生命週期釋放。 |
| `FindGopls` 忽略 GOBIN/GOPATH | 只查 PATH 與 `%USERPROFILE%\go\bin`;`go env -w GOBIN=…` 的使用者照錯誤訊息 `go install` 後仍「找不到」,永遠循環。 | 探測順序改為 PATH → `GOBIN`/`GOPATH\bin`（環境變數＋`go env` 讀出 `go env -w` 持久化值）→ `%USERPROFILE%\go\bin`,錯誤訊息同步更新。 |

**腳本**：`install-vsix.ps1 -NoBuild` 不再要求 extension development workload——它只需要每個 VS 版本都有的 `VSIXInstaller.exe`;workload 檢查僅在需要 MSBuild 建置時執行。

驗證：compiler 測試 26/26（含新增的 Emit UTF-16 欄號測試）;workspace stale binary、`-tags` 進 test/vet、freebsd publish override、`My App` 淨化、GoWorkUse 工具鏈切換、orika-goc 四個場景（相對路徑、缺 require、壞 go.mod、BOM）皆以實際重現腳本在修正前後比對確認。
