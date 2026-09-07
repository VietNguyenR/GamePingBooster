// Telemetry: what this relay is doing right now, posted to the licence server on a timer.
//
// The shape of the thing is the point, so it is worth stating plainly. Every report is an
// ABSOLUTE SNAPSHOT, never an event and never a delta. A relay does not say "one client
// connected"; it says "these seven sessions exist, at this instant".
//
// Deltas were the obvious first design and they are wrong here. A missed connect - the licence
// server restarting, a dropped request, this process being killed mid-match - leaves the far end
// permanently off by one, with nothing anywhere that can notice or repair it. Reconciling would
// need a periodic full snapshot anyway, at which point the deltas are doing no work. A snapshot
// heals on its own: lose one and the next is still right, restart the relay and the count
// honestly becomes zero.
//
// The second decision is the direction. The relay pushes rather than exposing something to be
// scraped, which needs no inbound port, no certificate on the VPS and no firewall change, and it
// gives liveness for free: no report for a few intervals means the box is gone, which a
// pull-based design has to infer from its own failures.
//
// The third is that none of this may touch the data plane. The report runs on its own goroutine
// with its own HTTP client and a short timeout, it takes the session lock once per interval
// rather than once per packet, and a failure is dropped rather than queued. loopUDP and loopTUN
// do not know it exists. If the licence server is down, players keep playing - which is also what
// keeps the "a relay verifies offline and calls no API" rule intact: this call is telemetry, and
// nothing about admitting a client waits on it.
package server

import (
	"bytes"
	"crypto/ecdsa"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"net/http"
	"strconv"
	"time"

	"github.com/gamepingbooster/relay/internal/protocol"
)

const (
	defaultReportInterval = 20 * time.Second
	minReportInterval     = 5 * time.Second
	reportTimeout         = 5 * time.Second

	// reportVersion is the body format. The licence server refuses anything it does not know
	// rather than guessing at a field that moved.
	reportVersion = 1

	// maxReportedSessions caps the device list so one enormous relay cannot post a body the
	// licence server has to refuse whole. The count is always complete; only the per-device
	// detail is cut, and the report says when it was.
	maxReportedSessions = 500

	// reportDomain separates this signature from every other thing the relay key signs.
	//
	// The same key also signs handshake answers. Signing two different message formats with one
	// key is how a signature from one protocol gets replayed as a valid message in the other; a
	// fixed prefix that the handshake format can never begin with makes that impossible.
	reportDomain = "gpb-relay-report-v1\n"
)

// Report is the snapshot body. Field names are a contract with the licence server.
type Report struct {
	V       int    `json:"v"`
	TS      int64  `json:"ts"`       // unix seconds, checked for skew at the far end
	UptimeS int64  `json:"uptime_s"` // how long this process has been up
	Version string `json:"version"`  // relayd build
	Mode    string `json:"mode"`     // "psk" or "token"
	Listen  string `json:"listen"`
	Subnet  string `json:"subnet"`

	Sessions   int `json:"sessions"` // the number everything else here exists to support
	MaxClients int `json:"max_clients"`
	PoolFree   int `json:"pool_free"`
	Reserved   int `json:"reserved"`

	RxPackets uint64 `json:"rx_packets"`
	TxPackets uint64 `json:"tx_packets"`
	RxBytes   uint64 `json:"rx_bytes"`
	TxBytes   uint64 `json:"tx_bytes"`
	Dropped   uint64 `json:"dropped"`
	Limited   uint64 `json:"rate_limited"`

	// Devices is every live session. Truncated is true when there were more than the cap, so a
	// short list is never mistaken for a small relay.
	Devices   []ReportDevice `json:"devices"`
	Truncated bool           `json:"truncated,omitempty"`
}

// ReportDevice is one live session.
type ReportDevice struct {
	SessionID  string `json:"sid"`      // 8 bytes of hex
	InnerIP    string `json:"inner_ip"` // the address this session holds inside the tunnel
	ClientIP   string `json:"client_ip"`
	ClientPort int    `json:"client_port"`

	// UserID is a STRING holding an unsigned 64-bit number. As a JSON number it would pass
	// through JavaScript's double and come back altered above 2^53 - silently, and only for some
	// users. Empty in PSK mode.
	UserID string `json:"user_id,omitempty"`

	// DeviceKey is 130 hex characters, exactly as the licence server stored it against a Device
	// row. It is what a report is resolved by; user_id is a one-way hash and cannot be.
	DeviceKey string `json:"device_key,omitempty"`

	ConnectedAt int64 `json:"connected_at"` // unix seconds
	LastSeen    int64 `json:"last_seen"`    // unix seconds
	Resumed     bool  `json:"resumed,omitempty"`
}

