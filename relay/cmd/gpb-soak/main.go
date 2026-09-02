// Command gpb-soak drives a relay with many simulated clients for a long time, to surface the
// faults that a few minutes with one client never will.
//
// Every serious bug found in this project so far was invisible to manual testing: the address
// reservation that only broke with two clients, the handshake retry that only broke when an
// answer was slow, the route deletion that never ran. They all needed either load, duration, or
// concurrency. This is the tool that supplies all three.
//
// It sends ICMP echo requests to the relay's OWN inner address, so the Linux kernel answers them
// and each packet makes the full round trip - client, UDP, relay, TUN, kernel, TUN, relay, UDP,
// client - without a single byte leaving the VPS. No game servers are touched, no external
// bandwidth is used, and the test can run for hours without bothering anybody.
//
// Usage:
//
//	gpb-soak -relay 203.0.113.10:51820 -psk-file ./psk -clients 5 -pps 60 -duration 30m
//
// The PSK is read from a file or the GPB_PSK environment variable, never from a flag, so it does
// not end up in shell history or in a process list.
package main

import (
	"encoding/binary"
	"errors"
	"flag"
	"fmt"
	"net"
	"net/netip"
	"os"
	"os/signal"
	"sort"
	"strings"
	"sync"
	"sync/atomic"
	"syscall"
	"time"

	"github.com/gamepingbooster/relay/internal/protocol"
)

func main() {
	var (
		relayAddr = flag.String("relay", "", "relay endpoint, ip:port (required)")
		pskFile   = flag.String("psk-file", "", "file holding the pre-shared key (or set GPB_PSK)")
		clients   = flag.Int("clients", 3, "number of simulated clients")
		pps       = flag.Int("pps", 60, "packets per second per client")
		payload   = flag.Int("payload", 100, "ICMP payload bytes; raise it to probe the MTU")
		duration  = flag.Duration("duration", 2*time.Minute, "how long to run")
		interval  = flag.Duration("report", 15*time.Second, "progress report interval")
		churn     = flag.Duration("churn", 0, "reconnect one client this often (0 = never)")
		joins     = flag.Duration("joins", 0, "a brand new client connects this often (0 = never); needed to exercise the address pool the way a live relay is")
		joinHold  = flag.Duration("join-hold", 20*time.Second, "how long each joining client stays before disconnecting")
		churnGap  = flag.Duration("churn-gap", 0, "stay disconnected this long before reconnecting; set it above the relay idle timeout to exercise the address reservation rather than the live-session path")
	)
	flag.Parse()

	if *relayAddr == "" {
		fatal("missing -relay")
	}
	if *payload < 8 {
		fatal("-payload must be at least 8: the send timestamp is carried in the first 8 bytes")
	}
	if *clients < 1 || *pps < 1 {
		fatal("-clients and -pps must be positive")
	}

	psk, err := loadPSK(*pskFile)
	if err != nil {
		fatal(err.Error())
	}
	endpoint, err := netip.ParseAddrPort(*relayAddr)
	if err != nil {
		fatal(fmt.Sprintf("bad -relay %q: %v", *relayAddr, err))
	}

	fmt.Printf("Connecting %d client(s) to %s\n", *clients, endpoint)
	all := make([]*client, 0, *clients)
	for i := 0; i < *clients; i++ {
		// Sequentially, like the real client, so the handshakes do not contend for the uplink.
		c, err := connect(endpoint, psk, i)
		if err != nil {
			fatal(fmt.Sprintf("client %d could not connect: %v", i, err))
		}
		fmt.Printf("  client %d: inner IP %s, relay inner IP %s, MTU %d\n", i, c.innerIP, c.relayIP, c.mtu)
		all = append(all, c)
	}

	// Two clients on the same inner address would each receive the other's replies. That is the
	// exact shape of the address-pool bug this tool exists to catch, and it is worth failing
	// immediately rather than waiting to see it in the cross-talk counter.
	seen := make(map[netip.Addr]int)
	for i, c := range all {
		if other, dup := seen[c.innerIP]; dup {
			fatal(fmt.Sprintf("the relay gave clients %d and %d the same inner IP %s", other, i, c.innerIP))
		}
		seen[c.innerIP] = i
	}

	stop := make(chan struct{})
	sig := make(chan os.Signal, 1)
	signal.Notify(sig, os.Interrupt, syscall.SIGTERM)

	var wg sync.WaitGroup
	for _, c := range all {
		wg.Add(2)
		go func(c *client) { defer wg.Done(); c.sendLoop(*pps, *payload, stop) }(c)
		go func(c *client) { defer wg.Done(); c.recvLoop(stop) }(c)
	}

	var joined, joinFailed, joinStole atomic.Uint64
	if *joins > 0 {
		fmt.Printf("Joins on: a new client arrives every %s and stays %s\n", *joins, *joinHold)
		wg.Add(1)
		go func() {
			defer wg.Done()
			joinLoop(all, endpoint, psk, *joins, *joinHold, stop, &joined, &joinFailed, &joinStole)
		}()
	}

	var churnFailures atomic.Uint64
	if *churn > 0 {
		fmt.Printf("Churn on: one client reconnects every %s\n", *churn)
		wg.Add(1)
		go func() { defer wg.Done(); churnLoop(all, *churn, *churnGap, stop, &churnFailures) }()
	}

	started := time.Now()
	deadline := time.NewTimer(*duration)
	ticker := time.NewTicker(*interval)
	defer ticker.Stop()

running:
	for {
		select {
		case <-deadline.C:
			break running
		case <-sig:
			fmt.Println("\ninterrupted - stopping early")
			break running
		case <-ticker.C:
			fmt.Printf("[%6s] %s\n", time.Since(started).Truncate(time.Second), progress(all))
		}
	}

	close(stop)
	// Let replies still in flight arrive before calling them lost.
	time.Sleep(2 * time.Second)
	for _, c := range all {
		if st := c.state.Load(); st != nil {
			st.conn.Close()
		}
	}
	wg.Wait()

	report(all, time.Since(started), churnFailures.Load(),
		joined.Load(), joinFailed.Load(), joinStole.Load())
}

