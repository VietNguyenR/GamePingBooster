package server

import (
	"crypto/ecdsa"
	"encoding/binary"
	"encoding/hex"
	"net"
	"testing"
	"time"

	"github.com/gamepingbooster/relay/internal/protocol"
)

// Measurement tickets over the real UDP loop. The property everything else rests on is the first one:
// a ticket costs the relay no session, so a full relay still answers, and a list open on a thousand
// PCs takes no slot from a player trying to connect.

// askTicket sends a ticket request for a fresh device and returns the ticket, or fails the test.
func askTicket(t *testing.T, cli *net.UDPConn, licence *ecdsa.PrivateKey, life time.Duration) []byte {
	t.Helper()
	device, err := protocol.GenerateKey()
	if err != nil {
		t.Fatal(err)
	}
	req, nonce, err := protocol.BuildMeasureReq(device, mintFor(t, licence, device, life), clientID(9), time.Now())
	if err != nil {
		t.Fatalf("build: %v", err)
	}
	if _, err := cli.Write(req); err != nil {
		t.Fatalf("send: %v", err)
	}
	op, echo, ticket, err := protocol.ParseMeasure(recvPacket(t, cli))
	if err != nil {
		t.Fatalf("parse answer: %v", err)
	}
	if op != protocol.MeasureOpTicket {
		t.Fatalf("answered with op %d, want a ticket", op)
	}
	if echo != binary.BigEndian.Uint64(nonce[:]) {
		t.Fatal("the ticket answer does not echo the request's nonce")
	}
	return append([]byte(nil), ticket...)
}

func TestATicketCostsNoSessionAndWorksOnAFullRelay(t *testing.T) {
	s, cli, licence, _ := licensedRelay(t)

	// Full: one slot, taken by a player.
	s.cfg.MaxClients = 1
	player, err := protocol.GenerateKey()
	if err != nil {
		t.Fatal(err)
	}
	req, nonce, err := protocol.BuildHandshakeReqToken(player, mintFor(t, licence, player, time.Hour), clientID(1), time.Now())
	if err != nil {
		t.Fatal(err)
	}
	if _, err := cli.Write(req); err != nil {
		t.Fatal(err)
	}
	if res, err := protocol.ParseHandshakeRespToken(&s.cfg.RelayPriv.PublicKey, recvPacket(t, cli), nonce); err != nil || res.Status != protocol.StatusOK {
		t.Fatalf("the player was not admitted: %v %+v", err, res)
	}

	ticket := askTicket(t, cli, licence, time.Hour)

	// Probed from another socket, which is what an entry is: another source address.
	other, err := net.DialUDP("udp4", nil, s.conn.LocalAddr().(*net.UDPAddr))
	if err != nil {
		t.Fatal(err)
	}
	defer other.Close()
	if _, err := other.Write(protocol.BuildMeasureProbe(ticket, 0xabcdef)); err != nil {
		t.Fatal(err)
	}
	reply := recvPacket(t, other)
	if len(reply) != protocol.MeasureLen {
		t.Fatalf("reply is %d bytes, want %d - a reply must not be larger than its probe", len(reply), protocol.MeasureLen)
	}
	op, stamp, _, err := protocol.ParseMeasure(reply)
	if err != nil || op != protocol.MeasureOpReply || stamp != 0xabcdef {
		t.Fatalf("reply op %d stamp %#x err %v, want a reply echoing %#x", op, stamp, err, 0xabcdef)
	}

	s.mu.RLock()
	sessions := len(s.bySession)
	s.mu.RUnlock()
	if sessions != 1 {
		t.Fatalf("%d sessions after measuring, want only the player's 1 - a ticket took a slot", sessions)
	}
}

func TestATamperedTicketIsSilent(t *testing.T) {
	_, cli, licence, _ := licensedRelay(t)
	ticket := askTicket(t, cli, licence, time.Hour)

	// Moving the expiry is the forgery worth trying.
	ticket[7] ^= 0x01
	if _, err := cli.Write(protocol.BuildMeasureProbe(ticket, 1)); err != nil {
		t.Fatal(err)
	}
	expectSilence(t, cli)
}

func TestATicketFromAnotherRelayIsSilent(t *testing.T) {
	_, cli, _, _ := licensedRelay(t)
	otherRelay, err := protocol.GenerateKey()
	if err != nil {
		t.Fatal(err)
	}
	foreign := protocol.MintTicket(protocol.TicketKey(otherRelay), [8]byte{1}, time.Now().Add(time.Hour))
	if _, err := cli.Write(protocol.BuildMeasureProbe(foreign[:], 1)); err != nil {
		t.Fatal(err)
	}
	expectSilence(t, cli)
}

func TestAnExpiredTicketIsSilent(t *testing.T) {
	s, cli, _, _ := licensedRelay(t)
	old := protocol.MintTicket(s.ticketKey, [8]byte{2}, time.Now().Add(-time.Second))
	if _, err := cli.Write(protocol.BuildMeasureProbe(old[:], 1)); err != nil {
		t.Fatal(err)
	}
	expectSilence(t, cli)
}

