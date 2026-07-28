// Command orika-goc is the Go analysis sidecar for the Orika compiler
// platform. It exposes go/parser, go/ast and go/types over a small JSON
// protocol consumed by the Orika.Go.CodeAnalysis C# library.
//
// Commands:
//
//	orika-goc parse <file.go>            parse a file to a JSON AST
//	orika-goc parse -                    same, reading source from stdin
//	orika-goc parse --expr "<src>"       parse a single expression
//	orika-goc check <moduleDir>          type-check a whole module
//	orika-goc symbol <file.go> <L> <C>   symbol info at a 1-based position
//
// Every command prints a single JSON document on stdout and exits 0 even
// when the analyzed source contains errors: bad source is data (returned
// as errors/diagnostics), not a tool failure. A nonzero exit code means an
// infrastructure error (unreadable input, bad arguments, ...), reported on
// stderr.
package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"os"
)

func main() {
	os.Exit(run(os.Args[1:]))
}

// run dispatches to a subcommand. It never lets a panic escape: analyzing
// arbitrary (possibly broken) Go source must at worst produce diagnostics,
// and a genuine internal error becomes a nonzero exit, not a crash dump.
func run(args []string) (code int) {
	defer func() {
		if r := recover(); r != nil {
			fmt.Fprintf(os.Stderr, "orika-goc: internal error: %v\n", r)
			code = 1
		}
	}()
	if len(args) == 0 {
		usage(os.Stderr)
		return 2
	}
	switch args[0] {
	case "parse":
		return cmdParse(args[1:])
	case "check":
		return cmdCheck(args[1:])
	case "symbol":
		return cmdSymbol(args[1:])
	case "help", "-h", "-help", "--help":
		usage(os.Stdout)
		return 0
	default:
		fmt.Fprintf(os.Stderr, "orika-goc: unknown command %q\n\n", args[0])
		usage(os.Stderr)
		return 2
	}
}

const usageText = `orika-goc - Go analysis sidecar for Orika.Go.CodeAnalysis

usage:
  orika-goc parse <file.go> [-pretty]         print the go/parser AST as JSON
  orika-goc parse - [--path <name>]           same, reading source from stdin
  orika-goc parse --expr "<src>"              parse a single expression
  orika-goc check <moduleDir>                 type-check a module with go/types
  orika-goc symbol <file.go> <line> <col> [-dir <moduleDir>]
                                              symbol info at a 1-based position

All commands print JSON on stdout and exit 0 even when the analyzed source
contains errors; a nonzero exit reports an infrastructure failure on stderr.
`

func usage(w io.Writer) {
	fmt.Fprint(w, usageText)
}

// infra reports an infrastructure (non-source) error and yields exit code 1.
func infra(err error) int {
	fmt.Fprintln(os.Stderr, "orika-goc: "+err.Error())
	return 1
}

// emit writes v to stdout as JSON: one line by default, indented with pretty.
func emit(v any, pretty bool) int {
	enc := json.NewEncoder(os.Stdout)
	if pretty {
		enc.SetIndent("", "  ")
	}
	if err := enc.Encode(v); err != nil {
		return infra(err)
	}
	return 0
}

func newFlagSet(name string) *flag.FlagSet {
	fs := flag.NewFlagSet(name, flag.ContinueOnError)
	fs.SetOutput(os.Stderr)
	return fs
}

// parseArgs parses fs against args, allowing flags to appear both before
// and after up to npos positional arguments (so "parse file.go -pretty"
// and "parse -pretty file.go" both work). It returns the positional
// arguments and -1, or a non-negative exit code when flag parsing failed
// or help was requested.
func parseArgs(fs *flag.FlagSet, args []string, npos int) ([]string, int) {
	if err := fs.Parse(args); err != nil {
		if err == flag.ErrHelp {
			return nil, 0
		}
		return nil, 2
	}
	rest := fs.Args()
	if len(rest) <= npos {
		return rest, -1
	}
	pos := append([]string(nil), rest[:npos]...)
	if err := fs.Parse(rest[npos:]); err != nil {
		if err == flag.ErrHelp {
			return nil, 0
		}
		return nil, 2
	}
	return append(pos, fs.Args()...), -1
}
