package server

import "testing"

// The cap has to bind on NEW sessions and only on new sessions.
//
// The pool is deliberately larger than the cap in every test here, so a refusal can only have
// come from MaxClients. With the two numbers equal, every one of these would pass with the cap
// deleted - the pool would refuse in exactly the same place, and the test would be measuring
// arithmetic that was already there.
func TestMaxClientsRefusesPastTheCap(t *testing.T) {
	s := testServer(50)
	s.cfg.MaxClients = 3

	for i := 1; i <= 3; i++ {
		if _, ok := s.allocSession(addr(t, "203.0.113.9:41000"), clientID(byte(i))); !ok {
			t.Fatalf("client %d was refused below the cap of 3", i)
		}
	}

	if _, ok := s.allocSession(addr(t, "203.0.113.9:41000"), clientID(4)); ok {
		t.Fatal("a fourth client was admitted with max-clients 3")
	}
	if len(s.freeIPs) == 0 {
		t.Fatal("the pool ran out, so this proved the pool binds and not the cap")
	}
}

// A client that already holds a session must never be turned away by the cap.
//
// This is the case that would be missed by putting the check at the top of allocSession, and it
// is not hypothetical: a handshake is retried whenever its answer is slow, so at a full relay
// every existing client would start being refused its own session the moment one packet went
// missing. The refusal would look exactly like "the relay is full" and the client would fail
// over to another relay it did not need to move to.
func TestMaxClientsDoesNotRefuseAClientThatAlreadyHasASession(t *testing.T) {
	s := testServer(50)
	s.cfg.MaxClients = 2

	first, ok := s.allocSession(addr(t, "203.0.113.9:41000"), clientID(1))
	if !ok {
		t.Fatal("could not allocate the first session")
	}
	if _, ok := s.allocSession(addr(t, "203.0.113.9:41001"), clientID(2)); !ok {
		t.Fatal("could not allocate the second session")
	}

	// At the cap now. The retry from client 1 must come back with the SAME session.
	again, ok := s.allocSession(addr(t, "203.0.113.9:41002"), clientID(1))
	if !ok {
		t.Fatal("a handshake retry from an existing client was refused at the cap")
	}
	if again.id != first.id {
		t.Fatalf("the retry got a new session %x instead of the existing %x", again.id, first.id)
	}
}

// Zero means "no cap", which is what every relay did before this flag existed and what an
// operator who has not set it expects.
func TestMaxClientsZeroMeansUnlimited(t *testing.T) {
	s := testServer(10)
	s.cfg.MaxClients = 0

	for i := 1; i <= 10; i++ {
		if _, ok := s.allocSession(addr(t, "203.0.113.9:41000"), clientID(byte(i))); !ok {
			t.Fatalf("client %d was refused with max-clients 0 and a pool of 10", i)
		}
	}
	// The eleventh is refused by the POOL, not by the cap - which is the old behaviour intact.
	if _, ok := s.allocSession(addr(t, "203.0.113.9:41000"), clientID(11)); ok {
		t.Fatal("an eleventh client was admitted from a pool of ten")
	}
}

// A slot has to come back when its session goes, or the relay fills up permanently after one
// busy evening and only a restart fixes it.
func TestMaxClientsFreesASlotWhenASessionEnds(t *testing.T) {
	s := testServer(50)
	s.cfg.MaxClients = 2

	one, _ := s.allocSession(addr(t, "203.0.113.9:41000"), clientID(1))
	if _, ok := s.allocSession(addr(t, "203.0.113.9:41001"), clientID(2)); !ok {
		t.Fatal("could not fill the cap")
	}
	if _, ok := s.allocSession(addr(t, "203.0.113.9:41002"), clientID(3)); ok {
		t.Fatal("admitted past the cap")
	}

	// releaseSession with keepReservation false: the client said goodbye rather than going quiet.
	s.releaseSession(one, false)

	if _, ok := s.allocSession(addr(t, "203.0.113.9:41003"), clientID(3)); !ok {
		t.Fatal("a slot did not come back after a session ended")
	}
}
