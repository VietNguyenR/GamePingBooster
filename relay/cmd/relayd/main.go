// relayd is the UDP relay for Game Ping Booster.
//
// It runs on a Linux VPS (Singapore / Hong Kong). It receives IP packets that the
// client has encapsulated, writes them into a TUN device, and lets the Linux kernel
// handle NAT and forwarding out to the internet. The return path is the reverse.
//
// Quick start:
//
//	sudo ./relayd -psk-file /etc/gpb/psk -listen :51820
package main

import (
	"flag"
	"fmt"
	"log/slog"
	"net/netip"
	"os"
	"os/signal"
	"strings"
	"syscall"
	"time"

	"github.com/gamepingbooster/relay/internal/server"
)

func main() {
	var (
		listen      = flag.String("listen", ":51820", "UDP listen address")
		tunName     = flag.String("tun", "gpb0", "TUN interface name")
		subnetStr   = flag.String("subnet", "10.77.0.0/24", "inner IP pool handed to clients")
		mtu         = flag.Int("mtu", 1400, "TUN MTU (must match the client)")
		pskFile     = flag.String("psk-file", "", "path to the pre-shared key file (takes precedence over GPB_PSK)")
		idleTimeout = flag.Duration("idle-timeout", 90*time.Second, "drop a session after this long without packets")
		configureIf = flag.Bool("configure-if", true, "run `ip addr/link` to configure the TUN device")
		rateKBps    = flag.Int64("rate-limit", 512, "per-session cap in KB/s each way, 0 disables it; a real game session uses about 10")
		burstKB     = flag.Int64("rate-burst", 0, "burst allowance in KB, 0 means four seconds at the sustained rate")
		logLevel    = flag.String("log-level", "info", "debug | info | warn | error")
	)
	flag.Parse()

	log := newLogger(*logLevel)

	psk, err := loadPSK(*pskFile)
	if err != nil {
		log.Error("could not read the PSK", "err", err)
		os.Exit(1)
	}

	subnet, err := netip.ParsePrefix(*subnetStr)
	if err != nil {
		log.Error("invalid subnet", "subnet", *subnetStr, "err", err)
		os.Exit(1)
	}

	srv, err := server.New(server.Config{
		Listen:      *listen,
		TunName:     *tunName,
		Subnet:      subnet,
		MTU:         *mtu,
		PSK:         psk,
		IdleTimeout: *idleTimeout,
		// Stated in KB on the command line because that is how anyone reasons about it, and
		// converted here once rather than at every packet.
		RateBytesPerSec: *rateKBps * 1024,
		BurstBytes:      *burstKB * 1024,
		ConfigureIf:     *configureIf,
		Log:             log,
	})
	if err != nil {
		log.Error("relay failed to start", "err", err)
		os.Exit(1)
	}

	done := make(chan struct{})
	sig := make(chan os.Signal, 1)
	signal.Notify(sig, os.Interrupt, syscall.SIGTERM)
	go func() {
		s := <-sig
		log.Info("shutdown signal received, closing", "signal", s.String())
		close(done)
	}()

	if err := srv.Run(done); err != nil {
		log.Error("relay stopped with an error", "err", err)
		os.Exit(1)
	}
	log.Info("relay stopped cleanly")
}

// loadPSK reads the key from a file (preferred, chmod 600) or from GPB_PSK.
func loadPSK(path string) ([]byte, error) {
	if path != "" {
		b, err := os.ReadFile(path)
		if err != nil {
			return nil, err
		}
		psk := []byte(strings.TrimSpace(string(b)))
		if len(psk) < 16 {
			return nil, fmt.Errorf("the PSK in %s is too short (%d bytes), need at least 16", path, len(psk))
		}
		return psk, nil
	}
	if env := strings.TrimSpace(os.Getenv("GPB_PSK")); env != "" {
		if len(env) < 16 {
			return nil, fmt.Errorf("GPB_PSK is too short (%d bytes), need at least 16", len(env))
		}
		return []byte(env), nil
	}
	return nil, fmt.Errorf("no PSK configured: pass -psk-file or set GPB_PSK")
}

func newLogger(level string) *slog.Logger {
	var l slog.Level
	switch strings.ToLower(level) {
	case "debug":
		l = slog.LevelDebug
	case "warn":
		l = slog.LevelWarn
	case "error":
		l = slog.LevelError
	default:
		l = slog.LevelInfo
	}
	// TextHandler reads well straight out of `journalctl -u relayd -f`.
	return slog.New(slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{Level: l}))
}
