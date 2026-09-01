package server

import (
	"net"
	"net/netip"
	"testing"
	"time"

	"github.com/gamepingbooster/relay/internal/protocol"
	"github.com/gamepingbooster/relay/internal/tun"
)

var testPSK = []byte("a-test-psk-of-at-least-16-bytes")

// newTestRelay runs the real loopUDP over a real UDP socket, so these tests exercise the same
// code path a player's packets take - parsing, dispatch, authentication, the session table - and
// not a reimplementation of it.
//
// The TUN device is the stub build: its Write fails without dereferencing the receiver, so
// handleData still runs every check before the write. That is why these tests decide whether a
// packet was ACCEPTED with the `accepted` helper below rather than with the dropped counter,
// which the failing write increments either way.
func newTestRelay(t *testing.T) (*Server, *net.UDPConn) {
	t.Helper()

	srvConn, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatalf("listen: %v", err)
	}
	s := testServer(16)
	s.cfg.PSK = testPSK
	s.conn = srvConn
	s.dev = &tun.Device{}

	go func() { _ = s.loopUDP() }()

	cli, err := net.DialUDP("udp4", nil, srvConn.LocalAddr().(*net.UDPAddr))
	if err != nil {
		srvConn.Close()
		t.Fatalf("dial: %v", err)
	}
	t.Cleanup(func() { cli.Close(); srvConn.Close() })
	return s, cli
}

func recvPacket(t *testing.T, c *net.UDPConn) []byte {
	t.Helper()
	buf := make([]byte, protocol.MaxPacketLen)
	_ = c.SetReadDeadline(time.Now().Add(2 * time.Second))
	n, err := c.Read(buf)
	if err != nil {
		t.Fatalf("no answer from the relay: %v", err)
	}
	return buf[:n]
}

func expectSilence(t *testing.T, c *net.UDPConn) {
	t.Helper()
	buf := make([]byte, protocol.MaxPacketLen)
	_ = c.SetReadDeadline(time.Now().Add(300 * time.Millisecond))
	if n, err := c.Read(buf); err == nil {
		t.Fatalf("expected silence, got %d bytes of type %#x", n, buf[0]&0x0f)
	}
}

// handshake performs a real handshake and returns the parsed result.
func handshake(t *testing.T, c *net.UDPConn, id protocol.ClientID) protocol.HandshakeResult {
	t.Helper()
	req, _, err := protocol.BuildHandshakeReq(testPSK, id, time.Now())
	if err != nil {
		t.Fatalf("build handshake: %v", err)
	}
	if _, err := c.Write(req); err != nil {
		t.Fatalf("send handshake: %v", err)
	}
	res, err := protocol.ParseHandshakeResp(testPSK, recvPacket(t, c))
	if err != nil {
		t.Fatalf("parse handshake response: %v", err)
	}
	if res.Status != protocol.StatusOK {
		t.Fatalf("handshake refused with status %d", res.Status)
	}
	return res
}

// ipv4Packet builds a minimal well-formed IPv4 packet, which is all the relay inspects.
func ipv4Packet(src, dst netip.Addr) []byte {
	pkt := make([]byte, 28)
	pkt[0] = 0x45
	pkt[2] = byte(len(pkt) >> 8)
	pkt[3] = byte(len(pkt))
	pkt[8] = 64 // TTL
	pkt[9] = 17 // UDP
	s4 := src.As4()
	d4 := dst.As4()
	copy(pkt[12:16], s4[:])
	copy(pkt[16:20], d4[:])
	return pkt
}

func sessionOf(t *testing.T, s *Server, res protocol.HandshakeResult) *session {
	t.Helper()
	sess := s.lookup(res.Session)
	if sess == nil {
		t.Fatal("the relay has no session for the id it just handed out")
	}
	return sess
}

