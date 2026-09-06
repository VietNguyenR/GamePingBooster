// Package server holds the relay data plane: one UDP socket talking to clients, one
// TUN device pushing packets into the Linux kernel for NAT and forwarding.
package server

import (
	"crypto/ecdsa"
	"crypto/rand"
	"crypto/sha256"
	"errors"
	"fmt"
	"log/slog"
	"net"
	"net/netip"
	"os"
	"sync"
	"sync/atomic"
	"time"

	"github.com/gamepingbooster/relay/internal/protocol"
	"github.com/gamepingbooster/relay/internal/tun"
)

// Config holds every runtime parameter of the relay.
type Config struct {
	Listen      string       // UDP listen address, e.g. ":51820"
	TunName     string       // TUN interface name, e.g. "gpb0"
	Subnet      netip.Prefix // inner IP pool handed to clients, e.g. 10.77.0.0/24
	MTU         int          // TUN MTU, must match the MTU the client sets on Wintun
	IdleTimeout time.Duration

	// MaxSessionAge caps how long one handshake stays good for. 0 uses protocol.MaxSessionAge.
	//
	// A session is authenticated once, at handshake, and never re-checked while it runs: cutting
	// a customer off mid-match is the worst possible moment, and a lapsed subscription is refused
	// at their next connect anyway. The cost of that choice is a session that could be held open
	// forever, which this closes. No real game session lasts a day.
	MaxSessionAge time.Duration

	// MaxClients caps how many sessions may be live at once. 0 means no cap beyond the address
	// pool, which is how every relay behaved before this existed.
	//
	// A cap by COUNT rather than by shrinking Subnet, and the difference is not cosmetic. The
	// pool size of a prefix is whatever the arithmetic gives - a /26 is 61 usable addresses, not
	// 50 - so a subnet chosen to mean "fifty people" says something slightly different from what
	// was meant, and says it in a place that also has to match setup-nat.sh and the client's
	// idea of the tunnel netmask. This says the number out loud and touches nothing else.
	//
	// It never refuses a client that already has a session: allocSession returns the existing
	// one before it gets here, so a handshake retry and a reconnect inside the reservation
	// window are unaffected by the cap. Only a genuinely new session counts against it.
	MaxClients int

	// Exactly one authentication mode is configured, and the relay answers only that one.
	//
	// PSK is the self-hosted mode: one shared key, as in v1 and v2.
	//
	// LicencePub and RelayPriv are the commercial mode. The relay holds the licence server's
	// PUBLIC key and nothing else of its, so it can verify a token offline without a database,
	// without a network call, and without holding a secret that would matter if the machine
	// were lost. RelayPriv is the relay's OWN key, used to sign answers - in token mode there
	// is no shared secret, so without it a client cannot tell a real relay from a forged reply.
	PSK         []byte
	LicencePub  *ecdsa.PublicKey
	RelayPriv   *ecdsa.PrivateKey
	ConfigureIf bool // true = the relay runs `ip addr/link` for the TUN device itself

	// Per-session, per-direction cap. 0 disables it. Sized so a game never reaches it: a real
	// PUBG session runs at roughly 10 KB/s, so the default is a hundred times what the thing
	// this relay exists for actually needs.
	RateBytesPerSec int64
	BurstBytes      int64

	Log *slog.Logger
}

type session struct {
	id protocol.SessionID
	// resKey is what the inner-address reservation is keyed on. In PSK mode it is the client
	// id straight off the wire. In token mode it is derived from the DEVICE key instead,
	// because the client id is chosen by the client, and keying on it would let one paying
	// customer claim another one's reserved address just by naming it.
	resKey   protocol.ClientID
	innerIP  netip.Addr
	addr     atomic.Pointer[netip.AddrPort] // current client UDP address (changes on roaming)
	lastSeen atomic.Int64                   // unix nanoseconds
	born     int64                          // unix nanoseconds, set once and never updated
	resumed  bool                           // true if this session reclaimed a previously held address

	up   *bucket // client -> internet, touched only by loopUDP
	down *bucket // internet -> client, touched only by loopTUN
}

