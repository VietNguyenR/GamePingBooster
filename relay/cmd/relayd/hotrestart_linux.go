//go:build linux

package main

// The process half of a hot restart - see internal/server/handoff.go for what it is and why.
//
// SIGUSR2 asks for one. install.sh sends it after replacing /usr/local/bin/relayd, through
// `systemctl reload relayd` (the unit's ExecReload), and reads the journal for the new binary's
// "hot restart: resumed" line before it calls the deploy good.

import (
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net"
	"os"
	"strings"
	"syscall"

	"github.com/gamepingbooster/relay/internal/server"
)

// handoffEnv carries the inherited descriptors to the new binary: "udp,tun,state".
const handoffEnv = "GPB_HANDOFF_FDS"

var hotRestartSignals = []os.Signal{syscall.SIGUSR2}

func isHotRestartSignal(s os.Signal) bool { return s == syscall.SIGUSR2 }

// hotRestart exec's the binary now at this process's path in its place. It returns only on
// failure, with nothing closed, and the relay carries on as it was.
func hotRestart(srv *server.Server, log *slog.Logger) {
	// os.Executable reads /proc/self/exe and drops the " (deleted)" the kernel appends once the
	// file has been replaced, so this is the path - and therefore the NEW binary - not the old inode.
	exe, err := os.Executable()
	if err != nil {
		log.Error("hot restart refused: cannot find this binary's path", "err", err)
		return
	}
	if _, err := os.Stat(exe); err != nil {
		log.Error("hot restart refused: no binary to switch to", "path", exe, "err", err)
		return
	}

	err = srv.Handoff(func(state []byte, udp, tun syscall.RawConn) error {
		var fds []int
		closeAll := func() {
			for _, fd := range fds {
				syscall.Close(fd)
			}
		}
		// dup() gives a descriptor WITHOUT close-on-exec, which is exactly the one property needed:
		// every descriptor Go opens itself has it set, and would vanish at exec.
		dupRaw := func(rc syscall.RawConn) error {
			var derr error
			if err := rc.Control(func(fd uintptr) {
				var nfd int
				nfd, derr = syscall.Dup(int(fd))
				if derr == nil {
					fds = append(fds, nfd)
				}
			}); err != nil {
				return err
			}
			return derr
		}
		if err := dupRaw(udp); err != nil {
			closeAll()
			return fmt.Errorf("udp socket: %w", err)
		}
		if err := dupRaw(tun); err != nil {
			closeAll()
			return fmt.Errorf("tun device: %w", err)
		}
		stateFd, err := stateFile(state)
		if err != nil {
			closeAll()
			return err
		}
		fds = append(fds, stateFd)

		env := []string{fmt.Sprintf("%s=%d,%d,%d", handoffEnv, fds[0], fds[1], fds[2])}
		for _, kv := range os.Environ() {
			if !strings.HasPrefix(kv, handoffEnv+"=") {
				env = append(env, kv)
			}
		}
		err = syscall.Exec(exe, os.Args, env)
		closeAll()
		return fmt.Errorf("exec %s: %w", exe, err)
	})
	log.Error("hot restart failed, still running the current binary", "err", err)
}

// stateFile writes the table to an already-unlinked temporary file and returns a descriptor on it,
// rewound, for the next binary to read. Unlinked so a crash at any point leaves nothing behind.
func stateFile(state []byte) (int, error) {
	f, err := os.CreateTemp("", "relayd-handoff-*")
	if err != nil {
		return -1, fmt.Errorf("state file: %w", err)
	}
	defer f.Close()
	os.Remove(f.Name())
	if _, err := f.Write(state); err != nil {
		return -1, fmt.Errorf("state file: %w", err)
	}
	fd, err := syscall.Dup(int(f.Fd()))
	if err != nil {
		return -1, fmt.Errorf("state file: %w", err)
	}
	if _, err := syscall.Seek(fd, 0, io.SeekStart); err != nil {
		syscall.Close(fd)
		return -1, fmt.Errorf("state file: %w", err)
	}
	return fd, nil
}

// inheritedFromParent picks up what a hot restart handed this process, or returns nil for an
// ordinary start.
func inheritedFromParent() (*server.Inherited, error) {
	v, ok := os.LookupEnv(handoffEnv)
	if !ok {
		return nil, nil
	}
	// Not passed on to anything this process starts later.
	os.Unsetenv(handoffEnv)

	var udpFd, tunFd, stateFd int
	if _, err := fmt.Sscanf(v, "%d,%d,%d", &udpFd, &tunFd, &stateFd); err != nil {
		return nil, fmt.Errorf("bad %s %q: %w", handoffEnv, v, err)
	}
	for _, fd := range []int{udpFd, tunFd, stateFd} {
		syscall.CloseOnExec(fd)
	}

	uf := os.NewFile(uintptr(udpFd), "udp")
	pc, err := net.FilePacketConn(uf) // dups; the original is closed below
	uf.Close()
	if err != nil {
		return nil, fmt.Errorf("udp socket: %w", err)
	}
	conn, ok := pc.(*net.UDPConn)
	if !ok {
		pc.Close()
		return nil, errors.New("the inherited socket is not UDP")
	}

	sf := os.NewFile(uintptr(stateFd), "state")
	state, _ := io.ReadAll(sf) // an unreadable table is handled by the server: clients reconnect
	sf.Close()

	return &server.Inherited{UDP: conn, TUN: os.NewFile(uintptr(tunFd), "tun"), State: state}, nil
}