// accepted reports whether the relay took a Data packet, by sending it from a throwaway socket
// and watching whether the session's return address follows it. handleData moves that address
// only after every check has passed, so it is a precise signal.
//
// The obvious alternative, watching sess.lastSeen, is NOT usable: it stores time.Now(), and the
// Windows wall clock is coarse enough (tens of milliseconds) that two packets microseconds apart
// can carry identical timestamps. A test written that way reports every packet as dropped - and,
// worse, its negative cases pass for the wrong reason and would never catch a real regression.
func accepted(t *testing.T, s *Server, sess *session, sid protocol.SessionID, inner []byte) bool {
	t.Helper()

	probe, err := net.DialUDP("udp4", nil, s.conn.LocalAddr().(*net.UDPAddr))
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer probe.Close()

	want, err := netip.ParseAddrPort(probe.LocalAddr().String())
	if err != nil {
		t.Fatalf("parse local address: %v", err)
	}
	if cur := sess.addr.Load(); cur != nil && *cur == want {
		t.Fatal("the probe socket reused the address the session already points at")
	}

	buf := make([]byte, protocol.MaxPacketLen)
	if _, err := probe.Write(protocol.EncodeData(buf, sid, inner)); err != nil {
		t.Fatalf("send data: %v", err)
	}
	for i := 0; i < 40; i++ {
		if cur := sess.addr.Load(); cur != nil && *cur == want {
			return true
		}
		time.Sleep(5 * time.Millisecond)
	}
	return false
}

func TestHandshakeAndPingPong(t *testing.T) {
	s, c := newTestRelay(t)
	res := handshake(t, c, clientID(1))

	if !res.ClientIP.IsValid() || !s.cfg.Subnet.Contains(res.ClientIP) {
		t.Errorf("inner IP %s is not inside the pool subnet", res.ClientIP)
	}
	if res.RelayIP != s.relayIP {
		t.Errorf("relay IP is %s, want %s", res.RelayIP, s.relayIP)
	}

	const stamp = 0x0123456789abcdef
	if _, err := c.Write(protocol.BuildPing(res.Session, stamp)); err != nil {
		t.Fatalf("send ping: %v", err)
	}
	pong := recvPacket(t, c)
	v, msgType := protocol.ParseHeader(pong[0])
	if v != protocol.Version || msgType != protocol.TypePong {
		t.Fatalf("expected a Pong, got version %d type %#x", v, msgType)
	}
	sid, echoed, err := protocol.DecodePing(pong)
	if err != nil {
		t.Fatalf("decode pong: %v", err)
	}
	if sid != res.Session {
		t.Error("the Pong carries a different session id")
	}
	// The RTT the player sees is computed from this echo. Corrupt it and every latency number
	// in the UI, including the one that picks which relay to use, becomes fiction.
	if echoed != stamp {
		t.Errorf("the Pong echoed %#x, want %#x", echoed, stamp)
	}
}

// A handshake signed with the wrong key must produce no answer at all. Answering would let
// anyone scanning the internet fingerprint this port as a relay.
func TestHandshakeWithWrongPskIsIgnored(t *testing.T) {
	_, c := newTestRelay(t)
	req, _, err := protocol.BuildHandshakeReq([]byte("the-wrong-pre-shared-key-value"), clientID(2), time.Now())
	if err != nil {
		t.Fatalf("build: %v", err)
	}
	if _, err := c.Write(req); err != nil {
		t.Fatalf("send: %v", err)
	}
	expectSilence(t, c)
}

// A clock far out of range is refused. Without this a captured handshake could be replayed
// forever.
func TestHandshakeWithSkewedClockIsIgnored(t *testing.T) {
	_, c := newTestRelay(t)
	req, _, err := protocol.BuildHandshakeReq(testPSK, clientID(3), time.Now().Add(-10*time.Minute))
	if err != nil {
		t.Fatalf("build: %v", err)
	}
	if _, err := c.Write(req); err != nil {
		t.Fatalf("send: %v", err)
	}
	expectSilence(t, c)
}

