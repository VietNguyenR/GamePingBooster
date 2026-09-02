package protocol

import (
	"testing"
	"time"
)

// The relay listens on a public UDP port. It will be scanned, and it will be sent deliberate
// garbage. Every parser here runs on that traffic before anything has been authenticated, so a
// single out-of-range index in any of them is a remote crash of the whole relay - every player
// on it disconnected at once.
//
// The hand-written cases in TestMalformedPacketsDoNotKillTheRelay only cover the shapes somebody
// thought of. This covers the ones nobody thought of.
//
//	cd relay && go test ./internal/protocol/ -fuzz=FuzzParsers -fuzztime=60s
//
// A crash is written to testdata/fuzz/ and replays automatically on every later `go test`, so a
// failure found once is guarded forever. Commit those files when they appear.
func FuzzParsers(f *testing.F) {
	psk := []byte(vectorPSK)
	now := time.Unix(vectorUnixTime, 0)

	// Seeds: the shapes that are nearly valid are the interesting starting points, because the
	// fuzzer mutates from them into the almost-right packets that break length assumptions.
	seeds := [][]byte{
		{},
		{0x00},
		{0x21},
		{0x23},
		{0x24},
		{0x26},
		{0x45},
		make([]byte, 8),
		make([]byte, 9),
		make([]byte, 19),
		make([]byte, 20),
		make([]byte, HandshakeReqPSKLen),
		make([]byte, HandshakeReqTokenLen),
		make([]byte, HandshakeRespPSKLen),
		make([]byte, HandshakeRespTokenLen),
		make([]byte, HandshakeRespV2Len),
		make([]byte, TokenLen),
		make([]byte, PingLen),
		make([]byte, MaxPacketLen),
	}
	for _, s := range seeds {
		f.Add(s)
	}
	for _, hexed := range []string{
		"213cbe73aabd5c3ddb000000006955b9000102030405060708",
		"220011223344556677880a4d00050a4d000105780fb1",
		"2311223344556677884500002000000000401100000a4d0005010101011f901f9000080000",
		"2411223344556677880123456789abcdef",
		"261122334455667788",
	} {
		f.Add(decodeSeed(hexed))
	}

	fuzzLicence, err := GenerateKey()
	if err != nil {
		f.Fatalf("generate a licence key for fuzzing: %v", err)
	}

	f.Fuzz(func(t *testing.T, data []byte) {
		// None of these may panic, whatever they are handed. Return values are irrelevant here -
		// the only assertion is that control reaches the end of the function.
		_, _, _ = VerifyHandshakeReq(psk, data, now)
		_, _ = ParseHandshakeResp(psk, data, [8]byte{})
		_ = mustNotPanicMode(data)

		// v3's token path is new attack surface: 240 bytes of attacker-chosen input, parsed
		// before anything about it is known to be true. Fuzz it against a real licence key, so
		// the signature check is exercised rather than short-circuited by a malformed one.
		_, _, _, _ = VerifyHandshakeReqToken(&fuzzLicence.PublicKey, data, now)
		_, _ = VerifyToken(&fuzzLicence.PublicKey, data, now)
		_, _ = ParseHandshakeRespToken(&fuzzLicence.PublicKey, data, [8]byte{})
		_, _ = ParsePublicKey(data)
		_, _ = ParsePrivateKey(data)
		_, _, _ = DecodeData(data)
		_, _, _ = DecodePing(data)
		_, _ = DecodeSessionID(data)
		_, _ = SrcIPv4(data)
		_, _ = DstIPv4(data)
		if len(data) > 0 {
			ParseHeader(data[0])
		}

		// The relay re-encodes whatever it read from the TUN device, so the encoder sees
		// arbitrary lengths too. It must clamp rather than run off the end of the buffer.
		if len(data) <= MaxPacketLen-DataHeaderLen {
			buf := make([]byte, MaxPacketLen)
			out := EncodeData(buf, SessionID{}, data)
			if len(out) != DataHeaderLen+len(data) {
				t.Fatalf("EncodeData returned %d bytes for a %d byte payload", len(out), len(data))
			}
		}
	})
}

func decodeSeed(s string) []byte {
	out := make([]byte, len(s)/2)
	for i := range out {
		hi := hexNibble(s[i*2])
		lo := hexNibble(s[i*2+1])
		out[i] = hi<<4 | lo
	}
	return out
}

func hexNibble(c byte) byte {
	switch {
	case c >= '0' && c <= '9':
		return c - '0'
	case c >= 'a' && c <= 'f':
		return c - 'a' + 10
	default:
		return 0
	}
}

// mustNotPanicMode exists so HandshakeReqMode is fuzzed as well. It reads the auth mode out of
// bytes nobody has validated yet, which is exactly the kind of code that panics on a short
// slice.
func mustNotPanicMode(data []byte) byte {
	m, err := HandshakeReqMode(data)
	if err != nil {
		return 0
	}
	return m
}