// ------------------------------------------------------------------- client

// connState is swapped atomically so a reconnect can replace the socket and session under the
// running send and receive loops without a lock in the packet path.
type connState struct {
	conn *net.UDPConn
	sess protocol.SessionID
}

type client struct {
	index     int
	clientID  protocol.ClientID
	endpoint  netip.AddrPort
	psk       []byte
	state     atomic.Pointer[connState]
	innerIP   netip.Addr
	relayIP   netip.Addr
	mtu       int
	echoID    uint16
	seq       atomic.Uint32
	handshake time.Duration

	// Churn results. A reconnecting client must come back on the address its routing table
	// already points at - that is the whole purpose of the client id, and the bug that broke it
	// was invisible until two clients were involved.
	reconnects   atomic.Uint64
	sameAddress  atomic.Uint64
	newAddress   atomic.Uint64
	reconnecting atomic.Bool

	sent      atomic.Uint64
	received  atomic.Uint64
	crossTalk atomic.Uint64 // a reply addressed to somebody else - the pool handed out a duplicate
	notOurs   atomic.Uint64 // a Data packet naming a different session
	malformed atomic.Uint64

	mu   sync.Mutex
	rtts []time.Duration
}

func connect(endpoint netip.AddrPort, psk []byte, index int) (*client, error) {
	var id protocol.ClientID
	// A distinct, stable client id per simulated client, so the relay's address reservation is
	// exercised the way it would be by real installations rather than by one client reconnecting.
	binary.BigEndian.PutUint64(id[:], 0x50AC000000000000|uint64(index+1))

	c := &client{
		index:    index,
		clientID: id,
		endpoint: endpoint,
		psk:      psk,
		echoID:   uint16(0x4000 + index),
	}

	conn, res, took, err := c.dial()
	if err != nil {
		return nil, err
	}
	c.state.Store(&connState{conn: conn, sess: res.Session})
	c.innerIP = res.ClientIP
	c.relayIP = res.RelayIP
	c.mtu = int(res.MTU)
	c.handshake = took
	return c, nil
}