// The relay must answer a handshake from another protocol version instead of ignoring it.
// Silence there is indistinguishable from a dead relay or a blocked port.
func TestVersionMismatchIsAnswered(t *testing.T) {
	_, c := newTestRelay(t)

	req, _, err := protocol.BuildHandshakeReq(testPSK, clientID(4), time.Now())
	if err != nil {
		t.Fatalf("build: %v", err)
	}
	req[0] = (protocol.Version+1)<<4 | protocol.TypeHandshakeReq

	if _, err := c.Write(req); err != nil {
		t.Fatalf("send: %v", err)
	}
	resp := recvPacket(t, c)
	if len(resp) != protocol.HandshakeRespLen {
		t.Fatalf("answer is %d bytes, want %d", len(resp), protocol.HandshakeRespLen)
	}
	// The answer must carry the CLIENT's version so that client can parse it.
	if v, _ := protocol.ParseHeader(resp[0]); v != protocol.Version+1 {
		t.Errorf("answer carries version %d, want the client's %d", v, protocol.Version+1)
	}
	if resp[1] != protocol.StatusVersionMismatch {
		t.Errorf("status is %d, want StatusVersionMismatch", resp[1])
	}
}

// The inner source address is the only thing tying a Data packet to its session. A client that
// forges another address must be refused, or it could send traffic that the relay NATs and
// answers to somebody else's session.
func TestDataWithForgedInnerSourceIsDropped(t *testing.T) {
	s, c := newTestRelay(t)
	res := handshake(t, c, clientID(5))
	sess := sessionOf(t, s, res)

	inner := ipv4Packet(netip.MustParseAddr("10.77.0.99"), netip.MustParseAddr("1.1.1.1"))
	if accepted(t, s, sess, res.Session, inner) {
		t.Error("a packet with a forged inner source address was accepted")
	}
}

// The tunnel must not become a way into the VPS's own private network.
func TestDataToPrivateDestinationIsDropped(t *testing.T) {
	s, c := newTestRelay(t)
	res := handshake(t, c, clientID(6))
	sess := sessionOf(t, s, res)

	for _, dst := range []string{"192.168.1.1", "10.0.0.1", "127.0.0.1", "169.254.1.1", "224.0.0.1"} {
		inner := ipv4Packet(res.ClientIP, netip.MustParseAddr(dst))
		if accepted(t, s, sess, res.Session, inner) {
			t.Errorf("a packet addressed to %s was accepted", dst)
		}
	}
}

// A well-formed packet from the right session to a public address must be accepted.
func TestValidDataIsAccepted(t *testing.T) {
	s, c := newTestRelay(t)
	res := handshake(t, c, clientID(7))
	sess := sessionOf(t, s, res)

	inner := ipv4Packet(res.ClientIP, netip.MustParseAddr("1.1.1.1"))
	if !accepted(t, s, sess, res.Session, inner) {
		t.Error("a valid data packet was not accepted")
	}
}

// Data naming a session the relay does not have must be dropped without disturbing anything.
func TestDataForUnknownSessionIsDropped(t *testing.T) {
	s, c := newTestRelay(t)
	res := handshake(t, c, clientID(8))
	sess := sessionOf(t, s, res)

	inner := ipv4Packet(res.ClientIP, netip.MustParseAddr("1.1.1.1"))
	if accepted(t, s, sess, protocol.SessionID{9, 9, 9, 9, 9, 9, 9, 9}, inner) {
		t.Error("data for an unknown session touched a live session")
	}
}

