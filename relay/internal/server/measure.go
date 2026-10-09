package server

import (
	"net/netip"
	"sync"
	"time"

	"github.com/gamepingbooster/relay/internal/protocol"
)

// Measurement tickets: a client timing every way into this relay - the relay itself and each entry in
// front of it - for the relay list in the app, without a session. See protocol/measure.go for why a
// session would not do: every one is a slot under -max-clients.
//
// Nothing here touches the session table, the address pool or the stats line's session count, which
// `relay deploy --when-idle` reads to decide the relay is empty. A player with the list open is not a
// player on the relay.

// maxMeasuresPerSecond is how many ticket probes one device is answered per second, across every way in
// together. A list measures a handful of ways a few times a second each.
const maxMeasuresPerSecond = 40

// maxMeasureDevices bounds the rate limiter's table. Only a valid ticket gets a row, so reaching it
// takes that many paying devices measuring inside two seconds; past it new devices go unanswered
// until the janitor clears the quiet rows, and nobody's tunnel is affected.
const maxMeasureDevices = 50_000

type measureWindow struct {
	second int64
	count  int32
}

// measureLimiter is the only state the tickets cost: one fixed one-second window per device.
type measureLimiter struct {
	mu   sync.Mutex
	rows map[[8]byte]*measureWindow
}

func (l *measureLimiter) allow(device [8]byte, now time.Time) bool {
	second := now.Unix()
	l.mu.Lock()
	defer l.mu.Unlock()
	if l.rows == nil {
		l.rows = make(map[[8]byte]*measureWindow)
	}
	w := l.rows[device]
	if w == nil {
		if len(l.rows) >= maxMeasureDevices {
			return false
		}
		w = &measureWindow{second: second}
		l.rows[device] = w
	}
	if w.second != second {
		w.second, w.count = second, 0
	}
	w.count++
	return w.count <= maxMeasuresPerSecond
}

// prune drops the rows of devices that have not measured in the last couple of seconds.
func (l *measureLimiter) prune(now time.Time) {
	cutoff := now.Unix() - 2
	l.mu.Lock()
	defer l.mu.Unlock()
	for k, w := range l.rows {
		if w.second < cutoff {
			delete(l.rows, k)
		}
	}
}

// handleMeasureReq answers a ticket request: verified exactly like a token handshake, refused in
// silence for every reason - a list falls back to ICMP for a relay that does not answer, so there is
// no customer-facing message to give here, and silence is the rule for anything unproven.
func (s *Server) handleMeasureReq(pkt []byte, from netip.AddrPort) {
	now := time.Now()
	tok, _, nonce, err := protocol.VerifyMeasureReq(s.cfg.LicencePub, pkt, now)
	if err != nil {
		s.log.Debug("measurement ticket refused", "from", from.String(), "err", err)
		s.stats.dropped.Add(1)
		return
	}
	if tok.Tier < s.cfg.MinTier || s.isRevoked(tok.DeviceKeyRaw(), tok.Expiry.Unix()) {
		s.log.Debug("measurement ticket refused: tier or revocation", "from", from.String(), "user", tok.UserID)
		s.stats.dropped.Add(1)
		return
	}

	expiry := now.Add(protocol.TicketLifetime)
	if tok.Expiry.Before(expiry) {
		expiry = tok.Expiry
	}
	ticket := protocol.MintTicket(s.ticketKey, protocol.DeviceTag(tok.DeviceKeyRaw()), expiry)
	s.sendTo(protocol.BuildMeasureTicket(nonce, ticket), from)
}

// handleMeasure answers a probe that carries a valid ticket, turning the packet into its reply in place.
// Every other TypeMeasure message - a ticket answer or a reply sent at the relay - is dropped.
func (s *Server) handleMeasure(pkt []byte, from netip.AddrPort) {
	if s.ticketKey == nil {
		s.stats.dropped.Add(1)
		return
	}
	op, _, ticket, err := protocol.ParseMeasure(pkt)
	if err != nil || op != protocol.MeasureOpProbe {
		s.stats.dropped.Add(1)
		return
	}
	now := time.Now()
	device, ok := protocol.CheckTicket(s.ticketKey, ticket, now)
	if !ok {
		s.stats.dropped.Add(1)
		return
	}
	if !s.measures.allow(device, now) {
		s.stats.limited.Add(1)
		return
	}
	protocol.MeasureReplyInto(pkt)
	s.sendTo(pkt, from)
}
