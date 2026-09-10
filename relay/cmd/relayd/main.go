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

// version is stamped in at build time with -ldflags "-X main.version=0.1.4". It is only ever
// displayed - in the log line at startup and in the status report - so an operator can tell which
// relay is still running last month's binary.
var version = "dev"

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
		minTier     = flag.Int("min-tier", 0, "licensed mode: lowest plan tier this relay serves, 0 serves everyone; must match the relay's minTier on the licence server")
		rateKBps    = flag.Int64("rate-limit", 64, "per-session cap in KB/s each way, 0 disables it; a real game session uses about 10")
		burstKB     = flag.Int64("rate-burst", 0, "burst allowance in KB, 0 means four seconds at the sustained rate")
		logLevel    = flag.String("log-level", "info", "debug | info | warn | error")

		// Telemetry. Off unless a URL is given, which is what a self-hosted relay wants: it is
		// in nobody's dashboard and should make no outbound calls.
		//
		// The URL is OPERATOR configuration - a flag here, or GPB_REPORT_URL in the environment,
		// which is how the systemd unit delivers it. It is deliberately NOT hard-coded, and it is
		// deliberately not something a client can influence: a client that could name this
		// address could aim the relay at any host on the internet, the cloud metadata service on
		// 169.254.169.254 included.
		reportURL      = flag.String("report-url", "", "post a status snapshot here every -report-interval; empty disables it (env GPB_REPORT_URL)")
		reportEvery    = flag.Duration("report-interval", 20*time.Second, "how often to post the status snapshot")
		reportInsecure = flag.Bool("report-insecure", false, "allow a plain http:// report URL; for a development licence server only")
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

	// The flag wins over the environment, matching -psk-file. The environment is how the systemd
	// unit passes it, via EnvironmentFile=-/etc/gpb/relayd.env, so that changing where a relay
	// reports is one line in one file rather than an edit to the unit on every VPS.
	reportTo := strings.TrimSpace(*reportURL)
	if reportTo == "" {
		reportTo = strings.TrimSpace(os.Getenv("GPB_REPORT_URL"))
	}
	if reportTo != "" {
		if !strings.HasPrefix(reportTo, "https://") {
			// The body is signed, not encrypted. Over http anybody on the path reads how many
			// customers are on this relay and which devices they are - and can also see the
			// endpoint worth attacking. An explicit flag exists for a development server on
			// localhost; nothing else should ever want it.
			if !*reportInsecure || !strings.HasPrefix(reportTo, "http://") {
				log.Error("the report URL must be https://",
					"url", reportTo, "fix", "use https, or -report-insecure for a development server")
				os.Exit(1)
			}
			log.Warn("posting status reports over plain http - development only", "url", reportTo)
		}
	}

	// Range-checked rather than cast, because the quiet failure is the dangerous one: byte(256)
	// is 0, and 0 is "serve everyone". A premium relay whose flag was fat-fingered would come up
	// looking healthy while enforcing nothing at all.
	if *minTier < 0 || *minTier > 255 {
		log.Error("-min-tier must be 0-255", "given", *minTier)
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

		// A tier arrives inside a licence token, and a PSK relay never sees one. Setting the flag
		// here enforces nothing, and saying so is the difference between an operator finding out
		// now and finding out when somebody who should not have got in is already in.
		if *minTier > 0 {
			log.Warn("-min-tier is ignored in PSK mode: tiers live in licence tokens, and a "+
				"self-hosted relay authenticates with a shared key that carries no plan",
				"min_tier", *minTier)
		}

		// A PSK relay has no key of its own, because a shared secret makes a forged answer
		// impossible without one. Reporting still needs an identity: the licence server has to
		// know which relay a snapshot came from, and a name in the body would be a name anybody
		// could type. So a reporting PSK relay gets the same P-256 key a licensed one has, used
		// for nothing but signing its own reports.
		if reportTo != "" {
			var created bool
			relayPriv, created, err = loadOrCreateRelayKey(*relayKey)
			if err != nil {
				log.Error("could not load this relay's own key", "err", err)
				os.Exit(1)
			}
			pubHex := hex.EncodeToString(protocol.MarshalPublicKey(&relayPriv.PublicKey))
			if created {
				log.Warn("generated a new relay key for status reporting - paste its public half "+
					"into the relay's row in the admin dashboard, or its reports will be refused",
					"file", *relayKey, "public_key", pubHex)
			} else {
				log.Info("relay identity loaded", "public_key", pubHex)
			}
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
		MinTier:       byte(*minTier),
		// Stated in KB on the command line because that is how anyone reasons about it, and
		// converted here once rather than at every packet.
		RateBytesPerSec: *rateKBps * 1024,
		BurstBytes:      *burstKB * 1024,
		ConfigureIf:     *configureIf,
		ReportURL:       reportTo,
		ReportInterval:  *reportEvery,
		Version:         version,
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
	// TextHandler reads well straight out of `journalctl -u relayd -f`, which captures stderr
	// and stdout alike.
	//
	// stderr rather than stdout, so that stdout carries only real program OUTPUT - which is one
	// thing, the key printed by -print-relay-key. install.sh captures that in a command
	// substitution, and a log line landing in the same stream put a WARN in the middle of the
	// key it was meant to print.
	return slog.New(slog.NewTextHandler(os.Stderr, &slog.HandlerOptions{Level: l}))
}
