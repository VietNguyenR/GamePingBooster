package server

// The licensed data plane, over a real UDP socket.
//
// Everything about token mode was unit-tested and had never carried a packet: BuildToken and
// VerifyToken were exercised directly, and relayd's -licence-key path had no test at all. These
// run the same loopUDP a player's packets go through, so a mistake in dispatch, in mode
// selection or in the answer's signature shows up here rather than on a VPS.
//
// The two properties worth more than the happy path are at the bottom: a token signed by the
// wrong licence key must be refused, and a valid token presented by a machine that does not hold
// the matching device key must be refused. The second is the entire reason the token names a
// device at all - without it a leaked token would be a licence anyone could use.

import (
	"crypto/ecdsa"
	"net"
	"testing"
	"time"

	"github.com/gamepingbooster/relay/internal/protocol"
	"github.com/gamepingbooster/relay/internal/tun"
)

// licensedRelay runs the real UDP loop with a licence public key instead of a PSK.
func licensedRelay(t *testing.T) (*Server, *net.UDPConn, *ecdsa.PrivateKey, *ecdsa.PublicKey) {
	t.Helper()

	licence, err := protocol.GenerateKey()
	if err != nil {
		t.Fatalf("licence key: %v", err)
	}
	relayKey, err := protocol.GenerateKey()
	if err != nil {
		t.Fatalf("relay key: %v", err)
	}

	srvConn, err := net.ListenUDP("udp4", &net.UDPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatalf("listen: %v", err)
	}
	s := testServer(16)
	// No PSK on purpose: a relay serves one mode, and leaving both set would let a test pass
	// through the wrong path without anybody noticing.
	s.cfg.LicencePub = &licence.PublicKey
	s.cfg.RelayPriv = relayKey
	s.conn = srvConn
	s.dev = &tun.Device{}

	go func() { _ = s.loopUDP() }()

	cli, err := net.DialUDP("udp4", nil, srvConn.LocalAddr().(*net.UDPAddr))
	if err != nil {
		srvConn.Close()
		t.Fatalf("dial: %v", err)
	}
	t.Cleanup(func() { cli.Close(); srvConn.Close() })

	return s, cli, licence, &relayKey.PublicKey
}

// mintFor signs a token for a device, with the given licence key and lifetime.
func mintFor(t *testing.T, licence *ecdsa.PrivateKey, device *ecdsa.PrivateKey, life time.Duration) []byte {
	t.Helper()
	tok, err := protocol.BuildToken(licence, 42,
		protocol.MarshalPublicKey(&device.PublicKey), time.Now().Add(life), 1, 0)
	if err != nil {
		t.Fatalf("mint: %v", err)
	}
	return tok
}

func TestTokenHandshakeIsAccepted(t *testing.T) {
	s, cli, licence, relayPub := licensedRelay(t)

	device, err := protocol.GenerateKey()
	if err != nil {
		t.Fatal(err)
	}
	token := mintFor(t, licence, device, time.Hour)

	req, nonce, err := protocol.BuildHandshakeReqToken(device, token, clientID(7), time.Now())
	if err != nil {
		t.Fatalf("build: %v", err)
	}
	if _, err := cli.Write(req); err != nil {
		t.Fatalf("send: %v", err)
	}

	// Parsed against the RELAY's public key, not a shared secret. In token mode that signature
	// is the only thing separating a real relay's answer from anything else that arrives.
	res, err := protocol.ParseHandshakeRespToken(relayPub, recvPacket(t, cli), nonce)
	if err != nil {
		t.Fatalf("parse answer: %v", err)
	}
	if res.Status != protocol.StatusOK {
		t.Fatalf("refused with status %d", res.Status)
	}
	if sess := s.lookup(res.Session); sess == nil {
		t.Fatal("the relay answered OK but has no session for the id it handed out")
	}
	// IsValid() is NOT the check here. The wire always carries four bytes, so a refused
	// handshake parses as 0.0.0.0 - which is a perfectly valid netip.Addr and would make this
	// assertion pass on a relay that admitted nobody. Ask whether the address is one this relay
	// actually hands out instead.
	if res.ClientIP.IsUnspecified() {
		t.Fatal("no inner address was handed out")
	}
	if !s.cfg.Subnet.Contains(res.ClientIP) {
		t.Fatalf("inner address %v is outside the pool %v", res.ClientIP, s.cfg.Subnet)
	}
}

func TestTokenHandshakeSignedByAnotherLicenceKeyIsIgnored(t *testing.T) {
	// The property the entire commercial design rests on. If this ever passes, anybody who can
	// generate a P-256 key can mint themselves unlimited access to every relay.
	_, cli, _, _ := licensedRelay(t)

	forger, err := protocol.GenerateKey()
	if err != nil {
		t.Fatal(err)
	}
	device, err := protocol.GenerateKey()
	if err != nil {
		t.Fatal(err)
	}
	token := mintFor(t, forger, device, time.Hour)

	req, _, err := protocol.BuildHandshakeReqToken(device, token, clientID(8), time.Now())
	if err != nil {
		t.Fatal(err)
	}
	if _, err := cli.Write(req); err != nil {
		t.Fatal(err)
	}
	expectSilence(t, cli)
}

