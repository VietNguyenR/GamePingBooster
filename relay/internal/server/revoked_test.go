package server

// Last sign-in wins, as the relay sees it: the licence server's `revoked` list cuts the sessions
// of machines that were signed out, refuses their tokens at the handshake, and touches nothing
// else - not the same machine on a newer token, not anybody else's session.

import (
	"crypto/ecdsa"
	"encoding/hex"
	"net/http"
	"net/http/httptest"
	"net/netip"
	"strconv"
	"testing"
	"time"

	"github.com/gamepingbooster/relay/internal/protocol"
)

func deviceHex(d *ecdsa.PrivateKey) string {
	return hex.EncodeToString(protocol.MarshalPublicKey(&d.PublicKey))
}

func newDevice(t *testing.T) *ecdsa.PrivateKey {
	t.Helper()
	d, err := protocol.GenerateKey()
	if err != nil {
		t.Fatal(err)
	}
	return d
}

func TestARevokedTokenIsRefusedWithItsOwnStatus(t *testing.T) {
	s, cli, licence, relayPub := licensedRelay(t)
	device := newDevice(t)
	exp := time.Now().Add(time.Hour)

	s.applyRevoked([]revokedEntry{{Key: deviceHex(device), Exp: exp.Unix()}}, time.Now())

	tok, err := protocol.BuildToken(licence, 42, protocol.MarshalPublicKey(&device.PublicKey), exp, 1, 0)
	if err != nil {
		t.Fatal(err)
	}
	req, nonce, err := protocol.BuildHandshakeReqToken(device, tok, clientID(9), time.Now())
	if err != nil {
		t.Fatal(err)
	}
	if _, err := cli.Write(req); err != nil {
		t.Fatal(err)
	}
	res, err := protocol.ParseHandshakeRespToken(relayPub, recvPacket(t, cli), nonce)
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	if res.Status != protocol.StatusCredentialRevoked {
		t.Fatalf("status %d, want %d (revoked)", res.Status, protocol.StatusCredentialRevoked)
	}
	if len(s.bySession) != 0 {
		t.Fatal("a session was opened for a revoked token")
	}
}

func TestTheSameMachineOnANewerTokenIsAdmitted(t *testing.T) {
	// Signing in on the machine again mints a token with a different expiry. The entry names the
	// old token only, so this one must go through - otherwise winning the machine back would fail.
	s, cli, licence, relayPub := licensedRelay(t)
	device := newDevice(t)
	old := time.Now().Add(time.Hour)
	s.applyRevoked([]revokedEntry{{Key: deviceHex(device), Exp: old.Unix()}}, time.Now())

	tok, err := protocol.BuildToken(licence, 42, protocol.MarshalPublicKey(&device.PublicKey), old.Add(time.Minute), 1, 0)
	if err != nil {
		t.Fatal(err)
	}
	req, nonce, err := protocol.BuildHandshakeReqToken(device, tok, clientID(9), time.Now())
	if err != nil {
		t.Fatal(err)
	}
	if _, err := cli.Write(req); err != nil {
		t.Fatal(err)
	}
	res, err := protocol.ParseHandshakeRespToken(relayPub, recvPacket(t, cli), nonce)
	if err != nil {
		t.Fatalf("parse: %v", err)
	}
	if res.Status != protocol.StatusOK {
		t.Fatalf("status %d, want OK", res.Status)
	}
}

func TestRevokingCutsOnlyTheNamedSession(t *testing.T) {
	s := testServer(8)
	s.cfg.LicencePub = &newDevice(t).PublicKey
	gone, stays, newer := newDevice(t), newDevice(t), newDevice(t)
	exp := time.Now().Add(time.Hour).Unix()

	open := func(d *ecdsa.PrivateKey, id byte, e int64) *session {
		sess, ok := s.allocSession(netip.MustParseAddrPort("203.0.113.1:1000"), clientID(id), sessionIdent{
			userID: uint64(id), deviceKey: protocol.MarshalPublicKey(&d.PublicKey), expiry: e,
		})
		if !ok {
			t.Fatal("could not allocate")
		}
		return sess
	}
	a := open(gone, 1, exp)
	b := open(stays, 2, exp)
	c := open(newer, 3, exp+60) // same key as listed below, different token

	cut := s.applyRevoked([]revokedEntry{
		{Key: deviceHex(gone), Exp: exp},
		{Key: deviceHex(newer), Exp: exp},
	}, time.Now())

	if cut != 1 {
		t.Fatalf("cut %d sessions, want 1", cut)
	}
	if s.lookup(a.id) != nil {
		t.Error("the revoked session is still live")
	}
	if s.lookup(b.id) == nil {
		t.Error("somebody else's session was cut")
	}
	if s.lookup(c.id) == nil {
		t.Error("a session on a newer token for a listed machine was cut")
	}
}