func (s *session) touch() { s.lastSeen.Store(time.Now().UnixNano()) }

// Server is a running relay.
type Server struct {
	cfg Config
	log *slog.Logger

	conn *net.UDPConn
	dev  *tun.Device

	mu        sync.RWMutex
	bySession map[protocol.SessionID]*session
	byIP      map[netip.Addr]*session
	freeIPs   []netip.Addr

	// reservedIPs remembers which inner address a reservation key last held. A client that drops and
	// comes back gets the same address, so it does not have to tear down and rebuild its whole
	// routing table over a two-second network blip. Entries survive the session they came from
	// and are only given up when the pool runs dry.
	reservedIPs map[protocol.ClientID]netip.Addr

	relayIP netip.Addr

	stats struct {
		rxPackets, txPackets atomic.Uint64
		rxBytes, txBytes     atomic.Uint64
		dropped              atomic.Uint64
		limited              atomic.Uint64
	}
}

// New builds the relay: opens the TUN device, fills the IP pool, opens the UDP socket.
func New(cfg Config) (*Server, error) {
	psk := len(cfg.PSK) > 0
	licence := cfg.LicencePub != nil
	switch {
	case !psk && !licence:
		return nil, errors.New("no authentication configured - the relay would be an open proxy. " +
			"Pass -psk-file for a self-hosted relay, or -licence-key for a licensed one")
	case psk && licence:
		return nil, errors.New("both a PSK and a licence key were given - a relay serves exactly " +
			"one authentication mode, so pass one or the other")
	case licence && cfg.RelayPriv == nil:
		return nil, errors.New("a licensed relay needs its own key to sign answers with: " +
			"in token mode there is no shared secret, so without it a client cannot tell this " +
			"relay from a forged reply")
	}
	if !cfg.Subnet.Addr().Is4() {
		return nil, errors.New("subnet must be IPv4")
	}
	if cfg.Log == nil {
		cfg.Log = slog.Default()
	}
	if cfg.IdleTimeout <= 0 {
		cfg.IdleTimeout = 90 * time.Second
	}
	if cfg.MaxSessionAge <= 0 {
		cfg.MaxSessionAge = protocol.MaxSessionAge
	}
	if cfg.MaxClients < 0 {
		return nil, fmt.Errorf("max-clients %d is negative", cfg.MaxClients)
	}
	if cfg.RateBytesPerSec > 0 && cfg.BurstBytes <= 0 {
		// Four seconds at the sustained rate. The first draft used eight, and a test written
		// against it let 9.2 MB of a 10 MB flood straight through while still reporting success -
		// the burst was doing all the work and the sustained rate never got a chance to bind.
		cfg.BurstBytes = cfg.RateBytesPerSec * 4
	}

	s := &Server{
		cfg:         cfg,
		log:         cfg.Log,
		bySession:   make(map[protocol.SessionID]*session),
		byIP:        make(map[netip.Addr]*session),
		reservedIPs: make(map[protocol.ClientID]netip.Addr),
	}

	// .1 of the subnet is the relay's own address on the TUN device; the rest is the pool.
	base := cfg.Subnet.Masked().Addr()
	s.relayIP = base.Next()
	for ip := s.relayIP.Next(); cfg.Subnet.Contains(ip); ip = ip.Next() {
		if isBroadcast(ip, cfg.Subnet) {
			break
		}
		s.freeIPs = append(s.freeIPs, ip)
	}
	if len(s.freeIPs) == 0 {
		return nil, fmt.Errorf("subnet %s is too small, no addresses left to hand out", cfg.Subnet)
	}
	if cfg.MaxClients > len(s.freeIPs) {
		// Not fatal: the pool still binds, so the relay behaves correctly. But the operator asked
		// for a number they will never reach, and silently doing something other than what the
		// configuration says is how a limit gets believed for years without ever being tested.
		cfg.Log.Warn("max-clients is larger than the address pool, so the pool is the real limit",
			"max_clients", cfg.MaxClients, "pool", len(s.freeIPs), "subnet", cfg.Subnet.String())
	}

	dev, err := tun.Open(cfg.TunName)
	if err != nil {
		return nil, err
	}
	s.dev = dev

	if cfg.ConfigureIf {
		cidr := fmt.Sprintf("%s/%d", s.relayIP, cfg.Subnet.Bits())
		if err := dev.Configure(cidr, cfg.MTU); err != nil {
			dev.Close()
			return nil, err
		}
	}

	addr, err := net.ResolveUDPAddr("udp4", cfg.Listen)
	if err != nil {
		dev.Close()
		return nil, fmt.Errorf("invalid listen address %q: %w", cfg.Listen, err)
	}
	conn, err := net.ListenUDP("udp4", addr)
	if err != nil {
		dev.Close()
		return nil, fmt.Errorf("listen UDP %s: %w", cfg.Listen, err)
	}
	// Generous buffers so bursts do not drop; the kernel clamps to net.core.rmem_max.
	_ = conn.SetReadBuffer(4 << 20)
	_ = conn.SetWriteBuffer(4 << 20)
	s.conn = conn

	s.log.Info("relay ready",
		"listen", conn.LocalAddr().String(),
		"tun", dev.Name(),
		"subnet", cfg.Subnet.String(),
		"relay_ip", s.relayIP.String(),
		"mtu", cfg.MTU,
		"pool", len(s.freeIPs),
		"max_clients", cfg.MaxClients,
		"rate_kbps", cfg.RateBytesPerSec/1024)
	return s, nil
}