// Snapshot describes the relay at this instant.
//
// It walks the session map under the read lock rather than keeping a running counter beside it.
// A counter would be a second source of truth for a number the map already holds, free to drift
// from it and impossible to check; and the walk has to happen anyway for the per-device detail.
// The cost is one read lock every twenty seconds over a map of at most a few dozen entries, on a
// socket that already takes the same lock for every packet it forwards.
func (s *Server) Snapshot(now time.Time) Report {
	mode := "psk"
	if s.cfg.LicencePub != nil {
		mode = "token"
	}
	listen := s.cfg.Listen
	if s.conn != nil {
		listen = s.conn.LocalAddr().String()
	}

	rep := Report{
		V:          reportVersion,
		TS:         now.Unix(),
		UptimeS:    int64(now.Sub(s.started).Seconds()),
		Version:    s.cfg.Version,
		Mode:       mode,
		Listen:     listen,
		Subnet:     s.cfg.Subnet.String(),
		MaxClients: s.cfg.MaxClients,
		RxPackets:  s.stats.rxPackets.Load(),
		TxPackets:  s.stats.txPackets.Load(),
		RxBytes:    s.stats.rxBytes.Load(),
		TxBytes:    s.stats.txBytes.Load(),
		Dropped:    s.stats.dropped.Load(),
		Limited:    s.stats.limited.Load(),
	}

	s.mu.RLock()
	rep.Sessions = len(s.bySession)
	rep.PoolFree = len(s.freeIPs)
	rep.Reserved = len(s.reservedIPs)
	rep.Devices = make([]ReportDevice, 0, min(rep.Sessions, maxReportedSessions))
	for _, sess := range s.bySession {
		if len(rep.Devices) >= maxReportedSessions {
			rep.Truncated = true
			break
		}
		d := ReportDevice{
			SessionID:   hex.EncodeToString(sess.id[:]),
			InnerIP:     sess.innerIP.String(),
			ConnectedAt: sess.born / int64(time.Second),
			LastSeen:    sess.lastSeen.Load() / int64(time.Second),
			Resumed:     sess.resumed,
		}
		if a := sess.addr.Load(); a != nil {
			d.ClientIP = a.Addr().String()
			d.ClientPort = int(a.Port())
		}
		if sess.ident.userID != 0 {
			d.UserID = strconv.FormatUint(sess.ident.userID, 10)
		}
		if len(sess.ident.deviceKey) == protocol.PublicKeyLen {
			d.DeviceKey = hex.EncodeToString(sess.ident.deviceKey)
		}
		rep.Devices = append(rep.Devices, d)
	}
	s.mu.RUnlock()

	return rep
}

// loopReport posts a snapshot on a timer for as long as the relay runs.
//
// It returns immediately when no URL is configured, which is the default and what every
// self-hosted relay does: a relay nobody's licence server knows about has nowhere to report to,
// and should not be making outbound calls at all.
func (s *Server) loopReport(done <-chan struct{}) {
	if s.cfg.ReportURL == "" {
		return
	}
	if s.cfg.RelayPriv == nil {
		// Refused at startup, so this is unreachable from the command line. Logged rather than
		// fatal: a mistake in the telemetry setup must not take a working relay down.
		s.log.Error("reporting is configured but this relay has no key to sign with - not reporting")
		return
	}

	interval := s.cfg.ReportInterval
	if interval <= 0 {
		interval = defaultReportInterval
	}
	if interval < minReportInterval {
		interval = minReportInterval
	}

	client := &http.Client{Timeout: reportTimeout}
	pub := hex.EncodeToString(protocol.MarshalPublicKey(&s.cfg.RelayPriv.PublicKey))

	s.log.Info("reporting status to the licence server",
		"url", s.cfg.ReportURL, "every", interval.String(), "as", pub[:16]+"...")

	t := time.NewTicker(interval)
	defer t.Stop()

	var failures int
	send := func() {
		if err := s.postReport(client, s.cfg.RelayPriv, pub); err != nil {
			failures++
			// Loud once, then rarely. A licence server down for a day would otherwise write four
			// thousand identical lines into the relay's log, which is how the one line that
			// mattered gets buried.
			if failures == 1 || failures%30 == 0 {
				s.log.Warn("could not post the status report - the relay itself is unaffected",
					"err", err, "consecutive_failures", failures)
			}
			return
		}
		if failures > 0 {
			s.log.Info("status reporting recovered", "after_failures", failures)
			failures = 0
		}
	}

	// The first report goes out at once rather than after a full interval: a relay that has just
	// been restarted is exactly when somebody is watching the dashboard.
	send()
	for {
		select {
		case <-done:
			return
		case <-t.C:
			send()
		}
	}
}

// postReport signs and sends one snapshot.
func (s *Server) postReport(client *http.Client, priv *ecdsa.PrivateKey, pub string) error {
	body, err := json.Marshal(s.Snapshot(time.Now()))
	if err != nil {
		return fmt.Errorf("encode: %w", err)
	}

	// Signed over the exact bytes that go on the wire, with a domain prefix. The far end verifies
	// before it parses, so a body it cannot trust is never handed to a JSON decoder.
	sig, err := protocol.Sign(priv, append([]byte(reportDomain), body...))
	if err != nil {
		return fmt.Errorf("sign: %w", err)
	}

	req, err := http.NewRequest(http.MethodPost, s.cfg.ReportURL, bytes.NewReader(body))
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("User-Agent", "relayd/"+s.cfg.Version)
	// The key says which relay this is. The licence server looks the relay up by it and then
	// checks the signature against the key it has on file - never against this header, which is
	// only a hint about which row to fetch.
	req.Header.Set("X-Gpb-Relay-Key", pub)
	req.Header.Set("X-Gpb-Signature", hex.EncodeToString(sig))

	resp, err := client.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	// The body is discarded. There is nothing the licence server can say that this relay acts on,
	// by design: a control channel is a far bigger thing than a counter, and this is not one.
	if resp.StatusCode < 200 || resp.StatusCode > 299 {
		return fmt.Errorf("%s said %s", s.cfg.ReportURL, resp.Status)
	}
	return nil
}