// Home routers rewrite the client's public port without warning. The relay has to follow the
// client to its new address or the return path dies silently and the player just sees lag.
func TestClientRoamingUpdatesTheReturnAddress(t *testing.T) {
	s, c := newTestRelay(t)
	res := handshake(t, c, clientID(9))
	sess := sessionOf(t, s, res)

	original := *sess.addr.Load()

	roamed, err := net.DialUDP("udp4", nil, s.conn.LocalAddr().(*net.UDPAddr))
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer roamed.Close()

	if _, err := roamed.Write(protocol.BuildPing(res.Session, 1)); err != nil {
		t.Fatalf("send: %v", err)
	}
	recvPacket(t, roamed) // the Pong proves the relay processed it
	now := *sess.addr.Load()
	if now == original {
		t.Error("the return address was not moved to where the client is now")
	}
	if now.Port() != uint16(roamed.LocalAddr().(*net.UDPAddr).Port) {
		t.Errorf("return address is %s, want the roamed port %d", now, roamed.LocalAddr().(*net.UDPAddr).Port)
	}
}

// Disconnect carries no signature and session ids travel in clear, so one forged nine-byte
// packet from anywhere would otherwise end a player's session and drop the address reservation
// that lets them come back on the same inner IP.
func TestDisconnectFromAnotherAddressIsIgnored(t *testing.T) {
	s, c := newTestRelay(t)
	res := handshake(t, c, clientID(10))

	attacker, err := net.DialUDP("udp4", nil, s.conn.LocalAddr().(*net.UDPAddr))
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer attacker.Close()

	if _, err := attacker.Write(protocol.BuildDisconnect(res.Session)); err != nil {
		t.Fatalf("send: %v", err)
	}
	// Give the relay a chance to act on it before concluding that it did not.
	time.Sleep(100 * time.Millisecond)
	if s.lookup(res.Session) == nil {
		t.Fatal("a forged Disconnect from an unrelated address ended the session")
	}

	// The real client can still end its own session.
	if _, err := c.Write(protocol.BuildDisconnect(res.Session)); err != nil {
		t.Fatalf("send: %v", err)
	}
	for i := 0; i < 100 && s.lookup(res.Session) != nil; i++ {
		time.Sleep(5 * time.Millisecond)
	}
	if s.lookup(res.Session) != nil {
		t.Error("the client's own Disconnect did not end the session")
	}
}

// The client retries a handshake whenever the answer is slow, and cannot tell which answer
// belongs to which request. Both answers must therefore name the same session.
func TestRepeatedHandshakeReturnsTheSameSession(t *testing.T) {
	_, c := newTestRelay(t)
	id := clientID(11)

	first := handshake(t, c, id)
	second := handshake(t, c, id)

	if first.Session != second.Session {
		t.Errorf("two handshakes produced different sessions (%x then %x): a client adopting the "+
			"slower answer would be sending into a session the relay has forgotten",
			first.Session, second.Session)
	}
	if first.ClientIP != second.ClientIP {
		t.Errorf("two handshakes produced different inner IPs (%s then %s)", first.ClientIP, second.ClientIP)
	}
}

// Garbage on the listening port must never take the relay down. It is a public UDP port; it will
// be scanned.
func TestMalformedPacketsDoNotKillTheRelay(t *testing.T) {
	s, c := newTestRelay(t)

	junk := [][]byte{
		{},
		{0x00},
		{0x21},                   // Data header, no session id, no payload
		{0x23, 1, 2, 3},          // truncated Data
		{0x24, 1, 2, 3, 4, 5},    // truncated Ping
		{0x26, 1},                // truncated Disconnect
		{0x21, 0, 0, 0, 0, 0, 0}, // truncated handshake
		make([]byte, 1500),       // all zeroes, full size
	}
	for _, p := range junk {
		if len(p) == 0 {
			continue // a zero-length UDP write is not something a client can send meaningfully
		}
		if _, err := c.Write(p); err != nil {
			t.Fatalf("send: %v", err)
		}
	}

	// The relay must still be serving after all of that.
	res := handshake(t, c, clientID(12))
	if s.lookup(res.Session) == nil {
		t.Error("the relay stopped working after receiving malformed packets")
	}
}