// A ticket never outlives the licence it was issued on.
func TestATicketExpiresWithItsToken(t *testing.T) {
	_, cli, licence, _ := licensedRelay(t)
	ticket := askTicket(t, cli, licence, time.Minute)
	expiry := time.Unix(int64(binary.BigEndian.Uint64(ticket[:8])), 0)
	if expiry.After(time.Now().Add(time.Minute + time.Second)) {
		t.Fatalf("ticket good until %v, past its token's expiry a minute from now", expiry)
	}
}

func TestAnExpiredTokenGetsNoTicket(t *testing.T) {
	_, cli, licence, _ := licensedRelay(t)
	device, _ := protocol.GenerateKey()
	req, _, err := protocol.BuildMeasureReq(device, mintFor(t, licence, device, -time.Minute), clientID(3), time.Now())
	if err != nil {
		t.Fatal(err)
	}
	if _, err := cli.Write(req); err != nil {
		t.Fatal(err)
	}
	expectSilence(t, cli)
}

func TestATierBelowTheRelayMinimumGetsNoTicket(t *testing.T) {
	s, cli, licence, _ := licensedRelay(t)
	s.cfg.MinTier = 2
	device, _ := protocol.GenerateKey()
	req, _, err := protocol.BuildMeasureReq(device, mintTierFor(t, licence, device, 1), clientID(4), time.Now())
	if err != nil {
		t.Fatal(err)
	}
	if _, err := cli.Write(req); err != nil {
		t.Fatal(err)
	}
	expectSilence(t, cli)
}

// A machine signed out by a sign-in elsewhere stops measuring as well as connecting.
func TestARevokedTokenGetsNoTicket(t *testing.T) {
	s, cli, licence, _ := licensedRelay(t)
	device, _ := protocol.GenerateKey()
	token := mintFor(t, licence, device, time.Hour)
	tok, err := protocol.VerifyToken(&licence.PublicKey, token, time.Now())
	if err != nil {
		t.Fatal(err)
	}
	s.applyRevoked([]revokedEntry{{Key: hex.EncodeToString(tok.DeviceKeyRaw()), Exp: tok.Expiry.Unix()}}, time.Now())

	req, _, err := protocol.BuildMeasureReq(device, token, clientID(8), time.Now())
	if err != nil {
		t.Fatal(err)
	}
	if _, err := cli.Write(req); err != nil {
		t.Fatal(err)
	}
	expectSilence(t, cli)
}

// The mode byte is signed: a ticket request relabelled as a handshake must not open a session.
func TestATicketRequestCannotBeReplayedAsAHandshake(t *testing.T) {
	s, cli, licence, _ := licensedRelay(t)
	device, _ := protocol.GenerateKey()
	req, _, err := protocol.BuildMeasureReq(device, mintFor(t, licence, device, time.Hour), clientID(5), time.Now())
	if err != nil {
		t.Fatal(err)
	}
	req[1] = protocol.AuthModeToken
	if _, err := cli.Write(req); err != nil {
		t.Fatal(err)
	}
	expectSilence(t, cli)
	s.mu.RLock()
	defer s.mu.RUnlock()
	if len(s.bySession) != 0 {
		t.Fatal("a relabelled ticket request opened a session")
	}
}

func TestAPskRelayIssuesNoTicket(t *testing.T) {
	_, cli := newTestRelay(t)
	licence, _ := protocol.GenerateKey()
	device, _ := protocol.GenerateKey()
	req, _, err := protocol.BuildMeasureReq(device, mintFor(t, licence, device, time.Hour), clientID(6), time.Now())
	if err != nil {
		t.Fatal(err)
	}
	if _, err := cli.Write(req); err != nil {
		t.Fatal(err)
	}
	expectSilence(t, cli)
}

// Fixed instants, for the reason TestProbesArePerSessionRateLimited gives.
func TestTicketProbesAreRateLimitedPerDevice(t *testing.T) {
	var l measureLimiter
	at := time.Unix(1_800_000_000, 0)
	a, b := [8]byte{1}, [8]byte{2}

	allowed := 0
	for i := 0; i < maxMeasuresPerSecond+5; i++ {
		if l.allow(a, at) {
			allowed++
		}
	}
	if allowed != maxMeasuresPerSecond {
		t.Errorf("answered %d probes in one second, want %d", allowed, maxMeasuresPerSecond)
	}
	if !l.allow(b, at) {
		t.Error("one device's flood used up another device's allowance")
	}
	if !l.allow(a, at.Add(time.Second)) {
		t.Error("the next second did not start a fresh allowance")
	}

	l.prune(at.Add(10 * time.Second))
	if len(l.rows) != 0 {
		t.Errorf("%d rows left after every device went quiet", len(l.rows))
	}
}

// Derived, not drawn: a relayd restarted on the same key file - hot or cold - accepts the tickets the
// last one issued, so an open list does not go blank on a deploy.
func TestTheTicketKeySurvivesARestart(t *testing.T) {
	relayKey, _ := protocol.GenerateKey()
	ticket := protocol.MintTicket(protocol.TicketKey(relayKey), [8]byte{7}, time.Now().Add(time.Minute))
	if _, ok := protocol.CheckTicket(protocol.TicketKey(relayKey), ticket[:], time.Now()); !ok {
		t.Fatal("a ticket did not verify under the key derived again from the same relay key")
	}
}
