package main

import (
	"fmt"
	"go/ast"
	"go/build"
	"go/importer"
	"go/parser"
	"go/scanner"
	"go/token"
	"go/types"
	"io/fs"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
)

// diagnostic is one entry of the "check" command output.
type diagnostic struct {
	File     string `json:"file"`
	Line     int    `json:"line"`
	Col      int    `json:"col"`
	Severity string `json:"severity"`
	Msg      string `json:"msg"`
}

// loader parses and type-checks the packages of a single Go module using
// only the standard library. It doubles as the types.Importer used during
// checking: imports that resolve inside the module are type-checked from
// the module sources; everything else — including the standard library —
// goes through the stdlib "source" importer sharing the same FileSet.
type loader struct {
	fset     *token.FileSet
	dir      string // absolute module root
	modPath  string // module path from go.mod, "" if unknown
	ctx      build.Context
	fallback types.Importer

	parsed   map[string]*ast.File              // file path -> AST (nil if unusable)
	pkgCache map[string]map[string][]*ast.File // dir+tests -> package name -> files
	checked  map[string]*checkEntry            // dir+package name -> result
	fileName map[*ast.File]string              // AST -> path it was parsed from
	diags    []diagnostic
	diagSeen map[string]bool
}

type checkEntry struct {
	pkg  *types.Package
	err  error
	done bool // false while the package is being checked (cycle guard)
}

func newLoader(moduleDir string) (*loader, error) {
	abs, err := filepath.Abs(moduleDir)
	if err != nil {
		return nil, fmt.Errorf("resolving %s: %w", moduleDir, err)
	}
	fset := token.NewFileSet()
	return &loader{
		fset:     fset,
		dir:      abs,
		modPath:  readModulePath(filepath.Join(abs, "go.mod")),
		ctx:      build.Default,
		fallback: importer.ForCompiler(fset, "source", nil),
		parsed:   map[string]*ast.File{},
		pkgCache: map[string]map[string][]*ast.File{},
		checked:  map[string]*checkEntry{},
		fileName: map[*ast.File]string{},
		diagSeen: map[string]bool{},
	}, nil
}