func isBroadcast(ip netip.Addr, p netip.Prefix) bool {
	// For IPv4 the last address of a prefix is the broadcast address - never hand it out.
	last := p.Masked().Addr().As4()
	hostBits := 32 - p.Bits()
	if hostBits >= 32 {
		return false
	}
	v := uint32(last[0])<<24 | uint32(last[1])<<16 | uint32(last[2])<<8 | uint32(last[3])
	v |= (uint32(1) << hostBits) - 1
	bc := netip.AddrFrom4([4]byte{byte(v >> 24), byte(v >> 16), byte(v >> 8), byte(v)})
	return ip == bc
}

// Run drives both main loops until done is closed or a fatal error occurs.
func (s *Server) Run(done <-chan struct{}) error {
	errc := make(chan error, 2)
	go func() { errc <- s.loopUDP() }()
	go func() { errc <- s.loopTUN() }()
	go s.loopJanitor(done)

	select {
	case <-done:
		s.Close()
		return nil
	case err := <-errc:
		s.Close()
		return err
	}
}

// Close shuts down the socket and the TUN device. Losing the TUN device also drops
// every route pointing at it.
func (s *Server) Close() {
	if s.conn != nil {
		s.conn.Close()
	}
	if s.dev != nil {
		s.dev.Close()
	}
}

// loopUDP handles client-to-relay traffic: read from the network, dispatch by type,
// write Data payloads into the TUN device.
func (s *Server) loopUDP() error {
	buf := make([]byte, protocol.MaxPacketLen)
	for {
		n, from, err := s.conn.ReadFromUDPAddrPort(buf)
		if err != nil {
			if errors.Is(err, net.ErrClosed) {
				return nil
			}
			return fmt.Errorf("read UDP: %w", err)
		}
		if n < 1 {
			continue
		}
		s.stats.rxPackets.Add(1)
		s.stats.rxBytes.Add(uint64(n))

		version, msgType := protocol.ParseHeader(buf[0])
		if version != protocol.Version {
			// Answer a mismatched handshake rather than dropping it. Silence here is
			// indistinguishable from a dead relay or a firewalled port, and that ambiguity
			// costs hours the first time a client and a relay drift apart in version.
			//
			// Only in PSK mode. A licensed relay shares no key with an old client, so anything
			// it sent would fail that client's own HMAC check - the reply would be noise, and
			// answering an unauthenticated packet is exactly what the silence rule forbids.
			if msgType == protocol.TypeHandshakeReq && len(s.cfg.PSK) > 0 {
				s.sendTo(protocol.BuildVersionMismatchResp(s.cfg.PSK, version), from)
				s.log.Warn("handshake from a different protocol version",
					"from", from.String(), "client_version", version, "our_version", protocol.Version)
			}
			s.stats.dropped.Add(1)
			continue
		}

		switch msgType {
		case protocol.TypeHandshakeReq:
			s.handleHandshake(buf[:n], from)
		case protocol.TypeData:
			s.handleData(buf[:n], from)
		case protocol.TypePing:
			s.handlePing(buf[:n], from)
		case protocol.TypeDisconnect:
			s.handleDisconnect(buf[:n], from)
		default:
			s.stats.dropped.Add(1)
		}
	}
}

