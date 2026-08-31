// Package server holds the relay data plane: one UDP socket talking to clients, one
// TUN device pushing packets into the Linux kernel for NAT and forwarding.
package server

import (
	"crypto/rand"
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
	PSK         []byte       // pre-shared key used to sign handshakes
	IdleTimeout time.Duration
	ConfigureIf bool // true = the relay runs `ip addr/link` for the TUN device itself
	Log         *slog.Logger
}

type session struct {
	id       protocol.SessionID
	innerIP  netip.Addr
	addr     atomic.Pointer[netip.AddrPort] // current client UDP address (changes on roaming)
	lastSeen atomic.Int64                   // unix nanoseconds
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

	relayIP netip.Addr

	stats struct {
		rxPackets, txPackets atomic.Uint64
		rxBytes, txBytes     atomic.Uint64
		dropped              atomic.Uint64
	}
}

// New builds the relay: opens the TUN device, fills the IP pool, opens the UDP socket.
func New(cfg Config) (*Server, error) {
	if len(cfg.PSK) == 0 {
		return nil, errors.New("missing PSK - without one the relay would be an open proxy")
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

	s := &Server{
		cfg:       cfg,
		log:       cfg.Log,
		bySession: make(map[protocol.SessionID]*session),
		byIP:      make(map[netip.Addr]*session),
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
		"pool", len(s.freeIPs))
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

func (s *Server) handleHandshake(pkt []byte, from netip.AddrPort) {
	if err := protocol.VerifyHandshakeReq(s.cfg.PSK, pkt, time.Now()); err != nil {
		// Stay silent: never answer a bad packet, so scanners cannot fingerprint us.
		s.log.Debug("handshake rejected", "from", from.String(), "err", err)
		s.stats.dropped.Add(1)
		return
	}

	sess, ok := s.allocSession(from)
	if !ok {
		resp := protocol.BuildHandshakeResp(s.cfg.PSK, protocol.StatusPoolFull,
			protocol.SessionID{}, netip.Addr{}, netip.Addr{}, 0)
		s.sendTo(resp, from)
		s.log.Warn("address pool exhausted", "from", from.String())
		return
	}

	resp := protocol.BuildHandshakeResp(s.cfg.PSK, protocol.StatusOK,
		sess.id, sess.innerIP, s.relayIP, uint16(s.cfg.MTU))
	s.sendTo(resp, from)
	s.log.Info("client connected", "from", from.String(), "inner_ip", sess.innerIP.String())
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

func (s *Server) handleDisconnect(pkt []byte, _ netip.AddrPort) {
	sid, err := protocol.DecodeSessionID(pkt)
	if err != nil {
		return
	}
	if sess := s.lookup(sid); sess != nil {
		s.releaseSession(sess)
		s.log.Info("client disconnected", "inner_ip", sess.innerIP.String())
	}
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
			cutoff := time.Now().Add(-s.cfg.IdleTimeout).UnixNano()
			var expired []*session
			s.mu.RLock()
			for _, sess := range s.bySession {
				if sess.lastSeen.Load() < cutoff {
					expired = append(expired, sess)
				}
			}
			active := len(s.bySession)
			s.mu.RUnlock()

			for _, sess := range expired {
				s.releaseSession(sess)
				s.log.Info("session expired", "inner_ip", sess.innerIP.String())
			}
			s.log.Info("stats",
				"sessions", active-len(expired),
				"rx_pkt", s.stats.rxPackets.Load(),
				"tx_pkt", s.stats.txPackets.Load(),
				"rx_bytes", s.stats.rxBytes.Load(),
				"tx_bytes", s.stats.txBytes.Load(),
				"dropped", s.stats.dropped.Load())
		}
	}
}

// ------------------------------------------------------------ session table

func (s *Server) allocSession(from netip.AddrPort) (*session, bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if len(s.freeIPs) == 0 {
		return nil, false
	}
	ip := s.freeIPs[len(s.freeIPs)-1]
	s.freeIPs = s.freeIPs[:len(s.freeIPs)-1]

	var sid protocol.SessionID
	if _, err := rand.Read(sid[:]); err != nil {
		s.freeIPs = append(s.freeIPs, ip)
		return nil, false
	}

	sess := &session{id: sid, innerIP: ip}
	f := from
	sess.addr.Store(&f)
	sess.touch()

	s.bySession[sid] = sess
	s.byIP[ip] = sess
	return sess, true
}

func (s *Server) releaseSession(sess *session) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, ok := s.bySession[sess.id]; !ok {
		return
	}
	delete(s.bySession, sess.id)
	delete(s.byIP, sess.innerIP)
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