// dial opens a fresh socket and handshakes on it, retrying the way the real client does because
// UDP gives no delivery guarantee.
func (c *client) dial() (*net.UDPConn, protocol.HandshakeResult, time.Duration, error) {
	var zero protocol.HandshakeResult

	conn, err := net.DialUDP("udp4", nil, net.UDPAddrFromAddrPort(c.endpoint))
	if err != nil {
		return nil, zero, 0, err
	}

	var last error

	// Cho chay toi da 4 lan thoi chay lam chay lon
	for attempt := 1; attempt <= 4; attempt++ {
		req, nonce, err := protocol.BuildHandshakeReq(c.psk, c.clientID, time.Now())
		if err != nil {
			conn.Close()
			return nil, zero, 0, err
		}
		sentAt := time.Now()
		if _, err := conn.Write(req); err != nil {
			conn.Close()
			return nil, zero, 0, err
		}

		buf := make([]byte, protocol.MaxPacketLen)
		_ = conn.SetReadDeadline(time.Now().Add(2 * time.Second))
		n, err := conn.Read(buf)
		if err != nil {
			last = err
			continue
		}
		// v3 echoes the nonce back, and checking it is the point: a captured answer replayed at
		// a client mid-handshake would otherwise hand it a session the relay has forgotten.
		// Each attempt sends a fresh nonce, so a late answer to attempt 1 is rejected here
		// rather than adopted during attempt 2.
		res, err := protocol.ParseHandshakeResp(c.psk, buf[:n], nonce)
		if err != nil {
			last = err
			continue
		}
		if res.Status != protocol.StatusOK {
			conn.Close()
			return nil, zero, 0, fmt.Errorf("the relay refused the handshake with status %d", res.Status)
		}
		_ = conn.SetReadDeadline(time.Time{})
		return conn, res, time.Since(sentAt), nil
	}
	conn.Close()
	return nil, zero, 0, fmt.Errorf("no usable answer after 4 attempts: %w", last)
}

// reconnect drops the socket and handshakes again on a new one, the way the client's supervisor
// does after the tunnel goes quiet. It deliberately does NOT send a Disconnect first: a real
// reconnect must not tell the relay to release the address it is about to ask for again.
func (c *client) reconnect(gap time.Duration) error {
	old := c.state.Load()

	// With a gap, go completely silent for a while first. That is the only way to reach the
	// address RESERVATION path: without it the old session is still live on the relay, the
	// repeated handshake is answered from the live-session path instead, and the reservation -
	// the thing that actually broke - is never exercised at all.
	if gap > 0 && old != nil {
		if !c.reconnecting.CompareAndSwap(false, true) {
			return nil // already away; do not start a second gap for the same client
		}
		defer c.reconnecting.Store(false)
		old.conn.Close()
		time.Sleep(gap)
	}

	conn, res, _, err := c.dial()
	if err != nil {
		return err
	}
	c.state.Store(&connState{conn: conn, sess: res.Session})
	if old != nil {
		old.conn.Close()
	}

	c.reconnects.Add(1)
	if res.ClientIP == c.innerIP {
		c.sameAddress.Add(1)
	} else {
		c.newAddress.Add(1)
		// Follow the relay, but record that the client's routing table would have had to be
		// rebuilt at this point - on a real machine, mid-game.
		c.innerIP = res.ClientIP
	}
	return nil
}