// handleHandshake routes a request to the verifier for the mode this relay serves. A request
// for the OTHER mode is dropped in silence, like any other failed authentication: replying
// "wrong mode" would tell an unauthenticated scanner that a licensed relay lives here.
func (s *Server) handleHandshake(pkt []byte, from netip.AddrPort) {
	mode, err := protocol.HandshakeReqMode(pkt)
	if err != nil {
		s.log.Debug("handshake rejected", "from", from.String(), "err", err)
		s.stats.dropped.Add(1)
		return
	}

	switch {
	case mode == protocol.AuthModePSK && len(s.cfg.PSK) > 0:
		s.handleHandshakePSK(pkt, from)
	case mode == protocol.AuthModeToken && s.cfg.LicencePub != nil:
		s.handleHandshakeToken(pkt, from)
	default:
		s.log.Debug("handshake for an authentication mode this relay does not serve",
			"from", from.String(), "mode", mode)
		s.stats.dropped.Add(1)
	}
}

func (s *Server) handleHandshakePSK(pkt []byte, from netip.AddrPort) {
	clientID, nonce, err := protocol.VerifyHandshakeReq(s.cfg.PSK, pkt, time.Now())
	if err != nil {
		// Stay silent: never answer a bad packet, so scanners cannot fingerprint us.
		s.log.Debug("handshake rejected", "from", from.String(), "err", err)
		s.stats.dropped.Add(1)
		return
	}

	sess, ok := s.allocSession(from, clientID)
	if !ok {
		resp := protocol.BuildHandshakeResp(s.cfg.PSK, protocol.StatusPoolFull,
			protocol.SessionID{}, netip.Addr{}, netip.Addr{}, 0, nonce)
		s.sendTo(resp, from)
		s.log.Warn("address pool exhausted", "from", from.String())
		return
	}

	resp := protocol.BuildHandshakeResp(s.cfg.PSK, protocol.StatusOK,
		sess.id, sess.innerIP, s.relayIP, uint16(s.cfg.MTU), nonce)
	s.sendTo(resp, from)
	s.log.Info("client connected",
		"from", from.String(), "inner_ip", sess.innerIP.String(), "resumed", sess.resumed)
}