func TestExpiredAndMalformedEntriesAreIgnoredAndPruned(t *testing.T) {
	s := testServer(4)
	d := newDevice(t)
	now := time.Now()
	soon := now.Add(time.Minute).Unix()

	s.applyRevoked([]revokedEntry{
		{Key: deviceHex(d), Exp: now.Add(-time.Minute).Unix()}, // already expired
		{Key: "zz", Exp: soon},                                 // not a key
		{Key: "05" + deviceHex(d)[2:], Exp: soon},              // not an uncompressed point
		{Key: deviceHex(d), Exp: soon},
	}, now)
	if got := len(*s.revoked.Load()); got != 1 {
		t.Fatalf("%d entries kept, want 1", got)
	}

	s.pruneRevoked(now.Add(2 * time.Minute))
	if got := len(*s.revoked.Load()); got != 0 {
		t.Fatalf("%d entries left after their tokens expired, want 0", got)
	}
}

func TestTheReportReplyCarriesTheList(t *testing.T) {
	priv := newDevice(t)
	device := newDevice(t)
	exp := time.Now().Add(time.Hour).Unix()

	reply := `{"ok":true,"revoked":[{"key":"` + deviceHex(device) + `","exp":` + strconv.FormatInt(exp, 10) + `}]}`
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(http.StatusAccepted)
		_, _ = w.Write([]byte(reply))
	}))
	defer srv.Close()

	s := testServer(4)
	s.cfg.RelayPriv = priv
	s.cfg.LicencePub = &newDevice(t).PublicKey
	s.cfg.ReportURL = srv.URL
	sess, ok := s.allocSession(netip.MustParseAddrPort("203.0.113.1:1"), clientID(1), sessionIdent{
		deviceKey: protocol.MarshalPublicKey(&device.PublicKey), expiry: exp,
	})
	if !ok {
		t.Fatal("could not allocate")
	}

	if err := s.postReport(srv.Client(), priv, deviceHex(priv)); err != nil {
		t.Fatalf("postReport: %v", err)
	}
	if s.lookup(sess.id) != nil {
		t.Fatal("the report reply named this token and the session is still live")
	}

	// A licence server from before the field: the list stays as it was.
	reply = `{"ok":true}`
	if err := s.postReport(srv.Client(), priv, deviceHex(priv)); err != nil {
		t.Fatalf("postReport: %v", err)
	}
	if !s.isRevoked(protocol.MarshalPublicKey(&device.PublicKey), exp) {
		t.Fatal("a reply without the field cleared the list")
	}

	// An empty list clears it.
	reply = `{"ok":true,"revoked":[]}`
	if err := s.postReport(srv.Client(), priv, deviceHex(priv)); err != nil {
		t.Fatalf("postReport: %v", err)
	}
	if s.isRevoked(protocol.MarshalPublicKey(&device.PublicKey), exp) {
		t.Fatal("an empty list did not clear it")
	}
}

func TestAHandedOverSessionCanStillBeCut(t *testing.T) {
	s := testServer(4)
	s.cfg.LicencePub = &newDevice(t).PublicKey
	device := newDevice(t)
	exp := time.Now().Add(time.Hour).Unix()
	if _, ok := s.allocSession(netip.MustParseAddrPort("203.0.113.1:1"), clientID(1), sessionIdent{
		deviceKey: protocol.MarshalPublicKey(&device.PublicKey), expiry: exp,
	}); !ok {
		t.Fatal("could not allocate")
	}

	s.mu.Lock()
	st := s.exportLocked()
	s.mu.Unlock()
	if len(st.Sessions) != 1 || st.Sessions[0].Expiry != exp {
		t.Fatalf("the handoff does not carry the token expiry: %+v", st.Sessions)
	}
}
