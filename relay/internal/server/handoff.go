// Hot restart: replace the relayd binary without anybody in a match noticing.
//
// A plain `systemctl restart` closes the UDP socket and the TUN device and forgets every session.
// Every client then has to notice the silence and handshake again, mid-match, and the relays are
// never empty - so a deploy always cost somebody a fight.
//
// A hot restart keeps all three. The running relayd freezes its session table, writes it to a
// file, and exec()s the new binary in its own place: same PID (so systemd sees nothing happen),
// and the socket and the TUN device are handed down as open descriptors. Nothing in the kernel
// changes at all - the interface, its address, the routes, the NAT conntrack entries and the
// public address the game server knows the player by are exactly as they were. The new binary
// adopts the descriptors, reloads the table and carries on. Packets that arrive in between wait
// in the socket's and the device's queues; what a player sees is a pause of a few milliseconds.
//
// What it does NOT carry is configuration: the new binary runs with the old process's arguments.
// A deploy that changes a flag must do a cold restart, and install.sh decides which one it is.
//
// Every failure falls back to today's behaviour rather than to something worse. A state file that
// cannot be read starts an empty table on the kept socket - each client handshakes again, exactly
// as after a cold restart. A new binary that cannot start at all exits, systemd restarts it cold,
// and the descriptors die with the process.
package server

import (
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"net/netip"
	"os"
	"syscall"

	"github.com/gamepingbooster/relay/internal/protocol"
	"github.com/gamepingbooster/relay/internal/tun"
)

// handoffVersion is the state file's format. A binary that does not know the number refuses the
// table rather than guessing at a field that moved; the clients then handshake again.
const handoffVersion = 1

// Inherited is what a hot restart hands the new process.
type Inherited struct {
	UDP   *net.UDPConn
	TUN   *os.File
	State []byte // the session table, as written by Handoff; may be empty or unreadable
}

type handoffState struct {
	V        int               `json:"v"`
	From     string            `json:"from"` // the version that wrote it, for the log line only
	Sessions []handoffSession  `json:"sessions"`
	Reserved []handoffReserved `json:"reserved"`
}

type handoffSession struct {
	ID       protocol.SessionID `json:"id"`
	ResKey   protocol.ClientID  `json:"resKey"`
	InnerIP  netip.Addr         `json:"innerIP"`
	Addr     netip.AddrPort     `json:"addr"`
	LastSeen int64              `json:"lastSeen"`
	// Born is carried, not reset, or a hot restart would quietly extend the maximum session age.
	Born      int64             `json:"born"`
	Resumed   bool              `json:"resumed"`
	UserID    uint64            `json:"userID,omitempty"`
	DeviceKey []byte            `json:"deviceKey,omitempty"`
	ClientID  protocol.ClientID `json:"clientID"`
}

type handoffReserved struct {
	Key protocol.ClientID `json:"key"`
	IP  netip.Addr        `json:"ip"`
}

// Handoff freezes the relay and calls exec with the session table and the two descriptors to pass
// on. exec is expected not to return; if it does, its error is returned and the relay carries on
// exactly as before, nothing having been closed.
//
// The table lock is held for the whole call, which stalls the data plane: every lookup waits. That
// is the point - no session may be added or retired after the table was written - and a successful
// exec never releases it, because the process it belongs to is gone.
func (s *Server) Handoff(exec func(state []byte, udp, tun syscall.RawConn) error) error {
	udpRaw, err := s.conn.SyscallConn()
	if err != nil {
		return fmt.Errorf("udp socket: %w", err)
	}
	tunRaw, err := s.dev.SyscallConn()
	if err != nil {
		return fmt.Errorf("tun device: %w", err)
	}

	s.mu.Lock()
	defer s.mu.Unlock()

	state, err := json.Marshal(s.exportLocked())
	if err != nil {
		return fmt.Errorf("encode the session table: %w", err)
	}
	s.log.Info("hot restart: handing over", "sessions", len(s.bySession), "state_bytes", len(state))
	return exec(state, udpRaw, tunRaw)
}