func (s *Server) handleHandshakeToken(pkt []byte, from netip.AddrPort) {
	// The client id in the packet is deliberately discarded here. It is signed, so it cannot be
	// altered in transit, but it is still a value the client picked for itself - and in token
	// mode the reservation is keyed on the device instead. See below.
	tok, _, nonce, err := protocol.VerifyHandshakeReqToken(s.cfg.LicencePub, pkt, time.Now())

	// An expired token is the one failure worth answering: the signature verified, so this is a
	// real customer whose subscription lapsed, not a stranger probing the port. Everything else
	// stays silent.
	if err == protocol.ErrTokenExpired {
		s.respondToken(protocol.StatusCredentialExpired, protocol.SessionID{},
			netip.Addr{}, 0, nonce, from)
		s.log.Info("licence expired", "from", from.String(), "user", tok.UserID)
		s.stats.dropped.Add(1)
		return
	}
	if err != nil {
		s.log.Debug("handshake rejected", "from", from.String(), "err", err)
		s.stats.dropped.Add(1)
		return
	}

	// The reservation is keyed on the DEVICE, not on the client id.
	//
	// The client id is chosen by the client. Keying on it would let one device claim another's
	// reserved inner address simply by naming it - harmless when everybody shares a PSK and is
	// on the same side, but not when they are separate paying customers. The device key cannot
	// be borrowed: producing this handshake required its private half.
	var resKey protocol.ClientID
	sum := sha256.Sum256(tok.DeviceKeyRaw())
	copy(resKey[:], sum[:8])

	sess, ok := s.allocSession(from, resKey)
	if !ok {
		s.respondToken(protocol.StatusPoolFull, protocol.SessionID{}, netip.Addr{}, 0, nonce, from)
		s.log.Warn("address pool exhausted", "from", from.String())
		return
	}

	s.respondToken(protocol.StatusOK, sess.id, sess.innerIP, uint16(s.cfg.MTU), nonce, from)
	s.log.Info("client connected", "from", from.String(), "inner_ip", sess.innerIP.String(),
		"resumed", sess.resumed, "user", tok.UserID)
}

func (s *Server) respondToken(status byte, sid protocol.SessionID, clientIP netip.Addr,
	mtu uint16, nonce [8]byte, to netip.AddrPort) {

	resp, err := protocol.BuildHandshakeRespToken(s.cfg.RelayPriv, status, sid,
		clientIP, s.relayIP, mtu, nonce)
	if err != nil {
		// Signing cannot fail for a key that was validated at startup, so this means something
		// is badly wrong rather than that one client had bad luck.
		s.log.Error("could not sign a handshake answer", "err", err)
		return
	}
	s.sendTo(resp, to)
}

func (s *Server) handleData(pkt []byte, from netip.AddrPort) {
	sid, inner, err := protocol.DecodeData(pkt)
	if err != nil {
		s.stats.dropped.Add(1)
		return
	}
	sess := s.lookup(sid)
	if sess == nil {
		s.stats.dropped.Add(1)
		return
	}
	// Anti-spoofing: the inner source address must be the one we assigned to this session.
	src, ok := protocol.SrcIPv4(inner)
	if !ok || src != sess.innerIP {
		s.stats.dropped.Add(1)
		return
	}
	// Stop anyone using the tunnel as a stepping stone into the VPS's private network.
	if dst, ok := protocol.DstIPv4(inner); !ok || s.isForbiddenDst(dst) {
		s.stats.dropped.Add(1)
		return
	}

	sess.touch()
	if cur := sess.addr.Load(); cur == nil || *cur != from {
		f := from
		sess.addr.Store(&f)
		s.log.Info("client roamed", "inner_ip", sess.innerIP.String(), "new_addr", from.String())
	}

	// Checked after authentication, so a forged or spoofed packet cannot spend a real session's
	// allowance, and after touch(), so a limited session is still considered alive rather than
	// being timed out for traffic it was not allowed to send.
	if !sess.up.allow(len(inner), s.cfg.RateBytesPerSec, s.cfg.BurstBytes) {
		if sess.up.shouldWarn() {
			s.log.Warn("session hit the uplink rate limit - packets are being dropped",
				"inner_ip", sess.innerIP.String(), "limit_bytes_per_sec", s.cfg.RateBytesPerSec)
		}
		s.stats.limited.Add(1)
		return
	}

	if _, err := s.dev.Write(inner); err != nil {
		s.log.Warn("TUN write failed", "err", err)
		s.stats.dropped.Add(1)
	}
}

func (s *Server) handlePing(pkt []byte, from netip.AddrPort) {
	sid, stamp, err := protocol.DecodePing(pkt)
	if err != nil {
		return
	}
	sess := s.lookup(sid)
	if sess == nil {
		return
	}
	sess.touch()
	if cur := sess.addr.Load(); cur == nil || *cur != from {
		f := from
		sess.addr.Store(&f)
	}
	s.sendTo(protocol.BuildPong(sid, stamp), from)
}

