package main

import "testing"

func TestGreet(t *testing.T) {
	cases := []struct {
		name string
		in   string
		want string
	}{
		{"named", "epic", "hello from epic"},
		{"empty falls back to world", "", "hello from world"},
	}
	for _, c := range cases {
		got := Greet(c.in)
		if got != c.want {
			t.Errorf("%s: Greet(%q) = %q, want %q", c.name, c.in, got, c.want)
		}
	}
}