// exportLocked snapshots the session table. Caller must hold s.mu.
func (s *Server) exportLocked() handoffState {
	st := handoffState{V: handoffVersion, From: s.cfg.Version}
	for _, sess := range s.bySession {
		hs := handoffSession{
			ID: sess.id, ResKey: sess.resKey, InnerIP: sess.innerIP,
			LastSeen: sess.lastSeen.Load(), Born: sess.born, Resumed: sess.resumed,
			UserID: sess.ident.userID, DeviceKey: sess.ident.deviceKey, ClientID: sess.ident.clientID,
		}
		if a := sess.addr.Load(); a != nil {
			hs.Addr = *a
		}
		st.Sessions = append(st.Sessions, hs)
	}
	for k, ip := range s.reservedIPs {
		st.Reserved = append(st.Reserved, handoffReserved{Key: k, IP: ip})
	}
	return st
}

// adopt finishes New for a process started by a hot restart.
func (s *Server) adopt(in *Inherited) (*Server, error) {
	if in.UDP == nil || in.TUN == nil {
		return nil, errors.New("hot restart: the socket or the TUN device was not handed over")
	}
	s.conn = in.UDP
	s.dev = tun.FromFile(in.TUN, s.cfg.TunName)

	sessions, from, err := s.restore(in.State)
	if err != nil {
		// The socket and the device are still good, so keep them: an empty table is what a cold
		// restart would have left, and every client recovers from it by handshaking again.
		s.log.Warn("hot restart: the session table could not be restored, clients will reconnect",
			"err", err)
	} else {
		s.log.Info("hot restart: resumed", "sessions", sessions, "from_version", from,
			"version", s.cfg.Version)
	}
	s.logReady()
	return s, nil
}

// restore loads a table written by Handoff into a server whose pool has just been filled. Anything
// that does not fit this relay's configuration - an address outside the pool, a duplicate - is
// dropped rather than trusted, so a table can never put two clients on one inner address.
func (s *Server) restore(data []byte) (int, string, error) {
	if len(data) == 0 {
		return 0, "", errors.New("no state was handed over")
	}
	var st handoffState
	if err := json.Unmarshal(data, &st); err != nil {
		return 0, "", fmt.Errorf("decode: %w", err)
	}
	if st.V != handoffVersion {
		return 0, st.From, fmt.Errorf("state format %d, this binary reads %d", st.V, handoffVersion)
	}

	inPool := make(map[netip.Addr]bool, len(s.freeIPs))
	for _, ip := range s.freeIPs {
		inPool[ip] = true
	}

	s.mu.Lock()
	defer s.mu.Unlock()

	taken := make(map[netip.Addr]bool)
	for _, hs := range st.Sessions {
		if !inPool[hs.InnerIP] || taken[hs.InnerIP] || !hs.Addr.IsValid() {
			continue
		}
		if _, dup := s.bySession[hs.ID]; dup {
			continue
		}
		sess := &session{
			id: hs.ID, resKey: hs.ResKey, innerIP: hs.InnerIP, born: hs.Born, resumed: hs.Resumed,
			ident: sessionIdent{userID: hs.UserID, deviceKey: hs.DeviceKey, clientID: hs.ClientID},
			up:    newBucket(s.cfg.BurstBytes),
			down:  newBucket(s.cfg.BurstBytes),
		}
		a := hs.Addr
		sess.addr.Store(&a)
		sess.lastSeen.Store(hs.LastSeen)
		s.bySession[sess.id] = sess
		s.byIP[sess.innerIP] = sess
		taken[sess.innerIP] = true
	}

	// Reservations after sessions, one key per address. A live session's own reservation is always
	// kept; a reservation pointing at an address another key's session holds is not.
	held := make(map[netip.Addr]bool)
	for _, sess := range s.bySession {
		s.reservedIPs[sess.resKey] = sess.innerIP
		held[sess.innerIP] = true
	}
	for _, r := range st.Reserved {
		if !inPool[r.IP] || held[r.IP] {
			continue
		}
		if _, has := s.reservedIPs[r.Key]; has {
			continue
		}
		s.reservedIPs[r.Key] = r.IP
		held[r.IP] = true
	}

	free := s.freeIPs[:0]
	for _, ip := range s.freeIPs {
		if !held[ip] {
			free = append(free, ip)
		}
	}
	s.freeIPs = free
	return len(s.bySession), st.From, nil
}