func (s *Server) handleDisconnect(pkt []byte, from netip.AddrPort) {
	sid, err := protocol.DecodeSessionID(pkt)
	if err != nil {
		return
	}
	sess := s.lookup(sid)
	if sess == nil {
		return
	}
	// Disconnect carries no signature, and v2 authenticates without encrypting - session ids
	// travel in clear. Accept the message only from the address the session is currently using,
	// otherwise anyone who can observe one packet can end the session with a forged nine bytes
	// and take the address reservation down with it.
	if cur := sess.addr.Load(); cur == nil || *cur != from {
		s.stats.dropped.Add(1)
		return
	}
	s.releaseSession(sess, true)
	s.log.Info("client disconnected", "inner_ip", sess.innerIP.String())
}

// loopTUN handles relay-to-client traffic: the kernel hands packets back through the
// TUN device, and the inner destination address tells us which session they belong to.
func (s *Server) loopTUN() error {
	readBuf := make([]byte, protocol.MaxPacketLen)
	sendBuf := make([]byte, protocol.MaxPacketLen)
	for {
		n, err := s.dev.Read(readBuf)
		if err != nil {
			if errors.Is(err, net.ErrClosed) || errors.Is(err, os.ErrClosed) {
				return nil
			}
			return fmt.Errorf("read TUN: %w", err)
		}
		if n < 20 || readBuf[0]>>4 != 4 {
			continue // skip IPv6 and garbage
		}
		if n > protocol.MaxPacketLen-protocol.DataHeaderLen {
			// EncodeData copies into a fixed buffer, so a packet this large would be silently
			// truncated and the client would receive a corrupt one - far worse than losing it.
			// Only reachable if the TUN MTU is raised beyond what MaxPacketLen allows.
			s.log.Warn("dropping an oversized packet from the TUN device", "len", n)
			s.stats.dropped.Add(1)
			continue
		}
		dst, ok := protocol.DstIPv4(readBuf[:n])
		if !ok {
			continue
		}
		s.mu.RLock()
		sess := s.byIP[dst]
		s.mu.RUnlock()
		if sess == nil {
			s.stats.dropped.Add(1)
			continue
		}
		addr := sess.addr.Load()
		if addr == nil {
			continue
		}
		if !sess.down.allow(n, s.cfg.RateBytesPerSec, s.cfg.BurstBytes) {
			if sess.down.shouldWarn() {
				s.log.Warn("session hit the downlink rate limit - packets are being dropped",
					"inner_ip", sess.innerIP.String(), "limit_bytes_per_sec", s.cfg.RateBytesPerSec)
			}
			s.stats.limited.Add(1)
			continue
		}
		out := protocol.EncodeData(sendBuf, sess.id, readBuf[:n])
		if _, err := s.conn.WriteToUDPAddrPort(out, *addr); err != nil {
			s.log.Debug("UDP send failed", "err", err)
			continue
		}
		s.stats.txPackets.Add(1)
		s.stats.txBytes.Add(uint64(len(out)))
	}
}

func (s *Server) loopJanitor(done <-chan struct{}) {
	t := time.NewTicker(30 * time.Second)
	defer t.Stop()
	for {
		select {
		case <-done:
			return
		case <-t.C:
			s.sweep(time.Now())
		}
	}
}

