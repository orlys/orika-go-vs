// Package orikagolib 提供本類別庫的公開 API。
//
// 此檔案由 Orika Go 類別庫範本產生；package 名稱會在建立專案時
// 以專案名稱的小寫形式取代。
package orikagolib

import "fmt"

// Greet 回傳給定名稱的問候字串。
//
// 這是一個匯出（exported）函式範例：以大寫字母開頭的識別項
// 會成為模組的公開 API，且應附上以識別項名稱開頭的文件註解。
func Greet(name string) string {
	return fmt.Sprintf("Hello, %s!", name)
}
