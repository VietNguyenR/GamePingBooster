# Contributing

Thanks for looking. This is a small project with a narrow purpose — making PUBG playable from
Vietnam — and contributions are welcome as long as they keep that purpose in view.

**Tiếng Việt cũng được.** Issue và pull request viết bằng tiếng Việt hoàn toàn ổn. Code comment
thì giữ tiếng Anh cho đồng bộ với phần còn lại.

## Before you write code

Open an issue first for anything beyond a typo. Two reasons, both practical: someone may already
be working on it, and some things that look like bugs are deliberate — the code is heavily
commented precisely so those decisions are visible, and it is worth reading the comment before
changing the line under it.

For a new game, use the **Game support request** issue template rather than a pull request. Adding
a game is mostly data collection, not code, and the capture has to be done by somebody who plays
it.

## Setting up

You need the .NET SDK (9 or newer), Go 1.22 or newer, and Windows for the client half. The relay
builds and runs on Linux.

```
git clone https://github.com/VietNguyenR/GamePingBooster
cd GamePingBooster

# relay
cd relay && go build ./... && go test ./...

# client (Windows)
cd client && dotnet build GamePingBooster.sln -c Release
```

`README.md` has the full command list. Two things are deliberately not in the repository and you
will need to supply them yourself:

- `client/native/wintun/wintun.dll` — third-party driver. `client/native/wintun/README.md` says
  where to get it and how to verify its signature. **Verify it.** It runs in kernel space.
- `gpb.conf` and `client/config.json` — copy the `.example` files beside them. They hold your own
  relay addresses and keys, which is why they are gitignored.

## What a good change looks like

**Comments explain why, not what.** The existing style is unusual and it is intentional: where a
line exists because something went wrong once, the comment says what went wrong. `git log` gets
squashed, summarised and eventually unread; the comment beside the line does not. If your change
fixes a real failure, write down what the failure was.

**One change per pull request.** A refactor bundled with a fix is two reviews wearing one hat.

**Keep the tests passing, and add one when you fix a bug.** The bug you just fixed is the cheapest
test case you will ever have:

```
cd relay && go test ./...
cd client && dotnet build GamePingBooster.sln -c Release
dotnet run --project client/src/GamePingBooster.ProtocolCheck/GamePingBooster.ProtocolCheck.csproj
```

That last one checks the Go relay and the C# client agree on every byte of the wire format. If you
touch the protocol, it must still pass — and note that a protocol change means editing **both**
implementations plus `testdata/protocol-vectors.json`.

**Do not commit secrets.** No keys, no tokens, no real relay addresses, no captured traffic. The
`.gitignore` covers the known cases; if you add a new kind of local file, add a rule for it too.

## What will probably be turned down

- Changes that trade measured latency for tidiness. The number this project exists for is 43 ms,
  measured in a real game, and it is the thing every change has to protect.
- Anything that touches the game process — injection, memory reads, DLL hooks. The architecture
  deliberately stays outside the game, and that is not up for negotiation: it is what keeps the
  anti-cheat question answerable.
- Encrypting the tunnel payload without a measurement showing what it costs. PUBG already
  encrypts its own gameplay packets.

## Security

**Do not open a public issue for a security problem.** This project handles authentication,
payment and relay infrastructure, and a public issue is a disclosure before there is a fix.
Report it privately to the maintainer instead — use GitHub's **Report a vulnerability** button on
the Security tab, which creates a private advisory only the maintainer can see.

## Licence

By contributing you agree that your contribution is licensed under the MIT licence, the same as
the rest of the repository.