func (c *client) sendLoop(pps, payload int, stop <-chan struct{}) {
	tick := time.NewTicker(time.Second / time.Duration(pps))
	defer tick.Stop()
	keepalive := time.NewTicker(3 * time.Second)
	defer keepalive.Stop()

	wire := make([]byte, protocol.MaxPacketLen)
	for {
		select {
		case <-stop:
			return
		case <-keepalive.C:
			// The real client sends these, so the soak should too - it keeps the relay's view of
			// the session identical to production.
			st := c.state.Load()
			// A write failure here is almost always the socket being swapped by a reconnect.
			// Keep going: the next tick picks up the new one.
			_, _ = st.conn.Write(protocol.BuildPing(st.sess, uint64(time.Now().UnixNano())))
		case <-tick.C:
			st := c.state.Load()
			seq := uint16(c.seq.Add(1))
			inner := buildEchoRequest(c.innerIP, c.relayIP, c.echoID, seq, payload)
			out := protocol.EncodeData(wire, st.sess, inner)
			if _, err := st.conn.Write(out); err != nil {
				continue
			}
			c.sent.Add(1)
		}
	}
}

// joinLoop simulates other players arriving on the relay while the soak clients are running.
//
// This is not decoration, and leaving it out is what made an earlier version of this tool pass
// against a relay whose address reservation was deliberately broken. A client that goes away only
// loses its reserved address if somebody else takes it meanwhile, and only a client with NO
// reservation of its own ever draws from the free pool. Without joiners, that interleaving never
// happens and the bug is unreachable.
//
// A joiner that is handed an address another client is holding is the failure, and it is checked
// at handshake time rather than by waiting to see who breaks.
func joinLoop(all []*client, endpoint netip.AddrPort, psk []byte, every, hold time.Duration,
	stop <-chan struct{}, joined, failed, stole *atomic.Uint64) {

	t := time.NewTicker(every)
	defer t.Stop()
	seq := uint64(0)
	for {
		select {
		case <-stop:
			return
		case <-t.C:
			seq++
			j := &client{index: -int(seq), endpoint: endpoint, psk: psk}
			binary.BigEndian.PutUint64(j.clientID[:], 0x101A000000000000|seq)

			conn, res, _, err := j.dial()
			if err != nil {
				failed.Add(1)
				continue
			}
			joined.Add(1)

			for _, c := range all {
				if res.ClientIP == c.innerIP {
					stole.Add(1)
					fmt.Printf("         a new client was handed %s, which client %d is holding%s\n",
						res.ClientIP, c.index, awayNote(c))
					break
				}
			}

			go func(conn *net.UDPConn, sess protocol.SessionID) {
				timer := time.NewTimer(hold)
				defer timer.Stop()
				select {
				case <-stop:
				case <-timer.C:
				}
				// Leave the way a user switching the booster off does, so the address goes back
				// to the pool instead of waiting out the idle timeout.
				_, _ = conn.Write(protocol.BuildDisconnect(sess))
				conn.Close()
			}(conn, res.Session)
		}
	}
}

func awayNote(c *client) string {
	if c.reconnecting.Load() {
		return " (it is temporarily away and expects that address back)"
	}
	return " RIGHT NOW"
}

// churnLoop reconnects one client at a time, on a rotation, while everything else keeps running.
// This is the condition under which the address reservation, the session table and the pool are
// all being changed by one client while others are actively using them - and it is the condition
// every serious bug in this project so far has needed.
func churnLoop(all []*client, every, gap time.Duration, stop <-chan struct{}, failures *atomic.Uint64) {
	t := time.NewTicker(every)
	defer t.Stop()
	next := 0
	for {
		select {
		case <-stop:
			return
		case <-t.C:
			c := all[next%len(all)]
			next++
			if c.reconnecting.Load() {
				continue // still away from its previous turn
			}
			// Each reconnect runs on its own goroutine so that a long silent gap does not hold
			// up the rotation. Overlapping gaps are the whole point: with only one client away
			// at a time, nobody else ever allocates an address while it is gone, and the pool
			// bugs that need exactly that interleaving stay hidden.
			go func(c *client) {
				if err := c.reconnect(gap); err != nil {
					failures.Add(1)
					fmt.Printf("         client %d failed to reconnect: %v\n", c.index, err)
				}
			}(c)
		}
	}
}