func TestAStolenTokenIsUselessWithoutTheDeviceKey(t *testing.T) {
	// A genuine token, minted by the real licence key, for somebody else's machine - which is
	// exactly what an attacker gets by reading a token out of a file or off the wire. It must be
	// worthless, because the request is signed with the device key the token names and that key
	// never leaves the machine it was generated on.
	_, cli, licence, _ := licensedRelay(t)

	victim, err := protocol.GenerateKey()
	if err != nil {
		t.Fatal(err)
	}
	thief, err := protocol.GenerateKey()
	if err != nil {
		t.Fatal(err)
	}
	token := mintFor(t, licence, victim, time.Hour)

	// Same token, signed by the thief's key.
	req, _, err := protocol.BuildHandshakeReqToken(thief, token, clientID(9), time.Now())
	if err != nil {
		t.Fatal(err)
	}
	if _, err := cli.Write(req); err != nil {
		t.Fatal(err)
	}
	expectSilence(t, cli)
}

func TestExpiredTokenIsAnsweredAndNotIgnored(t *testing.T) {
	// The one rejection that gets a reply, and it is deliberate - see the comment on
	// ErrTokenExpired in handleHandshakeToken. The signature verified, so this is a real
	// customer whose subscription lapsed rather than a stranger probing the port, and telling
	// them so is the difference between "sign in again to renew" and a two-second timeout that
	// blames the network.
	//
	// This test was originally written the other way round, asserting silence, and the relay
	// proved it wrong. The behaviour is right; the assumption was not.
	s, cli, licence, relayPub := licensedRelay(t)

	device, err := protocol.GenerateKey()
	if err != nil {
		t.Fatal(err)
	}
	token := mintFor(t, licence, device, -time.Minute)

	req, nonce, err := protocol.BuildHandshakeReqToken(device, token, clientID(10), time.Now())
	if err != nil {
		t.Fatal(err)
	}
	if _, err := cli.Write(req); err != nil {
		t.Fatal(err)
	}

	res, err := protocol.ParseHandshakeRespToken(relayPub, recvPacket(t, cli), nonce)
	if err != nil {
		t.Fatalf("parse answer: %v", err)
	}
	if res.Status != protocol.StatusCredentialExpired {
		t.Fatalf("status is %d, want StatusCredentialExpired (%d)", res.Status, protocol.StatusCredentialExpired)
	}

	// Answered, but emphatically not admitted.
	if res.Session != (protocol.SessionID{}) {
		t.Fatalf("an expired token was given session %x", res.Session)
	}
	if sess := s.lookup(res.Session); sess != nil {
		t.Fatal("an expired token created a session")
	}
	// Not IsValid(): the four zero bytes on the wire parse into 0.0.0.0, which IS a valid
	// netip.Addr. The question is whether an address was actually allocated.
	if !res.ClientIP.IsUnspecified() {
		t.Fatalf("an expired token was handed the inner address %v", res.ClientIP)
	}
}

func TestALicensedRelayDoesNotAnswerAPskHandshake(t *testing.T) {
	// A relay serves one mode. A PSK client pointed at a licensed relay has to fail in a way the
	// client can explain, and silence is what its timeout message already talks about.
	_, cli, _, _ := licensedRelay(t)

	req, _, err := protocol.BuildHandshakeReq(testPSK, clientID(11), time.Now())
	if err != nil {
		t.Fatal(err)
	}
	if _, err := cli.Write(req); err != nil {
		t.Fatal(err)
	}
	expectSilence(t, cli)
}

func TestTokenHandshakeIsIdempotentPerDevice(t *testing.T) {
	// The same retry race that was fixed for PSK mode. A repeated handshake must return the
	// session that already exists rather than minting a second one and retiring the first, which
	// left a client sending into a session the relay had forgotten.
	s, cli, licence, relayPub := licensedRelay(t)

	device, err := protocol.GenerateKey()
	if err != nil {
		t.Fatal(err)
	}
	token := mintFor(t, licence, device, time.Hour)
	id := clientID(12)

	first, nonce1, err := protocol.BuildHandshakeReqToken(device, token, id, time.Now())
	if err != nil {
		t.Fatal(err)
	}
	if _, err := cli.Write(first); err != nil {
		t.Fatal(err)
	}
	res1, err := protocol.ParseHandshakeRespToken(relayPub, recvPacket(t, cli), nonce1)
	if err != nil {
		t.Fatalf("first answer: %v", err)
	}

	second, nonce2, err := protocol.BuildHandshakeReqToken(device, token, id, time.Now())
	if err != nil {
		t.Fatal(err)
	}
	if _, err := cli.Write(second); err != nil {
		t.Fatal(err)
	}
	res2, err := protocol.ParseHandshakeRespToken(relayPub, recvPacket(t, cli), nonce2)
	if err != nil {
		t.Fatalf("second answer: %v", err)
	}

	if res1.Session != res2.Session {
		t.Fatalf("a repeated handshake minted a new session: %x then %x", res1.Session, res2.Session)
	}
	if res1.ClientIP != res2.ClientIP {
		t.Fatalf("the inner address changed on a retry: %v then %v", res1.ClientIP, res2.ClientIP)
	}
	if s.lookup(res1.Session) == nil {
		t.Fatal("the session the relay pointed at twice does not exist")
	}
}

