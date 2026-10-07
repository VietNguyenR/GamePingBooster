//go:build !linux

package main

import (
	"log/slog"
	"os"

	"github.com/gamepingbooster/relay/internal/server"
)

// Hot restart needs exec and a TUN device, both Linux-only here. These keep `go build ./...`
// working on the Windows dev machine.

var hotRestartSignals []os.Signal

func isHotRestartSignal(os.Signal) bool               { return false }
func hotRestart(*server.Server, *slog.Logger)         {}
func inheritedFromParent() (*server.Inherited, error) { return nil, nil }
