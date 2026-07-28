package main

import (
	"fmt"
	"go/ast"
	"go/token"
	"go/types"
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

type declaredAt struct {
	File string `json:"file"`
	Line int    `json:"line"`
	Col  int    `json:"col"`
}

// symbolResult is the object printed by the symbol command. A nil Name
// marshals to {"name":null}: no symbol at the requested position.
type symbolResult struct {
	Name       *string     `json:"name"`
	Kind       string      `json:"kind,omitempty"`
	Type       string      `json:"type,omitempty"`
	DeclaredAt *declaredAt `json:"declaredAt,omitempty"`
}

// cmdSymbol implements "orika-goc symbol <file.go> <line> <col>". The
// module directory is taken from -dir or inferred from the nearest go.mod
// above the file.
func cmdSymbol(args []string) int {
	fs := newFlagSet("symbol")
	dirFlag := fs.String("dir", "", "module `directory` the file belongs to (default: inferred from go.mod)")
	pretty := fs.Bool("pretty", false, "indent the JSON output")
	rest, code := parseArgs(fs, args, 3)
	if code >= 0 {
		return code
	}
	if len(rest) != 3 {
		fmt.Fprintln(os.Stderr, "usage: orika-goc symbol <file.go> <line> <col> [-dir <moduleDir>]")
		return 2
	}
	file, err := filepath.Abs(rest[0])
	if err != nil {
		return infra(err)
	}
	line, lerr := strconv.Atoi(rest[1])
	col, cerr := strconv.Atoi(rest[2])
	if lerr != nil || cerr != nil || line < 1 || col < 1 {
		fmt.Fprintln(os.Stderr, "orika-goc: line and col must be positive integers (1-based)")
		return 2
	}
	if _, err := os.Stat(file); err != nil {
		return infra(err)
	}

	moduleDir := *dirFlag
	if moduleDir == "" {
		moduleDir = findModuleRoot(filepath.Dir(file))
	}
	if moduleDir == "" {
		moduleDir = filepath.Dir(file)
	}
	l, err := newLoader(moduleDir)
	if err != nil {
		return infra(err)
	}
	res, err := l.symbolAt(file, line, col)
	if err != nil {
		return infra(err)
	}
	return emit(res, *pretty)
}

// symbolAt type-checks the package containing file and reports the symbol
// used or declared at the given 1-based position. Errors in the source are
// tolerated: the lookup works off whatever go/types could compute.
func (l *loader) symbolAt(file string, line, col int) (symbolResult, error) {
	pkgDir := filepath.Dir(file)
	includeTests := strings.HasSuffix(strings.ToLower(filepath.Base(file)), "_test.go")
	groups := l.packagesIn(pkgDir, includeTests)

	var (
		groupName string
		files     []*ast.File
		target    *ast.File
	)
	for name, fl := range groups {
		for _, f := range fl {
			if samePath(l.fileName[f], file) {
				groupName, files, target = name, fl, f
				break
			}
		}
		if target != nil {
			break
		}
	}
	if target == nil {
		// The file is excluded by build constraints or otherwise not part
		// of a package group; fall back to checking it on its own.
		f := l.parseFile(file)
		if f == nil {
			return symbolResult{}, fmt.Errorf("cannot parse %s", file)
		}
		groupName = "main"
		if f.Name != nil {
			groupName = f.Name.Name
		}
		files, target = []*ast.File{f}, f
	}

	info := &types.Info{
		Defs: map[*ast.Ident]types.Object{},
		Uses: map[*ast.Ident]types.Object{},
	}
	// Type errors are collected as diagnostics but deliberately ignored
	// here: partial information is enough for a best-effort lookup.
	_, _ = l.checkGroup(pkgDir, l.importPathFor(pkgDir), groupName, files, info)

	id := identAt(l.fset, target, line, col)
	if id == nil {
		return symbolResult{}, nil
	}
	obj := info.Defs[id]
	if obj == nil {
		obj = info.Uses[id]
	}
	if obj == nil {
		return symbolResult{}, nil
	}

	name := obj.Name()
	res := symbolResult{Name: &name, Kind: objectKind(obj), Type: objectType(obj)}
	if p := l.fset.Position(obj.Pos()); p.IsValid() {
		declFile := p.Filename
		if abs, err := filepath.Abs(declFile); err == nil {
			declFile = abs
		}
		res.DeclaredAt = &declaredAt{File: declFile, Line: p.Line, Col: p.Column}
	}
	return res, nil
}

// identAt finds the identifier spanning the given 1-based position, if any.
func identAt(fset *token.FileSet, f *ast.File, line, col int) *ast.Ident {
	var found *ast.Ident
	ast.Inspect(f, func(n ast.Node) bool {
		if found != nil || n == nil {
			return false
		}
		id, ok := n.(*ast.Ident)
		if !ok {
			return true
		}
		p := fset.Position(id.Pos())
		e := fset.Position(id.End())
		if p.Line == line && col >= p.Column && col < e.Column {
			found = id
		}
		return false
	})
	return found
}

func objectKind(obj types.Object) string {
	switch o := obj.(type) {
	case *types.PkgName:
		return "package"
	case *types.Const:
		return "const"
	case *types.TypeName:
		return "type"
	case *types.Var:
		if o.IsField() {
			return "field"
		}
		return "var"
	case *types.Func:
		return "func"
	case *types.Label:
		return "label"
	case *types.Builtin:
		return "builtin"
	case *types.Nil:
		return "nil"
	default:
		return "object"
	}
}

func objectType(obj types.Object) string {
	t := obj.Type()
	if t == nil {
		return ""
	}
	if b, ok := t.(*types.Basic); ok && b.Kind() == types.Invalid {
		return "" // package names and builtins have no meaningful type
	}
	return t.String()
}