// readModulePath extracts the module path from a go.mod file; it returns
// "" when the file is missing or has no module directive.
func readModulePath(gomod string) string {
	data, err := os.ReadFile(gomod)
	if err != nil {
		return ""
	}
	for _, line := range strings.Split(string(data), "\n") {
		fields := strings.Fields(strings.TrimSpace(line))
		if len(fields) >= 2 && fields[0] == "module" {
			return strings.Trim(fields[1], `"`)
		}
	}
	return ""
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

func (l *loader) addDiag(file string, line, col int, msg string) {
	if file != "" {
		if abs, err := filepath.Abs(file); err == nil {
			file = abs
		}
	}
	key := fmt.Sprintf("%s\x00%d\x00%d\x00%s", file, line, col, msg)
	if l.diagSeen[key] {
		return
	}
	l.diagSeen[key] = true
	l.diags = append(l.diags, diagnostic{File: file, Line: line, Col: col, Severity: "error", Msg: msg})
}

// collectTypeError is the types.Config.Error callback: it records every
// type error (hard and soft) as a diagnostic and lets checking continue.
func (l *loader) collectTypeError(err error) {
	if te, ok := err.(types.Error); ok {
		p := te.Fset.Position(te.Pos)
		l.addDiag(p.Filename, p.Line, p.Column, te.Msg)
		return
	}
	l.addDiag("", 0, 0, err.Error())
}

// parseFile parses path once and caches the result. Syntax errors are
// recorded as diagnostics; a partial AST is still returned when available.
func (l *loader) parseFile(path string) *ast.File {
	if f, ok := l.parsed[path]; ok {
		return f
	}
	f, err := parser.ParseFile(l.fset, path, nil, parser.ParseComments|parser.AllErrors|parser.SkipObjectResolution)
	if err != nil {
		if list, ok := err.(scanner.ErrorList); ok {
			for _, e := range list {
				l.addDiag(e.Pos.Filename, e.Pos.Line, e.Pos.Column, e.Msg)
			}
		} else {
			l.addDiag(path, 0, 0, err.Error())
		}
	}
	if f != nil {
		l.fileName[f] = path
	}
	l.parsed[path] = f
	return f
}

// packagesIn parses every buildable .go file of dir and groups the files
// by package clause. Test files are considered only when includeTests is
// set; files excluded by build constraints for the current platform are
// skipped so that GOOS/GOARCH variants do not clash.
func (l *loader) packagesIn(dir string, includeTests bool) map[string][]*ast.File {
	key := fmt.Sprintf("%s\x00%v", dir, includeTests)
	if g, ok := l.pkgCache[key]; ok {
		return g
	}
	groups := map[string][]*ast.File{}
	l.pkgCache[key] = groups
	entries, err := os.ReadDir(dir)
	if err != nil {
		l.addDiag(dir, 0, 0, "reading directory: "+err.Error())
		return groups
	}
	var names []string
	for _, e := range entries {
		name := e.Name()
		if e.IsDir() || !strings.HasSuffix(name, ".go") ||
			strings.HasPrefix(name, ".") || strings.HasPrefix(name, "_") {
			continue
		}
		if !includeTests && strings.HasSuffix(name, "_test.go") {
			continue
		}
		names = append(names, name)
	}
	sort.Strings(names)
	for _, name := range names {
		if match, merr := l.ctx.MatchFile(dir, name); merr != nil || !match {
			continue // excluded by build constraints, or header unreadable
		}
		f := l.parseFile(filepath.Join(dir, name))
		if f == nil || f.Name == nil {
			continue
		}
		groups[f.Name.Name] = append(groups[f.Name.Name], f)
	}
	return groups
}

// checkGroup type-checks one package: the files sharing a package clause
// in a single directory. Results are cached, and packages currently being
// checked are reported as import cycles instead of recursing forever.
func (l *loader) checkGroup(dir, path, name string, files []*ast.File, info *types.Info) (*types.Package, error) {
	key := dir + "\x00" + name
	if e, ok := l.checked[key]; ok {
		if !e.done {
			return nil, fmt.Errorf("import cycle through %q", path)
		}
		return e.pkg, e.err
	}
	e := &checkEntry{}
	l.checked[key] = e
	conf := types.Config{
		Importer:    l,
		Error:       l.collectTypeError,
		FakeImportC: true,
	}
	pkg, err := conf.Check(path, l.fset, files, info)
	e.pkg, e.err, e.done = pkg, err, true
	return pkg, err
}

// Import implements types.Importer. Packages inside the module are
// type-checked from source through the loader itself; everything else is
// delegated to the standard library "source" importer.
func (l *loader) Import(path string) (*types.Package, error) {
	if path == "unsafe" {
		return types.Unsafe, nil
	}
	if dir, ok := l.localDir(path); ok {
		groups := l.packagesIn(dir, false)
		var names []string
		for name := range groups {
			if name != "main" && !strings.HasSuffix(name, "_test") {
				names = append(names, name)
			}
		}
		if len(names) == 0 {
			return nil, fmt.Errorf("no importable Go package for %q in %s", path, dir)
		}
		sort.Strings(names)
		pkg, err := l.checkGroup(dir, path, names[0], groups[names[0]], nil)
		if pkg != nil {
			// A package with type errors is still usable by importers;
			// the errors have already been collected as diagnostics.
			return pkg, nil
		}
		return nil, err
	}
	return l.fallback.Import(path)
}

// localDir maps an import path inside the module to its directory, or
// reports false for stdlib/external imports.
func (l *loader) localDir(path string) (string, bool) {
	if l.modPath == "" {
		return "", false
	}
	var rel string
	switch {
	case path == l.modPath:
		rel = "."
	case strings.HasPrefix(path, l.modPath+"/"):
		rel = path[len(l.modPath)+1:]
	default:
		return "", false
	}
	dir := filepath.Join(l.dir, filepath.FromSlash(rel))
	if fi, err := os.Stat(dir); err != nil || !fi.IsDir() {
		return "", false
	}
	return dir, true
}

// importPathFor derives the import path of a directory inside the module.
func (l *loader) importPathFor(dir string) string {
	rel, err := filepath.Rel(l.dir, dir)
	if err != nil || rel == ".." || strings.HasPrefix(rel, ".."+string(filepath.Separator)) {
		return filepath.ToSlash(dir)
	}
	rel = filepath.ToSlash(rel)
	switch {
	case l.modPath != "" && rel == ".":
		return l.modPath
	case l.modPath != "":
		return l.modPath + "/" + rel
	case rel == ".":
		return filepath.Base(dir)
	default:
		return rel
	}
}

// goDirs returns every directory under the module root containing at least
// one non-test .go file, skipping vendor trees, testdata, hidden and
// underscore directories, and nested modules.
func (l *loader) goDirs() []string {
	var dirs []string
	_ = filepath.WalkDir(l.dir, func(path string, d fs.DirEntry, err error) error {
		if err != nil {
			return nil // unreadable subtree: skip silently
		}
		if d.IsDir() {
			name := d.Name()
			if !samePath(path, l.dir) {
				if name == "vendor" || name == "testdata" ||
					strings.HasPrefix(name, ".") || strings.HasPrefix(name, "_") {
					return fs.SkipDir
				}
				if fi, serr := os.Stat(filepath.Join(path, "go.mod")); serr == nil && !fi.IsDir() {
					return fs.SkipDir // nested module
				}
			}
			return nil
		}
		name := d.Name()
		if strings.HasSuffix(name, ".go") && !strings.HasSuffix(name, "_test.go") &&
			!strings.HasPrefix(name, ".") && !strings.HasPrefix(name, "_") {
			dir := filepath.Dir(path)
			if len(dirs) == 0 || dirs[len(dirs)-1] != dir {
				dirs = append(dirs, dir)
			}
		}
		return nil
	})
	sort.Strings(dirs)
	return dirs
}

// checkModule type-checks every package of the module and returns the
// accumulated parse and type diagnostics sorted by position.
func (l *loader) checkModule() []diagnostic {
	for _, dir := range l.goDirs() {
		groups := l.packagesIn(dir, false)
		var names []string
		for name := range groups {
			names = append(names, name)
		}
		sort.Strings(names)
		path := l.importPathFor(dir)
		for _, name := range names {
			// Errors are already collected via collectTypeError.
			_, _ = l.checkGroup(dir, path, name, groups[name], nil)
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
	return l.diags
}
