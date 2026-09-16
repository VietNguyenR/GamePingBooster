package server

import (
	"net"
	"testing"
	"time"

	"github.com/gamepingbooster/relay/internal/protocol"
)

// A second way into the same session, which is what an entry is: another socket, another source
// address. The Probe must be answered there and must leave the session exactly where it was - a
// Probe that moved the return address would drag the game's traffic onto the path being measured.
func TestProbeIsAnsweredWithoutMovingTheSession(t *testing.T) {
	s, c := newTestRelay(t)
	res := handshake(t, c, clientID(21))
	sess := sessionOf(t, s, res)
	original := *sess.addr.Load()

	// A sentinel rather than a comparison with "before": the wall clock is coarse enough on Windows
	// that a touch() microseconds later can store the same value. See accepted() in dataplane_test.go.
	sess.lastSeen.Store(1)

	other, err := net.DialUDP("udp4", nil, s.conn.LocalAddr().(*net.UDPAddr))
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	defer other.Close()

	if _, err := other.Write(protocol.BuildProbe(res.Session, 0xfeedface)); err != nil {
		t.Fatalf("send probe: %v", err)
	}
	reply := recvPacket(t, other)

	if v, typ := protocol.ParseHeader(reply[0]); v != protocol.Version || typ != protocol.TypeProbeReply {
		t.Fatalf("answered with version %d type %#x, want a ProbeReply", v, typ)
	}
	sid, stamp, err := protocol.DecodePing(reply)
	if err != nil {
		t.Fatalf("decode reply: %v", err)
	}
	if sid != res.Session || stamp != 0xfeedface {
		t.Errorf("reply carries %x/%#x, want %x/%#x", sid, stamp, res.Session, uint64(0xfeedface))
	}

	if now := *sess.addr.Load(); now != original {
		t.Errorf("a probe moved the return address to %s - the game's traffic would follow it", now)
	}
	if sess.lastSeen.Load() != 1 {
		t.Error("a probe counted as activity - a session measured but not played on would never time out")
	}
}

// The rule that a stranger hears nothing holds for probes too.
func TestProbeForAnUnknownSessionIsSilent(t *testing.T) {
	_, c := newTestRelay(t)
	var sid protocol.SessionID
	copy(sid[:], "notasess")
	if _, err := c.Write(protocol.BuildProbe(sid, 1)); err != nil {
		t.Fatalf("send probe: %v", err)
	}
	expectSilence(t, c)
}

func TestProbeOfTheWrongLengthIsSilent(t *testing.T) {
	s, c := newTestRelay(t)
	res := handshake(t, c, clientID(22))
	_ = sessionOf(t, s, res)

	short := protocol.BuildProbe(res.Session, 1)[:protocol.ProbeLen-1]
	if _, err := c.Write(short); err != nil {
		t.Fatalf("send probe: %v", err)
	}
	expectSilence(t, c)
}

// Driven with fixed instants rather than a burst over the socket, which could straddle a second
// boundary and pass with twice the allowance.
func TestProbesArePerSessionRateLimited(t *testing.T) {
	sess := &session{}
	at := time.Unix(1_800_000_000, 0)

	allowed := 0
	for i := 0; i < maxProbesPerSecond+5; i++ {
		if sess.allowProbe(at) {
			allowed++
		}
	}
	if allowed != maxProbesPerSecond {
		t.Errorf("answered %d probes in one second, want %d", allowed, maxProbesPerSecond)
	}
	if !sess.allowProbe(at.Add(time.Second)) {
		t.Error("the next second did not start a fresh allowance")
	}
}