func (c *client) recvLoop(stop <-chan struct{}) {
	buf := make([]byte, protocol.MaxPacketLen)
	for {
		select {
		case <-stop:
			return
		default:
		}

		st := c.state.Load()
		_ = st.conn.SetReadDeadline(time.Now().Add(500 * time.Millisecond))
		n, err := st.conn.Read(buf)
		if err != nil {
			var ne net.Error
			if errors.As(err, &ne) && ne.Timeout() {
				continue
			}
			// Not a timeout: either the run is ending, or a reconnect just closed this socket
			// out from under us. Reload and carry on - deciding which it was by looking at the
			// error would be guessing, and the stop channel already says it unambiguously.
			select {
			case <-stop:
				return
			default:
			}
			if c.reconnecting.Load() {
				time.Sleep(50 * time.Millisecond)
				continue // deliberately offline during a churn gap
			}
			if c.state.Load() == st {
				return // nobody swapped it, so the socket really is gone
			}
			continue
		}
		if n < 1 {
			continue
		}

		version, msgType := protocol.ParseHeader(buf[0])
		if version != protocol.Version {
			c.malformed.Add(1)
			continue
		}
		if msgType == protocol.TypePong {
			continue
		}
		if msgType != protocol.TypeData {
			continue
		}

		sid, inner, err := protocol.DecodeData(buf[:n])
		if err != nil {
			c.malformed.Add(1)
			continue
		}
		if sid != c.state.Load().sess {
			// A packet for a session we no longer hold is expected briefly after a reconnect,
			// but it must never happen otherwise.
			c.notOurs.Add(1)
			continue
		}
		c.inspectReply(inner)
	}
}

// inspectReply is where the interesting failures show up. A reply addressed to another client's
// inner IP means the relay handed the same address to two sessions, and each of them is now
// receiving traffic meant for the other.
func (c *client) inspectReply(inner []byte) {
	if len(inner) < 28 || inner[0]>>4 != 4 || inner[9] != 1 {
		c.malformed.Add(1)
		return
	}
	dst, ok := protocol.DstIPv4(inner)
	if !ok {
		c.malformed.Add(1)
		return
	}
	if dst != c.innerIP {
		c.crossTalk.Add(1)
		return
	}

	ihl := int(inner[0]&0x0f) * 4
	if ihl < 20 || len(inner) < ihl+16 {
		c.malformed.Add(1)
		return
	}
	icmp := inner[ihl:]
	if icmp[0] != 0 { // 0 = echo reply
		return // an ICMP error, not our echo - not a defect, just not a measurement
	}
	if binary.BigEndian.Uint16(icmp[4:6]) != c.echoID {
		c.crossTalk.Add(1)
		return
	}

	sentAt := int64(binary.BigEndian.Uint64(icmp[8:16]))
	rtt := time.Since(time.Unix(0, sentAt))
	if rtt < 0 || rtt > time.Minute {
		c.malformed.Add(1)
		return
	}

	c.received.Add(1)
	c.mu.Lock()
	c.rtts = append(c.rtts, rtt)
	c.mu.Unlock()
}

// ---------------------------------------------------------------- reporting

func progress(all []*client) string {
	var sent, recv, cross uint64
	for _, c := range all {
		sent += c.sent.Load()
		recv += c.received.Load()
		cross += c.crossTalk.Load()
	}
	loss := 0.0
	if sent > 0 {
		loss = float64(sent-recv) / float64(sent) * 100
	}
	s := fmt.Sprintf("sent %d, received %d, loss %.2f%%", sent, recv, loss)
	if cross > 0 {
		s += fmt.Sprintf("  CROSS-TALK %d", cross)
	}
	return s
}

