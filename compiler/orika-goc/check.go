package main

import (
	"fmt"
	"os"
)

// checkResult is the wrapper object printed by the check command.
type checkResult struct {
	Diagnostics []diagnostic `json:"diagnostics"`
}

// cmdCheck implements "orika-goc check <moduleDir>": a full go/types
// type-check of every package in the module, in process. Parse and type
// errors are data — they become diagnostics and the exit code stays 0.
func cmdCheck(args []string) int {
	fs := newFlagSet("check")
	pretty := fs.Bool("pretty", false, "indent the JSON output")
	rest, code := parseArgs(fs, args, 1)
	if code >= 0 {
		return code
	}
	if len(rest) != 1 {
		fmt.Fprintln(os.Stderr, "usage: orika-goc check <moduleDir>")
		return 2
	}
	fi, err := os.Stat(rest[0])
	if err != nil {
		return infra(err)
	}
	if !fi.IsDir() {
		return infra(fmt.Errorf("%s is not a directory", rest[0]))
	}
	l, err := newLoader(rest[0])
	if err != nil {
		return infra(err)
	}
	return emit(checkResult{Diagnostics: l.checkModule()}, *pretty)
}