// sweep drops sessions that have gone quiet or have simply been up too long, and logs the
// statistics line.
//
// It takes the time rather than reading the clock so a test can reach a 24-hour session age
// without waiting 24 hours or sleeping. The ticker is the only caller in production.
func (s *Server) sweep(now time.Time) {
	idleCutoff := now.Add(-s.cfg.IdleTimeout).UnixNano()

	// Guarded rather than trusting New() to have filled the default in. A Server built by hand -
	// which the session tests do, because New() wants a TUN device and root - would otherwise
	// have a zero cap, and a zero cap read literally means "born before now", i.e. every session
	// is too old on the first tick. A silently disabled cap is bad; a relay that drops every
	// session thirty seconds after it starts is worse.
	capAge := s.cfg.MaxSessionAge > 0
	bornCutoff := now.Add(-s.cfg.MaxSessionAge).UnixNano()

	var expired, tooOld []*session
	s.mu.RLock()
	for _, sess := range s.bySession {
		switch {
		case sess.lastSeen.Load() < idleCutoff:
			expired = append(expired, sess)
		case capAge && sess.born < bornCutoff:
			tooOld = append(tooOld, sess)
		}
	}
	active := len(s.bySession)
	s.mu.RUnlock()

	for _, sess := range expired {
		s.releaseSession(sess, false)
		s.log.Info("session expired", "inner_ip", sess.innerIP.String())
	}
	// Age is checked separately from idleness because it means something different: the client
	// is alive and well, and is being asked to prove again that it is still entitled to be here.
	// It reconnects, which is a blip; it does not lose service.
	for _, sess := range tooOld {
		s.releaseSession(sess, false)
		s.log.Info("session reached its maximum age, the client must handshake again",
			"inner_ip", sess.innerIP.String(),
			"age", now.Sub(time.Unix(0, sess.born)).Truncate(time.Second).String())
	}
	s.log.Info("stats",
		"sessions", active-len(expired)-len(tooOld),
		"rx_pkt", s.stats.rxPackets.Load(),
		"tx_pkt", s.stats.txPackets.Load(),
		"rx_bytes", s.stats.rxBytes.Load(),
		"tx_bytes", s.stats.txBytes.Load(),
		"dropped", s.stats.dropped.Load(),
		"rate_limited", s.stats.limited.Load())
}

// ------------------------------------------------------------ session table

func (s *Server) allocSession(from netip.AddrPort, resKey protocol.ClientID) (*session, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()

	// A handshake is retried whenever its answer is slow, and the answer carries nothing that
	// says which request it belongs to. So a repeated handshake from a client that already has a
	// live session must return THAT session rather than mint a new one. Minting a new one retires
	// the old, and a client that then adopts the first (merely delayed) answer spends every
	// packet on a session id the relay has already forgotten: lookup fails, the data is dropped,
	// and nothing anywhere logs an error. Answering identically makes the retry harmless
	// whichever answer wins the race, and it also stops a replayed handshake - still valid inside
	// the 120s skew window - from knocking a live client off its session.
	if previous, ok := s.reservedIPs[resKey]; ok {
		if live, inUse := s.byIP[previous]; inUse && live.resKey == resKey {
			f := from
			live.addr.Store(&f)
			live.touch()
			return live, true
		}
	}

	// Past this point a NEW session is being minted, so this is where the cap belongs. Above it
	// is the resume path, which must never be refused: a client whose handshake answer was slow,
	// or which is reconnecting into its reservation, already occupies one of these slots and
	// turning it away would count it twice.
	if s.cfg.MaxClients > 0 && len(s.bySession) >= s.cfg.MaxClients {
		return nil, false
	}

	var sid protocol.SessionID
	if _, err := rand.Read(sid[:]); err != nil {
		return nil, false
	}

	ip, resumed := s.claimAddress(resKey)
	if !ip.IsValid() {
		return nil, false
	}

	sess := &session{
		id: sid, resKey: resKey, innerIP: ip, resumed: resumed,
		born: time.Now().UnixNano(),
		up:   newBucket(s.cfg.BurstBytes),
		down: newBucket(s.cfg.BurstBytes),
	}
	f := from
	sess.addr.Store(&f)
	sess.touch()

	s.bySession[sid] = sess
	s.byIP[ip] = sess
	s.reservedIPs[resKey] = ip
	return sess, true
}

