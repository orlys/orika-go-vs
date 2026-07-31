using System.Globalization;

namespace OrikaGo.LanguageService
{
    /// <summary>
    /// User-visible strings, selected by the IDE's UI culture (devenv sets the
    /// thread UI culture to the VS display language). Two languages only:
    /// Traditional Chinese and an English fallback - a resx/satellite pipeline
    /// buys nothing at this string count.
    /// ponytail: table-of-two; switch to .resx satellites if a third language
    /// ever ships.
    /// </summary>
    internal static class GoStrings
    {
        private static bool IsChinese =>
            CultureInfo.CurrentUICulture.Name.StartsWith("zh", System.StringComparison.OrdinalIgnoreCase);

        public static string AddReferenceDialogTitle => IsChinese ? "加入 Go 模組參考" : "Add Go Module Reference";
        public static string ModulePathLabel => IsChinese ? "模組路徑（例如 github.com/google/uuid）：" : "Module path (e.g. github.com/google/uuid):";
        public static string VersionLabel => IsChinese ? "版本（例如 v1.6.0；留空表示最新版）：" : "Version (e.g. v1.6.0; empty means latest):";
        public static string OkButton => IsChinese ? "確定" : "OK";
        public static string CancelButton => IsChinese ? "取消" : "Cancel";
        public static string InvalidModulePath => IsChinese
            ? "請輸入合法的模組路徑（不可含空白或引號）。"
            : "Enter a valid module path (no whitespace or quotes).";
        public static string InvalidVersion => IsChinese
            ? "版本不可含空白或引號。"
            : "The version must not contain whitespace or quotes.";
        public static string MessageBoxTitle => "Orika Go";

        public static string ReferenceAddedPinned(string module, string version) => IsChinese
            ? $"已加入 Go 模組參考 {module}@{version}，將於下次建置時以 go get 解析。"
            : $"Added Go module reference {module}@{version}; it resolves via go get on the next build.";
        public static string ReferenceAddedLatest(string module) => IsChinese
            ? $"已加入 Go 模組參考 {module}（最新版），將於下次建置時以 go get 解析。"
            : $"Added Go module reference {module} (latest); it resolves via go get on the next build.";
        public static string AddReferenceFailed(string message) => IsChinese
            ? "無法加入 Go 模組參考：" + message
            : "Could not add the Go module reference: " + message;

        public static string GoExecutableMissing(string path) => IsChinese
            ? "找不到 Go 執行檔：" + path + "。請先建置專案。"
            : "Go executable not found: " + path + ". Build the project first.";
        public static string DlvMissing => IsChinese
            ? "找不到 dlv.exe（delve 偵錯器）。已探測 " + GoToolLocator.ProbeDescription +
              "。請安裝：go install github.com/go-delve/delve/cmd/dlv@latest"
            : "dlv.exe (the delve debugger) was not found. Probed " + GoToolLocator.ProbeDescription +
              ". Install it with: go install github.com/go-delve/delve/cmd/dlv@latest";
        public static string DlvExitedEarly(int exitCode) => IsChinese
            ? "dlv dap 啟動後立即結束（結束代碼 " + exitCode + "）。"
            : "dlv dap exited immediately after starting (exit code " + exitCode + ").";
        public static string DlvNotListening(int port) => IsChinese
            ? "dlv dap 未在時限內開始監聽 port " + port + "。"
            : "dlv dap did not start listening on port " + port + " in time.";
    }
}