// mintTierFor is mintFor with the plan tier spelled out, for the -min-tier gate below.
func mintTierFor(t *testing.T, licence *ecdsa.PrivateKey, device *ecdsa.PrivateKey, tier byte) []byte {
	t.Helper()
	tok, err := protocol.BuildToken(licence, 42,
		protocol.MarshalPublicKey(&device.PublicKey), time.Now().Add(time.Hour), tier, 0)
	if err != nil {
		t.Fatalf("mint: %v", err)
	}
	return tok
}

// tierHandshake runs one token handshake against a relay set to minTier and returns the answer.
func tierHandshake(t *testing.T, minTier, tier byte) (*Server, protocol.HandshakeResult) {
	t.Helper()
	s, cli, licence, relayPub := licensedRelay(t)
	s.cfg.MinTier = minTier

	device, err := protocol.GenerateKey()
	if err != nil {
		t.Fatal(err)
	}
	req, nonce, err := protocol.BuildHandshakeReqToken(device, mintTierFor(t, licence, device, tier),
		clientID(11), time.Now())
	if err != nil {
		t.Fatal(err)
	}
	if _, err := cli.Write(req); err != nil {
		t.Fatal(err)
	}

	res, err := protocol.ParseHandshakeRespToken(relayPub, recvPacket(t, cli), nonce)
	if err != nil {
		t.Fatalf("parse answer: %v", err)
	}
	return s, res
}

func TestATierBelowTheRelayMinimumIsRefused(t *testing.T) {
	// The gate that turns "this relay was not listed in your profile" into an actual refusal.
	// Before it, a premium relay was protected only by nobody having told the client its address,
	// and an address is not a credential: a valid token from any plan opened it.
	s, res := tierHandshake(t, 2, 1)

	if res.Status != protocol.StatusTierTooLow {
		t.Fatalf("status is %d, want StatusTierTooLow (%d)", res.Status, protocol.StatusTierTooLow)
	}

	// Answered - the licence is real - but not admitted anywhere.
	if res.Session != (protocol.SessionID{}) {
		t.Fatalf("an under-tier token was given session %x", res.Session)
	}
	if sess := s.lookup(res.Session); sess != nil {
		t.Fatal("an under-tier token created a session")
	}
	if !res.ClientIP.IsUnspecified() {
		t.Fatalf("an under-tier token was handed the inner address %v", res.ClientIP)
	}
}

func TestATierAtTheRelayMinimumIsAdmitted(t *testing.T) {
	// Equal passes. The comparison is `tier < minTier`, so a plan that exactly meets the bar is
	// in - an off-by-one here would lock every customer out of the tier they just paid for.
	_, res := tierHandshake(t, 2, 2)
	if res.Status != protocol.StatusOK {
		t.Fatalf("status is %d, want StatusOK", res.Status)
	}
	if res.Session == (protocol.SessionID{}) {
		t.Fatal("a token at the minimum tier got no session")
	}
}

func TestTheDefaultMinTierAdmitsEveryPlan(t *testing.T) {
	// -min-tier defaults to 0, so switching this build on changes nothing until an operator
	// deliberately raises the bar on a particular relay. A tier-0 token is the weakest thing that
	// can arrive, and it has to get in.
	_, res := tierHandshake(t, 0, 0)
	if res.Status != protocol.StatusOK {
		t.Fatalf("status is %d, want StatusOK - the default must serve everyone", res.Status)
	}
}

func TestATierAboveTheRelayMinimumIsAdmitted(t *testing.T) {
	// -min-tier is a FLOOR, not an equality. A relay set to 2 serves 2, 3, 4 and every tier above
	// it; only 0 and 1 are turned away.
	//
	// Written because the opposite reading is the natural one - "min-tier 2 means the tier-2
	// relay" - and getting it backwards would be expensive in the quiet direction: the customer
	// on the most expensive plan is exactly the one who would find a premium relay refusing them,
	// and nothing in the code would look wrong.
	_, res := tierHandshake(t, 2, 5)
	if res.Status != protocol.StatusOK {
		t.Fatalf("status is %d, want StatusOK - a tier above the floor must get in", res.Status)
	}
	if res.Session == (protocol.SessionID{}) {
		t.Fatal("a token above the minimum tier got no session")
	}
}
