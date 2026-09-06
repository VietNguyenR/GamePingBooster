// relayd is the UDP relay for Game Ping Booster.
//
// It runs on a Linux VPS (Singapore / Hong Kong). It receives IP packets that the
// client has encapsulated, writes them into a TUN device, and lets the Linux kernel
// handle NAT and forwarding out to the internet. The return path is the reverse.
//
// It serves exactly ONE authentication mode, and refuses to start if given both or neither.
//
// Self-hosted, one shared key:
//
//	sudo ./relayd -psk-file /etc/gpb/psk -listen :51820
//
// Licensed, per-client tokens verified offline against the licence server's public key:
//
//	sudo ./relayd -licence-key /etc/gpb/licence.pub -listen :51820
//
// In the licensed mode the relay also needs a key of its own to sign answers with, since there
// is no shared secret to sign them with instead. It generates one on first run and prints the
// public half, which goes into the profile beside this relay's endpoint.
package main

import (
	"crypto/ecdsa"
	"encoding/hex"
	"flag"
	"fmt"
	"log/slog"
	"net/netip"
	"os"
	"os/signal"
	"strings"
	"syscall"
	"time"

	"github.com/gamepingbooster/relay/internal/protocol"
	"github.com/gamepingbooster/relay/internal/server"
)

func main() {
	var (
		listen      = flag.String("listen", ":51820", "UDP listen address")
		tunName     = flag.String("tun", "gpb0", "TUN interface name")
		subnetStr   = flag.String("subnet", "10.77.0.0/24", "inner IP pool handed to clients")
		mtu         = flag.Int("mtu", 1400, "TUN MTU (must match the client)")
		pskFile     = flag.String("psk-file", "", "self-hosted mode: path to the pre-shared key file (takes precedence over GPB_PSK)")
		licenceKey  = flag.String("licence-key", "", "licensed mode: path to the licence server's PUBLIC key, hex")
		relayKey    = flag.String("relay-key", "/etc/gpb/relay.key", "licensed mode: this relay's own private key, hex; created if missing")
		maxAge      = flag.Duration("max-session-age", protocol.MaxSessionAge, "force a client to handshake again after this long")
		idleTimeout = flag.Duration("idle-timeout", 90*time.Second, "drop a session after this long without packets")
		configureIf = flag.Bool("configure-if", true, "run `ip addr/link` to configure the TUN device")
		maxClients  = flag.Int("max-clients", 0, "refuse new sessions past this many at once, 0 means the address pool is the only limit")
		rateKBps    = flag.Int64("rate-limit", 64, "per-session cap in KB/s each way, 0 disables it; a real game session uses about 10")
		burstKB     = flag.Int64("rate-burst", 0, "burst allowance in KB, 0 means four seconds at the sustained rate")
		logLevel    = flag.String("log-level", "info", "debug | info | warn | error")
	)
	printRelayKey := flag.Bool("print-relay-key", false,
		"print this relay's own public key and exit; creates the key file if it does not exist")

	flag.Parse()

	log := newLogger(*logLevel)

	// Before the mode checks, because this needs neither mode and is run on a relay that may not
	// be configured yet.
	//
	// The key is also logged at startup, but only in the mode that uses it, and reading it back
	// then means grepping journalctl on a machine somebody is ssh'd into at 2am. It has to reach
	// the profile somehow, and there is nowhere else to read it from once the log has rotated.
	if *printRelayKey {
		key, created, err := loadOrCreateRelayKey(*relayKey)
		if err != nil {
			log.Error("could not load this relay's own key", "err", err)
			os.Exit(1)
		}
		if created {
			log.Warn("no key existed, so one was generated", "file", *relayKey)
		}
		fmt.Println(hex.EncodeToString(protocol.MarshalPublicKey(&key.PublicKey)))
		return
	}

	// Exactly one mode. Deciding here rather than in server.New means the error names the flag
	// the operator actually typed.
	if *pskFile == "" && os.Getenv("GPB_PSK") == "" && *licenceKey == "" {
		log.Error("no authentication configured - the relay would be an open proxy",
			"fix", "pass -psk-file for a self-hosted relay, or -licence-key for a licensed one")
		os.Exit(1)
	}
	if (*pskFile != "" || os.Getenv("GPB_PSK") != "") && *licenceKey != "" {
		log.Error("both authentication modes were configured, but a relay serves exactly one",
			"fix", "pass -psk-file or -licence-key, not both")
		os.Exit(1)
	}

	var (
		psk        []byte
		licencePub *ecdsa.PublicKey
		relayPriv  *ecdsa.PrivateKey
		err        error
	)

	if *licenceKey != "" {
		licencePub, err = loadLicenceKey(*licenceKey)
		if err != nil {
			log.Error("could not read the licence key", "err", err)
			os.Exit(1)
		}

		var created bool
		relayPriv, created, err = loadOrCreateRelayKey(*relayKey)
		if err != nil {
			log.Error("could not load this relay's own key", "err", err)
			os.Exit(1)
		}
		pubHex := hex.EncodeToString(protocol.MarshalPublicKey(&relayPriv.PublicKey))
		if created {
			// Loud, and only once: this value has to reach the profile or no client will accept
			// this relay's answers, and there is nowhere else to read it from afterwards.
			log.Warn("generated a new relay key - put its public half in the profile, "+
				"beside this relay's endpoint, or clients will reject every answer it sends",
				"file", *relayKey, "public_key", pubHex)
		} else {
			log.Info("relay identity loaded", "public_key", pubHex)
		}
	} else {
		psk, err = loadPSK(*pskFile)
		if err != nil {
			log.Error("could not read the PSK", "err", err)
			os.Exit(1)
		}
	}

	subnet, err := netip.ParsePrefix(*subnetStr)
	if err != nil {
		log.Error("invalid subnet", "subnet", *subnetStr, "err", err)
		os.Exit(1)
	}

	srv, err := server.New(server.Config{
		Listen:        *listen,
		TunName:       *tunName,
		Subnet:        subnet,
		MTU:           *mtu,
		PSK:           psk,
		LicencePub:    licencePub,
		RelayPriv:     relayPriv,
		IdleTimeout:   *idleTimeout,
		MaxSessionAge: *maxAge,
		MaxClients:    *maxClients,
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
