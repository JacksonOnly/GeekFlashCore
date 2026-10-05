# GeekFlashCore
A communication protocol and flashing development framework targeting various chip-level protocols. You can use it to develop flashing tools.

## CLI

`GeekFlashCore.CLI` is the .NET 10 command-line host for the implemented protocols. Protocol creation, USB identification, protocol-specific commands, and device information are registered through the CLI protocol registry, so adding MTK/Fastboot/other hosts does not require changing the generic command loop. With no transport option it discovers registered USB devices and waits for a matching hot-plug event on Windows; `--port COMx` and `--usb VID:PID` select a transport explicitly. The protocol is inferred by default and can be forced with `--protocol QualcommEdl`.

```text
geekflash devices
geekflash --port COM73 info
geekflash --loader programmer_firehose.mbn --port COM73 connect
geekflash --port COM73 partitions
geekflash --port COM73 read partition:boot boot.img
geekflash --port COM73 write boot.img partition:boot
geekflash --port COM73 qcom configure
```

When a loader, Digest, VIP table, or vendor authentication response is needed and no corresponding option was supplied, the CLI asks for it through the protocol Provider. `--verbose` enables package-level Debug logs; normal output keeps protocol stages and progress visible without exposing sensitive payloads.

## MediaTek

MediaTek uses LibUsb for BROM/Preloader and independent Legacy, XFlash and XML DA sessions. The reusable .NET 8 contracts, protocol and optional extension services are in `GeekFlashCore.Protocol.Mtk.Abstractions`, `GeekFlashCore.Protocol.Mtk` and `GeekFlashCore.Protocol.Mtk.Extensions`. Sync wire operations share a session gate; async providers resolve DA/EMI and legitimate SLA responses. The CLI is the .NET 10 host.

```text
geekflash --protocol mtk --usb 0E8D:0003 mtk-probe
geekflash --protocol mtk devices
geekflash --protocol mtk --loader DA.bin --mtk-preloader preloader.bin partitions
geekflash --protocol mtk --loader DA.bin --mtk-da-mode xflash read partition:boot boot.img
geekflash --protocol mtk --usb-bus 1 --usb-port-path 2.3 --loader DA.bin info
```

Pure USB discovery requires a unique device; use serial or bus/port selection when several match. `--usb-interface`, `--usb-control-interface` and `--usb-alt` select non-default interfaces. Standard eMMC wire regions are boot1=1, boot2=2, GP1–4=4–7, user=8; UFS LU0–2 use IDs 1–3. RPMB is separate and uses 256-byte data blocks.

After `Probe`, `UseBromSession` exposes the standard mtkclient BROM methods: hardware/version/configuration/capability queries, owned MEID/SOCID/log results, word and register access, UART configuration, authentication, independent DA upload/jumps and Preloader partition transfer. Its command catalog preserves all reference definitions. Sessions expire after the callback or a jump; undocumented catalog entries do not acquire guessed implementations. See the [BROM method mapping](docs/plans/2026-10-05-mtk-brom-method-mapping.md) for the exact correspondence and deliberate safety differences.

DA/auth/cert files are supplied explicitly. Missing SLA responses require a host signer; the CLI has no vendor signing service. Already loaded ABI-compatible extensions provide bounded memory/register/SEJ/RPMB operations after ACK/context validation. UFS RPMB capacities must be supplied explicitly with `--mtk-ufs-rpmb-blocks`. seccfg v3/v4 changes validate the original configuration, persist a backup, write the minimum aligned window and verify readback. Failed writes are not retried.

The exploit portion provides only the `IMtkExploitStrategy` host contract and scoped connection checkpoints: before DA1, after DA1 initialization, after DA2 starts, and after standard DA authentication. The host must explicitly inject a strategy; none are implemented or registered by the core. No patches, payloads or exploit CLI options are included. A completed callback does not bypass standard authentication; failed or reconnect outcomes invalidate the session. See [host checkpoint contract](docs/plans/2026-10-05-mtk-exploit-framework.md), [implementation evidence and support limits](docs/plans/2026-10-05-mtk-protocol-implementation.md), [design](docs/plans/2026-10-05-mtk-protocol-design.md) and [source notices](NOTICE-MTK.md). Hardware compatibility, Windows USB drivers and real transfer throughput remain unverified.
