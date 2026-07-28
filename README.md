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