func report(all []*client, elapsed time.Duration, churnFailures, joined, joinFailed, joinStole uint64) {
	fmt.Printf("\n=== %s over %s ===\n", "soak result", elapsed.Truncate(time.Second))

	var totalSent, totalRecv, totalCross, totalNotOurs, totalMalformed uint64
	var totalReconnects, totalSame, totalNew uint64
	var allRtts []time.Duration

	for _, c := range all {
		c.mu.Lock()
		rtts := append([]time.Duration(nil), c.rtts...)
		c.mu.Unlock()
		allRtts = append(allRtts, rtts...)

		sent, recv := c.sent.Load(), c.received.Load()
		totalSent += sent
		totalRecv += recv
		totalCross += c.crossTalk.Load()
		totalNotOurs += c.notOurs.Load()
		totalMalformed += c.malformed.Load()
		totalReconnects += c.reconnects.Load()
		totalSame += c.sameAddress.Load()
		totalNew += c.newAddress.Load()

		fmt.Printf("client %d (%s): sent %d, received %d, loss %s, handshake %s, %s\n",
			c.index, c.innerIP, sent, recv, lossString(sent, recv),
			ms(c.handshake), percentiles(rtts))
	}

	fmt.Printf("\ntotal: sent %d, received %d, loss %s\n", totalSent, totalRecv, lossString(totalSent, totalRecv))
	fmt.Printf("rtt:   %s\n", percentiles(allRtts))

	problems := 0

	if joined > 0 || joinFailed > 0 {
		fmt.Printf("joins: %d new client(s) arrived, %d refused, %d handed an address already held\n",
			joined, joinFailed, joinStole)
		if joinStole > 0 {
			fmt.Printf("\nADDRESS HANDED OUT TWICE: %d time(s) the pool gave a new client an address "+
				"another client owns. Two sessions on one inner IP each receive the other's traffic.\n",
				joinStole)
			problems++
		}
	}

	if totalReconnects > 0 || churnFailures > 0 {
		fmt.Printf("churn: %d reconnect(s), %d resumed on the same inner IP, %d got a new one, %d failed\n",
			totalReconnects, totalSame, totalNew, churnFailures)
		if totalNew > 0 {
			fmt.Printf("\nRESERVATION LOST: %d reconnect(s) came back on a different inner address. On a "+
				"real machine each of those means the virtual adapter is re-addressed and every game "+
				"route reinstalled, mid-game. With addresses still free in the pool, this is a bug.\n", totalNew)
			problems++
		}
		if churnFailures > 0 {
			fmt.Printf("\nRECONNECT FAILED: %d attempt(s) could not get back in at all.\n", churnFailures)
			problems++
		}
	}

	if totalCross > 0 {
		fmt.Printf("\nCROSS-TALK: %d reply/replies arrived for the wrong client. Two sessions are "+
			"sharing an inner address - this is a correctness bug in the relay's address pool.\n", totalCross)
		problems++
	}
	if totalNotOurs > 0 {
		fmt.Printf("NOT OURS: %d Data packet(s) named another session. The relay is sending a "+
			"client traffic it should not see.\n", totalNotOurs)
		problems++
	}
	if totalMalformed > 0 {
		fmt.Printf("MALFORMED: %d packet(s) could not be understood.\n", totalMalformed)
		problems++
	}
	if problems == 0 {
		fmt.Println("\nNo cross-talk, no foreign sessions, no malformed packets.")
	}
	if problems > 0 {
		os.Exit(1)
	}
}

func lossString(sent, recv uint64) string {
	if sent == 0 {
		return "n/a"
	}
	if recv > sent {
		return fmt.Sprintf("negative (%d duplicates)", recv-sent)
	}
	return fmt.Sprintf("%.2f%% (%d)", float64(sent-recv)/float64(sent)*100, sent-recv)
}

