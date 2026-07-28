package main

import (
	"flag"
	"fmt"
	"go/token"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"strconv"
	"strings"

	"golang.org/x/tools/go/packages"
)

// diagnostic is one entry of the "check" command output.
type diagnostic struct {
	File     string `json:"file"`
	Line     int    `json:"line"`
	Col      int    `json:"col"`
	Severity string `json:"severity"`
	Msg      string `json:"msg"`
}

// loadMode is the go/packages mode used by every command that needs
// types. It is deliberately the full source-based mode: NeedDeps plus
// NeedSyntax makes the loader type-check dependencies from source, which
// is what allows checking for a GOOS/GOARCH that has no prebuilt export
// data on this machine.
const loadMode = packages.NeedTypes |
	packages.NeedSyntax |
	packages.NeedTypesInfo |
	packages.NeedName |
	packages.NeedFiles |
	packages.NeedCompiledGoFiles |
	packages.NeedDeps |
	packages.NeedImports

// buildOptions carries the build-context selectors that decide *which*
// files are part of a package: build tags and the target platform. They
// mirror the flags `go build` accepts so that what is type-checked is the
// same file set that will later be emitted.
type buildOptions struct {
	Tags   string // comma-separated, as passed to -tags
	GOOS   string
	GOARCH string
}

// registerBuildFlags wires -tags/-goos/-goarch onto fs.
func (o *buildOptions) registerBuildFlags(fs *flag.FlagSet) {
	fs.StringVar(&o.Tags, "tags", "", "comma-separated build `tags` (as in go build -tags)")
	fs.StringVar(&o.GOOS, "goos", "", "target `operating system` (GOOS); empty means the host default")
	fs.StringVar(&o.GOARCH, "goarch", "", "target `architecture` (GOARCH); empty means the host default")
}

// env builds the environment for the `go list` subprocess go/packages
// runs: the current environment plus the requested overrides. Later
// entries win, so the overrides shadow any inherited value.
func (o buildOptions) env() []string {
	env := os.Environ()
	if o.GOOS != "" {
		env = append(env, "GOOS="+o.GOOS)
	}
	if o.GOARCH != "" {
		env = append(env, "GOARCH="+o.GOARCH)
	}
	if o.Tags != "" {
		env = append(env, "GOFLAGS=-tags="+o.Tags)
	}
	return env
}

// config returns the go/packages configuration for a load rooted at dir.
func (o buildOptions) config(dir string, fset *token.FileSet, tests bool) *packages.Config {
	return &packages.Config{
		Mode:  loadMode,
		Dir:   dir,
		Env:   o.env(),
		Fset:  fset,
		Tests: tests,
	}
}

// loader type-checks Go packages through golang.org/x/tools/go/packages,
// which drives the real `go list`. Going through the go command is what
// makes module-aware resolution work: external dependencies declared in
// go.mod, the module cache, workspaces (go.work), vendor directories and
// build tags are all handled by the toolchain itself rather than being
// re-implemented here.
type loader struct {
	dir   string // absolute directory the load is rooted at
	opts  buildOptions
	fset  *token.FileSet
	lines *lineIndex

	diags    []diagnostic
	diagSeen map[string]bool
}

func newLoader(dir string, opts buildOptions) (*loader, error) {
	abs, err := filepath.Abs(dir)
	if err != nil {
		return nil, fmt.Errorf("resolving %s: %w", dir, err)
	}
	return &loader{
		dir:      abs,
		opts:     opts,
		fset:     token.NewFileSet(),
		lines:    newLineIndex(),
		diagSeen: map[string]bool{},
	}, nil
}

// findModuleRoot walks up from dir looking for a go.mod; "" if none found.
func findModuleRoot(dir string) string {
	dir = filepath.Clean(dir)
	for {
		if fi, err := os.Stat(filepath.Join(dir, "go.mod")); err == nil && !fi.IsDir() {
			return dir
		}
		parent := filepath.Dir(dir)
		if parent == dir {
			return ""
		}
		dir = parent
	}
}

// samePath compares two file paths, case-insensitively on Windows.
func samePath(a, b string) bool {
	a, b = filepath.Clean(a), filepath.Clean(b)
	if runtime.GOOS == "windows" {
		return strings.EqualFold(a, b)
	}
	return a == b
}

// addDiag records a diagnostic. line/col arrive as go/token reports them
// (1-based, column counted in bytes) and are stored in the protocol's
// unit: 1-based UTF-16 code units.
func (l *loader) addDiag(file string, line, byteCol int, msg string) {
	if file != "" {
		if abs, err := filepath.Abs(file); err == nil {
			file = abs
		}
	}
	col := byteCol
	if col > 0 {
		col = l.lines.toUTF16Col(file, line, byteCol)
	}
	key := fmt.Sprintf("%s\x00%d\x00%d\x00%s", file, line, col, msg)
	if l.diagSeen[key] {
		return
	}
	l.diagSeen[key] = true
	l.diags = append(l.diags, diagnostic{File: file, Line: line, Col: col, Severity: "error", Msg: msg})
}

// parsePackagesError turns a packages.Error into a diagnostic. Its Pos is
// a formatted token.Position ("file:line:col", "file:line", or empty);
// on Windows the file part itself contains a colon, so the position is
// peeled off from the right.
func parsePackagesPos(pos string) (file string, line, col int) {
	pos = strings.TrimSpace(pos)
	if pos == "" || pos == "-" {
		return "", 0, 0
	}
	file = pos
	i := strings.LastIndex(file, ":")
	if i < 0 {
		return file, 0, 0
	}
	n, err := strconv.Atoi(file[i+1:])
	if err != nil {
		return file, 0, 0
	}
	rest := file[:i]
	j := strings.LastIndex(rest, ":")
	if j >= 0 {
		if m, err2 := strconv.Atoi(rest[j+1:]); err2 == nil {
			return rest[:j], m, n
		}
	}
	// Only one numeric component: it was the line, no column.
	return rest, n, 0
}

// checkModule type-checks every package of the module rooted at the
// loader's directory and returns the accumulated diagnostics, sorted by
// position. A returned error is an infrastructure failure (the go
// toolchain could not be run at all); source problems are diagnostics.
func (l *loader) checkModule() ([]diagnostic, error) {
	cfg := l.opts.config(l.dir, l.fset, false)
	pkgs, err := packages.Load(cfg, "./...")
	if err != nil {
		return nil, fmt.Errorf("loading packages in %s: %w", l.dir, err)
	}

	// Only packages belonging to the loaded patterns are reported;
	// problems inside dependencies surface on the importing package as an
	// import error, which is what a user of this module can act on.
	for _, pkg := range pkgs {
		for _, e := range pkg.Errors {
			file, line, col := parsePackagesPos(e.Pos)
			l.addDiag(file, line, col, e.Msg)
		}
	}

	sort.SliceStable(l.diags, func(i, j int) bool {
		a, b := l.diags[i], l.diags[j]
		if a.File != b.File {
			return a.File < b.File
		}
		if a.Line != b.Line {
			return a.Line < b.Line
		}
		if a.Col != b.Col {
			return a.Col < b.Col
		}
		return a.Msg < b.Msg
	})
	if l.diags == nil {
		l.diags = []diagnostic{}
	}
	return l.diags, nil
}
