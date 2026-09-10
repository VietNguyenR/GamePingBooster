<!--
Cảm ơn bạn đã gửi PR. Điền các mục dưới đây rồi xoá phần chú thích này.
Tiếng Việt hoặc tiếng Anh đều được.
-->

## What this changes

<!-- One or two sentences. What is different after this PR that was not true before? -->

## Why

<!--
If this fixes something that went wrong, describe the failure: what you did, what happened, what
should have happened. That description usually belongs in a code comment too - see CONTRIBUTING.md.
-->

Fixes #

## How it was tested

<!-- Say what you actually ran, not what you intended to run. -->

- [ ] `cd relay && go test ./...`
- [ ] `cd client && dotnet build GamePingBooster.sln -c Release`
- [ ] `dotnet run --project client/src/GamePingBooster.ProtocolCheck/GamePingBooster.ProtocolCheck.csproj`
- [ ] Tested in a real game session
- [ ] Not applicable, because:

## Checklist

- [ ] One logical change. A refactor bundled with a fix is two reviews wearing one hat.
- [ ] No secrets, keys, tokens, real relay addresses or captured traffic in the diff.
- [ ] Comments explain **why**, not what — especially anywhere the obvious approach is wrong.
- [ ] If this touches the wire protocol: **both** the Go relay and the C# client are updated, plus
      `testdata/protocol-vectors.json`, and ProtocolCheck passes.
- [ ] If this could affect latency, I have measured it and said so below.

## Latency impact

<!--
This project exists for one number: 43 ms, measured in a real game. If your change touches the
packet path - uplink, downlink, routing, the tunnel - say what you measured. "No measurable
change" is a fine answer; "I did not measure" is also fine, just say which.
-->
