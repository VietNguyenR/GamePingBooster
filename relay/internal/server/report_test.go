package server

import (
	"encoding/hex"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"net/netip"
	"os"
	"strconv"
	"strings"
	"testing"
	"time"

	"github.com/gamepingbooster/relay/internal/protocol"
)

// A snapshot has to name the devices, not only count them, or the dashboard can say how busy a
// relay is and nothing about who is on it - which is the half that answers a support question.
func TestSnapshotNamesEachDevice(t *testing.T) {
	s := testServer(8)
	s.started = time.Now().Add(-90 * time.Second)
	s.cfg.Version = "test"

	deviceKey := make([]byte, protocol.PublicKeyLen)
	deviceKey[0] = 4
	for i := 1; i < len(deviceKey); i++ {
		deviceKey[i] = byte(i)
	}

	from := netip.MustParseAddrPort("203.0.113.7:40000")
	sess, ok := s.allocSession(from, clientID(1), sessionIdent{userID: 1 << 60, deviceKey: deviceKey})
	if !ok {
		t.Fatal("could not allocate a session")
	}
	if _, ok := s.allocSession(netip.MustParseAddrPort("203.0.113.8:40000"), clientID(2), sessionIdent{}); !ok {
		t.Fatal("could not allocate the second session")
	}

	rep := s.Snapshot(time.Now())

	if rep.Sessions != 2 || len(rep.Devices) != 2 {
		t.Fatalf("sessions=%d devices=%d, want 2 and 2", rep.Sessions, len(rep.Devices))
	}
	if rep.UptimeS < 89 || rep.UptimeS > 92 {
		t.Errorf("uptime_s = %d, want about 90", rep.UptimeS)
	}

	var found *ReportDevice
	for i := range rep.Devices {
		if rep.Devices[i].SessionID == hex.EncodeToString(sess.id[:]) {
			found = &rep.Devices[i]
		}
	}
	if found == nil {
		t.Fatal("the session that was allocated is not in the report")
	}
	if found.InnerIP != sess.innerIP.String() {
		t.Errorf("inner_ip = %q, want %q", found.InnerIP, sess.innerIP)
	}
	if found.ClientIP != "203.0.113.7" || found.ClientPort != 40000 {
		t.Errorf("client = %s:%d, want 203.0.113.7:40000", found.ClientIP, found.ClientPort)
	}
	if found.DeviceKey != hex.EncodeToString(deviceKey) {
		t.Errorf("device_key = %q, want the 130-character device key", found.DeviceKey)
	}

	// A user id above 2^53 is the whole reason this field is a string. As a JSON number it comes
	// back through JavaScript's double with the low bits gone, and only for some customers.
	if found.UserID != strconv.FormatUint(1<<60, 10) {
		t.Errorf("user_id = %q, want %d", found.UserID, uint64(1<<60))
	}
	encoded, err := json.Marshal(found)
	if err != nil {
		t.Fatal(err)
	}
	var back map[string]any
	if err := json.Unmarshal(encoded, &back); err != nil {
		t.Fatal(err)
	}
	if _, isNumber := back["user_id"].(float64); isNumber {
		t.Error("user_id encoded as a JSON number - it will lose precision above 2^53")
	}
}

// A PSK relay has nobody to name: one shared key and no accounts. The report must be honest
// about that rather than inventing an identity.
func TestSnapshotOmitsIdentityInPSKMode(t *testing.T) {
	s := testServer(4)
	if _, ok := s.allocSession(netip.MustParseAddrPort("203.0.113.7:40000"), clientID(1), sessionIdent{}); !ok {
		t.Fatal("could not allocate a session")
	}

	rep := s.Snapshot(time.Now())
	if rep.Mode != "psk" {
		t.Errorf("mode = %q, want psk", rep.Mode)
	}
	if len(rep.Devices) != 1 {
		t.Fatalf("devices = %d, want 1", len(rep.Devices))
	}
	if rep.Devices[0].UserID != "" || rep.Devices[0].DeviceKey != "" {
		t.Errorf("PSK session carries an identity: %+v", rep.Devices[0])
	}
}

// The count is what the dashboard is for, so it has to follow the session table exactly - both
// when a client goes away for good and when it merely times out and keeps its reservation.
func TestSnapshotCountFollowsTheSessionTable(t *testing.T) {
	s := testServer(8)
	a, _ := s.allocSession(netip.MustParseAddrPort("203.0.113.1:1"), clientID(1), sessionIdent{})
	b, _ := s.allocSession(netip.MustParseAddrPort("203.0.113.2:1"), clientID(2), sessionIdent{})

	if got := s.Snapshot(time.Now()).Sessions; got != 2 {
		t.Fatalf("sessions = %d, want 2", got)
	}

	s.releaseSession(a, true) // explicit disconnect
	if got := s.Snapshot(time.Now()).Sessions; got != 1 {
		t.Fatalf("after a disconnect sessions = %d, want 1", got)
	}

	s.releaseSession(b, false) // idle timeout: the address stays reserved
	snap := s.Snapshot(time.Now())
	if snap.Sessions != 0 {
		t.Fatalf("after an idle timeout sessions = %d, want 0", snap.Sessions)
	}
	if snap.Reserved != 1 {
		t.Errorf("reserved = %d, want 1 - the timed-out client should keep its address", snap.Reserved)
	}
}