// claimAddress returns the inner address for a reservation key, preferring the one it held before.
// Caller must hold s.mu.
func (s *Server) claimAddress(resKey protocol.ClientID) (netip.Addr, bool) {
	if previous, ok := s.reservedIPs[resKey]; ok {
		if old, inUse := s.byIP[previous]; inUse {
			// A session is still holding the address under a different client id, which should
			// not happen. Retire it rather than hand the same inner IP to two clients at once.
			delete(s.bySession, old.id)
			delete(s.byIP, previous)
			// Drop the loser's reservation as well. Two client ids reserving one address is the
			// one shape that lets evictReservations return it to the pool twice, and a duplicate
			// in the pool means two live clients on the same inner IP, each receiving the other's
			// return traffic. Keep the mapping one-to-one and that can never arise.
			if held, ok := s.reservedIPs[old.resKey]; ok && held == previous && old.resKey != resKey {
				delete(s.reservedIPs, old.resKey)
			}
			return previous, true
		}
		// Reserved and idle. A reserved address is deliberately kept OUT of freeIPs while it is
		// being held, so there is nothing to remove from the pool here - see releaseSession.
		return previous, true
	}

	if len(s.freeIPs) == 0 {
		// Pool exhausted. Reservations are a convenience, not a promise: drop the oldest ones
		// rather than refuse a live client.
		s.evictReservations()
		if len(s.freeIPs) == 0 {
			return netip.Addr{}, false
		}
	}
	ip := s.freeIPs[len(s.freeIPs)-1]
	s.freeIPs = s.freeIPs[:len(s.freeIPs)-1]
	return ip, false
}

// evictReservations releases addresses that are reserved but not in use by any live session,
// returning them to the pool. This is the only way a held address comes back, so the append here
// is not optional: dropping the reservation without it would lose the address permanently.
// Caller must hold s.mu.
func (s *Server) evictReservations() {
	for resKey, ip := range s.reservedIPs {
		if _, inUse := s.byIP[ip]; inUse {
			continue
		}
		delete(s.reservedIPs, resKey)
		s.freeIPs = append(s.freeIPs, ip)
	}
}

// releaseSession returns the address to the pool. dropReservation distinguishes the two ways a
// session can end: an explicit Disconnect means the client is done and its address can go to
// anyone, whereas an idle timeout usually means it vanished mid-game and will be back shortly -
// so the reservation is kept and it can resume on the same address.
func (s *Server) releaseSession(sess *session, dropReservation bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, ok := s.bySession[sess.id]; !ok {
		return
	}
	delete(s.bySession, sess.id)
	delete(s.byIP, sess.innerIP)

	if dropReservation {
		delete(s.reservedIPs, sess.resKey)
		s.freeIPs = append(s.freeIPs, sess.innerIP)
		return
	}

	// Idle timeout: the address stays OUT of the free pool while it is reserved. Putting it back
	// was the bug that made reservations useless - freeIPs is a stack, so the address landed on
	// top and the very next client to connect was handed it, and the client it was being held for
	// came back to a different inner IP and a full routing-table rebuild. The address returns to
	// the pool only through evictReservations, when there is nothing else left to give out.
	if held, ok := s.reservedIPs[sess.resKey]; ok && held == sess.innerIP {
		return
	}
	s.freeIPs = append(s.freeIPs, sess.innerIP)
}

func (s *Server) lookup(sid protocol.SessionID) *session {
	s.mu.RLock()
	defer s.mu.RUnlock()
	return s.bySession[sid]
}

func (s *Server) sendTo(pkt []byte, to netip.AddrPort) {
	if _, err := s.conn.WriteToUDPAddrPort(pkt, to); err != nil {
		s.log.Debug("UDP send failed", "to", to.String(), "err", err)
		return
	}
	s.stats.txPackets.Add(1)
	s.stats.txBytes.Add(uint64(len(pkt)))
}

// isForbiddenDst blocks clients from reaching the VPS's own private network.
func (s *Server) isForbiddenDst(dst netip.Addr) bool {
	if s.cfg.Subnet.Contains(dst) {
		return false // talking to the relay or to other clients in the subnet is fine
	}
	return dst.IsLoopback() ||
		dst.IsPrivate() ||
		dst.IsLinkLocalUnicast() ||
		dst.IsLinkLocalMulticast() ||
		dst.IsMulticast() ||
		dst.IsUnspecified()
}
