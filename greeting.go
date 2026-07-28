package main

import "fmt"

// Greet returns a friendly greeting for the given name.
// Written against Go 1.13 — no generics, no go1.18+ features.
func Greet(name string) string {
	if name == "" {
		name = "world"
	}
	return fmt.Sprintf("hello from %s", name)
}