// The licence server has to be able to tell a real relay's report from anything else that
// arrives at a public URL, and the only thing separating them is this signature.
func TestPostReportIsSignedAndVerifiable(t *testing.T) {
	priv, err := protocol.GenerateKey()
	if err != nil {
		t.Fatal(err)
	}

	type received struct {
		body []byte
		key  string
		sig  string
	}
	got := make(chan received, 1)
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		body, _ := io.ReadAll(r.Body)
		got <- received{body: body, key: r.Header.Get("X-Gpb-Relay-Key"), sig: r.Header.Get("X-Gpb-Signature")}
		w.WriteHeader(http.StatusNoContent)
	}))
	defer srv.Close()

	s := testServer(4)
	s.cfg.RelayPriv = priv
	s.cfg.ReportURL = srv.URL
	if _, ok := s.allocSession(netip.MustParseAddrPort("203.0.113.1:1"), clientID(1), sessionIdent{}); !ok {
		t.Fatal("could not allocate a session")
	}

	pub := hex.EncodeToString(protocol.MarshalPublicKey(&priv.PublicKey))
	if err := s.postReport(srv.Client(), priv, pub); err != nil {
		t.Fatalf("postReport: %v", err)
	}

	r := <-got
	if r.key != pub {
		t.Errorf("relay key header = %q, want %q", r.key, pub)
	}

	sig, err := hex.DecodeString(r.sig)
	if err != nil {
		t.Fatalf("signature is not hex: %v", err)
	}
	if !protocol.Verify(&priv.PublicKey, append([]byte(reportDomain), r.body...), sig) {
		t.Fatal("the signature does not verify over the domain prefix and the body")
	}
	// Without the domain prefix a signature made here could be replayed as one made for another
	// format signed by the same key. Prove the prefix is actually part of what was signed.
	if protocol.Verify(&priv.PublicKey, r.body, sig) {
		t.Fatal("the body verifies without the domain prefix - the prefix is not being signed")
	}

	var rep Report
	if err := json.Unmarshal(r.body, &rep); err != nil {
		t.Fatalf("body is not the report shape: %v", err)
	}
	if rep.V != reportVersion || rep.Sessions != 1 {
		t.Errorf("v=%d sessions=%d, want %d and 1", rep.V, rep.Sessions, reportVersion)
	}
}

// A relay with no report URL must make no outbound call at all. That is the default, and it is
// what every self-hosted relay stays on.
func TestReportingIsOffByDefault(t *testing.T) {
	s := testServer(4)
	done := make(chan struct{})
	finished := make(chan struct{})
	go func() {
		s.loopReport(done)
		close(finished)
	}()

	select {
	case <-finished:
	case <-time.After(2 * time.Second):
		t.Fatal("loopReport did not return immediately with no report URL configured")
	}
	close(done)
}

// A manual cross-check against a running licence server, skipped unless it is pointed at one.
//
// The two implementations of this format - report.go here and relay-report.server.ts there -
// have nothing in either build that catches them drifting apart, and the symptom of drift is a
// dashboard that quietly says every relay is offline. This is how the crossing is done:
//
//	GPB_REPORT_TEST_URL=http://localhost:4000/relay/heartbeat \
//	GPB_REPORT_TEST_KEY=<32 bytes of hex> go test ./internal/server -run CrossCheck -v
//
// The key must be the private half of whatever public key is in that relay's row, so this also
// proves the signature is being verified rather than merely parsed.
func TestReportCrossCheck(t *testing.T) {
	url := os.Getenv("GPB_REPORT_TEST_URL")
	keyHex := os.Getenv("GPB_REPORT_TEST_KEY")
	if url == "" || keyHex == "" {
		t.Skip("set GPB_REPORT_TEST_URL and GPB_REPORT_TEST_KEY to cross-check against a real server")
	}

	raw, err := hex.DecodeString(strings.TrimSpace(keyHex))
	if err != nil {
		t.Fatalf("GPB_REPORT_TEST_KEY is not hex: %v", err)
	}
	priv, err := protocol.ParsePrivateKey(raw)
	if err != nil {
		t.Fatalf("GPB_REPORT_TEST_KEY is not a P-256 private key: %v", err)
	}
	t.Logf("public key: %s", hex.EncodeToString(protocol.MarshalPublicKey(&priv.PublicKey)))

	s := testServer(8)
	s.cfg.ReportURL = url
	s.cfg.Version = "cross-check"
	s.cfg.MaxClients = 50

	deviceKey := make([]byte, protocol.PublicKeyLen)
	deviceKey[0] = 4
	for i := 1; i < len(deviceKey); i++ {
		deviceKey[i] = byte(i * 7)
	}
	if _, ok := s.allocSession(netip.MustParseAddrPort("203.0.113.7:40000"), clientID(1),
		sessionIdent{userID: 1 << 60, deviceKey: deviceKey}); !ok {
		t.Fatal("could not allocate a session")
	}
	if _, ok := s.allocSession(netip.MustParseAddrPort("198.51.100.4:41234"), clientID(2), sessionIdent{}); !ok {
		t.Fatal("could not allocate the second session")
	}

	pub := hex.EncodeToString(protocol.MarshalPublicKey(&priv.PublicKey))
	if err := s.postReport(&http.Client{Timeout: 10 * time.Second}, priv, pub); err != nil {
		t.Fatalf("the licence server refused the report: %v", err)
	}
}