// percentiles reports the tail, not just the average. A mean hides exactly the stalls a player
// notices: an average of 45 ms with a p99 of 400 ms feels bad and reads fine.
func percentiles(rtts []time.Duration) string {
	if len(rtts) == 0 {
		return "rtt: no samples"
	}
	sorted := append([]time.Duration(nil), rtts...)
	sort.Slice(sorted, func(i, j int) bool { return sorted[i] < sorted[j] })

	at := func(p float64) time.Duration {
		i := int(float64(len(sorted)-1) * p)
		return sorted[i]
	}
	var sum time.Duration
	for _, d := range sorted {
		sum += d
	}
	return fmt.Sprintf("min %s avg %s p50 %s p95 %s p99 %s max %s",
		ms(sorted[0]), ms(sum/time.Duration(len(sorted))), ms(at(0.50)),
		ms(at(0.95)), ms(at(0.99)), ms(sorted[len(sorted)-1]))
}

// ms keeps enough precision to stay useful on a loopback test, where everything would otherwise
// round to 0.0ms and the numbers would say nothing.
func ms(d time.Duration) string {
	v := float64(d.Microseconds()) / 1000
	if v < 10 {
		return fmt.Sprintf("%.2fms", v)
	}
	return fmt.Sprintf("%.1fms", v)
}

// -------------------------------------------------------------------- misc

// buildEchoRequest makes an ICMP echo request carrying its own send time, so the round trip can
// be measured without keeping a table of outstanding packets - which would itself be a source of
// contention at high rates.
func buildEchoRequest(src, dst netip.Addr, id, seq uint16, payload int) []byte {
	total := 20 + 8 + payload
	pkt := make([]byte, total)

	pkt[0] = 0x45
	binary.BigEndian.PutUint16(pkt[2:4], uint16(total))
	binary.BigEndian.PutUint16(pkt[4:6], seq)
	pkt[8] = 64 // TTL
	pkt[9] = 1  // ICMP
	s := src.As4()
	d := dst.As4()
	copy(pkt[12:16], s[:])
	copy(pkt[16:20], d[:])
	binary.BigEndian.PutUint16(pkt[10:12], checksum(pkt[:20]))

	icmp := pkt[20:]
	icmp[0] = 8 // echo request
	binary.BigEndian.PutUint16(icmp[4:6], id)
	binary.BigEndian.PutUint16(icmp[6:8], seq)
	binary.BigEndian.PutUint64(icmp[8:16], uint64(time.Now().UnixNano()))
	binary.BigEndian.PutUint16(icmp[2:4], checksum(icmp))

	return pkt
}

func checksum(b []byte) uint16 {
	var sum uint32
	for i := 0; i+1 < len(b); i += 2 {
		sum += uint32(b[i])<<8 | uint32(b[i+1])
	}
	if len(b)%2 == 1 {
		sum += uint32(b[len(b)-1]) << 8
	}
	for sum>>16 != 0 {
		sum = sum&0xffff + sum>>16
	}
	return ^uint16(sum)
}

func loadPSK(path string) ([]byte, error) {
	if path != "" {
		b, err := os.ReadFile(path)
		if err != nil {
			return nil, fmt.Errorf("read %s: %w", path, err)
		}
		psk := []byte(strings.TrimSpace(string(b)))
		if len(psk) < 16 {
			return nil, fmt.Errorf("the PSK in %s is too short (%d bytes)", path, len(psk))
		}
		return psk, nil
	}
	if env := strings.TrimSpace(os.Getenv("GPB_PSK")); env != "" {
		return []byte(env), nil
	}
	return nil, errors.New("no PSK: pass -psk-file or set GPB_PSK")
}

func fatal(msg string) {
	fmt.Fprintln(os.Stderr, "gpb-soak: "+msg)
	os.Exit(2)
}
