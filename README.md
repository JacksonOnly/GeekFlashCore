# GeekFlashCore
A communication protocol and flashing development framework targeting various chip-level protocols. You can use it to develop flashing tools.

## Firmware

`GeekFlashCore.Firmware` exposes ZIP/OZIP, Qualcomm/MediaTek OFP, OPS, PAC, KDZ/DZ, UPDATE.APP and full Android payload v2 entries as seekable, reopenable `IDataSource` instances. The CLI provides `firmware list/extract/super-info` and accepts package entries in `write`, `rawprogram` and `patch`. Oplus loose Super packages use a metadata-first plan that streams each Sparse partition once without a payload pre-scan or extraction files. See [firmware API, CLI examples and format limits](docs/firmware.md) and [loose Super writing](docs/oplus-loose-super.md).

Split OFP Super images map to a virtual sparse `super.img` through the reusable Sparse composer, including ordered overlap handling and preserved DONT_CARE gaps. Unpacked directories and `ZIP::OFP::script.xml` references work with the same Qcom script pipeline. See [split Super streaming examples and budgets](docs/ofp-sparse-super.md).

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

## SPRD / Unisoc

`GeekFlashCore.Protocol.Sprd.Abstractions` and `GeekFlashCore.Protocol.Sprd` provide .NET 8 BSL contracts and a serialized synchronous core. BootROM/FDL1/FDL2, native or strict GPT capacities, named-partition reads, streamed Raw/Sparse writes, explicit Raw v1/v2 download, chip UID, erase, reset and power off are exposed through the public facade and CLI registry. Select `--protocol sprd` and an explicit `--port` or `--usb`, supply matching FDL images and confirmed load addresses, and configure native size units or a confirmed GPT sector profile. Raw download defaults off and requires confirmed flush/USB packet sizes. See [SPRD API, CLI profiles and current limits](docs/sprd.md). Hardware validation is pending.

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

Interactive MTK connections probe the device and acknowledge the known standard watchdog write before asking for a DA path. The CLI displays the observed watchdog state and continues through the same prepared session after selection. Unknown watchdog profiles are reported explicitly. DA selection has a finite resource timeout (default 30 seconds); cancellation, timeout or probe failure releases the connection. Noninteractive DA connections still require a valid `--loader` before acquiring USB. See the [loader preparation evidence](docs/plans/2026-10-06-mtk-watchdog-before-loader.md).

The MTK CLI enables `LibUsbConnectionOptions.RecoverInitialReadStall`: a zero-byte Pipe error on the first single-byte bulk read permits one IN endpoint halt clear and one further read within the remaining original timeout. Writes are never resent, and later transfers retain their normal failure behavior. Other hosts leave this option disabled by default. Debug logs identify the BROM handshake step and each reached host checkpoint, including `NoStrategy` or `DescriptorMismatch` when a callback is skipped. `BeforeDa1` precedes the DAA/certificate resource check; missing authentication still stops the connection after that checkpoint. See the [stall recovery and checkpoint evidence](docs/plans/2026-10-06-mtk-initial-usb-stall-implementation.md).

Standard DA diagnostics use the optional `IMtkDaDiagnostics` interface: allowlisted XFlash read-only controls, XML system properties/firmware/hardware information, Legacy/XML registers, explicit runtime PMT layouts, validated eMMC disk PT/MPT v1.0 and the native XML partition table. Legacy eMMC USER/512 discovery falls back to disk PMT when no GPT exists; `mtk-pmt disk` reads it directly. Only invalid header/version markers permit a mirror read; transport failures never do. GPT discovery validates the original header and entry-array CRCs, reads the actual entry LBA and recovers from a valid backup at the last physical block. Query buffers must be disposed; metadata stays bounded to 1 MiB and Raw/Sparse transfers remain streamed. Nonseekable Raw retains its prefix; nonseekable Sparse is rejected before writing.

XFlash recognizes SDMMC, NOR and logical NAND data pages with ECC. Legacy NAND supports 32/64-bit geometry, checksum-validated streaming reads with OOB removal, explicit `IMtkNandAccess.ReadNandPages` for data plus OOB, and logical writes. NAND writes require `--mtk-nand-write`; Legacy with BMT and XML additionally require confirmed `--mtk-nand-capacity bytes`. Native named-partition writes enforce the same policy. `--mtk-iot` selects the known three-region MT6261-style Legacy DA: DA1/DA2 upload, bounded DA3 configuration, CDC switching and NOR reads. It requires a compatible three-region loader. Legacy SDMMC read/physical erase and Legacy NOR/NAND physical erase have no confirmed reference implementation. `mtk-fill` performs an explicit logical pattern overwrite and full readback through ordinary writes. NOR erase requires confirmed `--mtk-nor-erase-block` geometry. `--mtk-pmt-layout 32|64|96` selects the known Legacy layout when GPT is absent.

`MtkBootControlService` validates version, slot count and CRC, backs up the minimum containing sector window, preserves adjacent bytes and verifies the entire readback. It accepts an explicitly resolved `misc`/`para` range; slot indexes are zero-based. `FileStream` backups are flushed durably before writing; other streams must provide their own persistence. RPMB erase is an authenticated zero write through the existing extension ABI. It requires an already provisioned key and does not program keys.

```text
geekflash --protocol mtk --loader DA.bin mtk-query Version da-version.bin
geekflash --protocol mtk --loader DA.bin --mtk-da-mode xml mtk-property DA.VERSION property.xml
geekflash --protocol mtk --loader DA.bin --mtk-da-mode xml mtk-pmt xml
geekflash --protocol mtk --loader DA.bin --mtk-da-mode legacy mtk-pmt 64
geekflash --protocol mtk --loader DA.bin mtk-slot read 8 MISC_OFFSET MISC_LENGTH
geekflash --protocol mtk --loader DA.bin mtk-slot set 1 8 MISC_OFFSET MISC_LENGTH misc-window-backup.bin
geekflash --protocol mtk --loader DA.bin mtk-rpmb erase 0 START_BLOCK BLOCK_COUNT key.bin
```

Replace the uppercase placeholders with confirmed numeric ranges/capacity; backup files are created without overwriting an existing file. `mtk-register read|write address [value]` uses the standard Legacy/XML command and requires aligned addresses. A Download reboot means Fastboot in XFlash/XML; XML PowerOff is unavailable and is rejected before I/O. See the [standard completion evidence and remaining limits](docs/plans/2026-10-05-mtk-standard-completion-implementation.md).

The updated reference is the local `penumbra-main` archive. Normal XFlash/XML eFuse read/write, native named partitions, supplied flash-policy/all-in-one-signature resources and XFlash RSC records use optional public interfaces. eFuse programming is explicit and returns an unknown-write error on an unconfirmed result; the CLI saves the original response to a new durable backup first. `MtkScatterService` preflights YAML/XML manifests and every image, backs up overwritten ranges, preserves moved protected partitions during optional GPT rebuild, and streams Raw/Sparse writes with readback. GPT rebuild writes the backup copy first and rejects conflicting pgpt/sgpt images. XML `IMtkNativeScatterAccess.ApplyXmlScatter` preserves the native FLASH-UPDATE event flow with a confined virtual file namespace and durable backups before ACK. Native bootloader transformations rely on DA status and require device validation.

`MtkSej`, `MtkGcpu` and `MtkDxcc` are independent drivers over already authorized `IMtkHardwareAccess`. The profile supplies confirmed peripheral/scratch/clock addresses; polling and input sizes are bounded, and temporary keys/scratch are cleared. BROM callbacks and standard Legacy/XML DA adapters expire after the gate returns. Services include AES, normal RPMB/MTEE/META derivation, GCPU MT6735 packet ECB and MTEE image decoding, DXCC CMAC/KDF/SHA256, OTP/public-key-hash/lifecycle reads. `MtkTrustedImageCrypto` streams the public software MTEE CBC and CTR formats with borrowed streams. Hardware algorithm outputs still require real device verification.

Already loaded extensions can explicitly select `--mtk-extension-abi penumbra2` for 128/192/256-bit key derivation and the newer SEJ parameters; the default retains the Legacy ABI. XML/UFS RPMB lock metadata requires a supplied, already provisioned key and explicit RPMB capacity. Lock changes persist the complete original block, preserve unrelated bytes and compare the full readback. No extension injection or RPMB key programming is performed.

```text
geekflash --protocol mtk --loader DA.bin mtk-scatter plan scatter.txt
geekflash --protocol mtk --loader DA.bin mtk-scatter flash scatter.txt images backups
geekflash --protocol mtk --loader DA.bin --mtk-da-mode xml mtk-scatter update scatter.xml images backups
geekflash --protocol mtk --loader DA.bin mtk-efuse read efuses.bin
geekflash --protocol mtk --loader DA.bin mtk-partition read boot MAXIMUM_BYTES boot.img
geekflash --protocol mtk --loader DA.bin mtk-fill REGION OFFSET LENGTH BYTE_VALUE
geekflash --protocol mtk --loader DA.bin mtk-rsc PARTITION rsc.bin
geekflash --protocol mtk --loader DA.bin --mtk-extension-abi penumbra2 --mtk-sej-base CONFIRMED_ADDRESS mtk-key id Rpmb 256 key.bin
```

See the [current parity evidence, limits and recovery entry](docs/plans/2026-10-05-mtk-nonexploit-parity-implementation.md) for test counts, source fingerprints, protocol limits and pending hardware checks.

The exploit portion provides the `IMtkExploitStrategy` host contract and scoped connection checkpoints: before DA1, after DA1 initialization and XML DA1 SLA, after DA2 boot/hardware initialization, and after the DA2 SLA phase (including XFlash packet-limit refresh). Context reports separate authentication evidence and uploaded-region counts. XML DA1 signatures use `MtkAuthenticationKind.Da1Sla`; `DaSla` continues to mean DA2. Hosts can explicitly inject either one strategy or an ordered `exploitStrategies` collection of at most 64; the protocol core registers none by default. `NotApplicable` advances to the next matching strategy, while `Completed` ends attempts at that checkpoint. Every callback has its own expiring context. No concrete host exploit algorithms, patch generation or exploit CLI options are included. Seven upstream binary resources are embedded for explicit offline reading as described below; the placeholders do not execute them. A completed callback does not bypass standard authentication; failed or reconnect outcomes invalidate the session. See [host checkpoint contract](docs/plans/2026-10-05-mtk-exploit-framework.md), [current implementation evidence](docs/plans/2026-10-05-mtk-legacy-pmt-checkpoints-implementation.md) and [source notices](NOTICE-MTK.md). Hardware compatibility, Windows USB drivers and real transfer throughput remain unverified.

`GeekFlashCore.Protocol.Mtk.Exploits` contains the nonexecuting `LineCodeExploitStrategy`, `CarbonaraExploitStrategy`, `HeapBaitExploitStrategy` and `UnfusedExploitStrategy` placeholders. They declare routing metadata, observe cancellation and return `NotApplicable` without accessing device context or changing loader data. Their names do not indicate implemented exploit support. See [placeholder scope and validation](docs/plans/2026-10-06-mtk-exploit-placeholders.md).

The MTK CLI explicitly supplies those placeholders in the order Unfused, LineCode, Carbonara, HeapBait. Descriptor filtering invokes Unfused then LineCode at `BeforeDa1` for XFlash/BROM, Unfused for XML, Carbonara at `Da1Ready`, and HeapBait at XML `Da2Ready`. Legacy matches none. Debug logs identify each attempted collection index; result logs confirm actual callback execution. The CLI completes all applicable `NotApplicable` attempts before checking DAA/certificate materials, so missing materials still stop the connection. See [CLI strategy ordering evidence](docs/plans/2026-10-06-mtk-cli-strategy-order-implementation.md).

The placeholders also accept an explicit `MtkExploitDependencies` bundle while retaining their parameterless constructors. The bundle holds a borrowed `MtkExploitResourceStore` and optional host analyzer/transformer contracts; `Execute` never invokes them. `MtkExploitDependencyCatalog.GetResourceInfo(kind)` describes each embedded resource's original filename, logical name, length and SHA-256 without opening the binary. The seven unchanged files from Penumbra's `core/payloads` are available through an explicit `MtkExploitResourceStore.FromEmbeddedResources()` call; streams are opened on demand, checked for length and SHA-256, and exposed as read-only seekable windows owned by the caller. `Empty` and parameterless dependencies remain empty. `MtkExploitDaData` supplies read-only region windows, and `MtkExploitBinaryTools` implements bounded offline digest calculation and exact byte search. These helpers contain no target patterns, payload construction or patch recipes. See [dependency design](docs/plans/2026-10-06-mtk-exploit-dependencies-design.md), [embedded resource design](docs/plans/2026-10-06-mtk-embedded-payload-resources-design.md) and [implementation evidence](docs/plans/2026-10-06-mtk-embedded-payload-resources-implementation.md).

```csharp
var store = MtkExploitResourceStore.FromEmbeddedResources();
var info = MtkExploitDependencyCatalog.GetResourceInfo(MtkExploitResourceKind.BromDefuse);
using Stream stream = store.OpenRead(info.Kind); // Offline bytes; dispose the stream after use.
var dependencies = new MtkExploitDependencies(store); // Optional explicit injection; Execute remains NotApplicable.
```

`GeekFlashCore.Protocol.Mtk.Analysis` supplies Penumbra-style offline `Arch`, `IArchAnalyzer` / `ArchAnalyzer`, `Analyzer`, `ArmAnalyzer`, `Aarch64Analyzer` and `Thumb2Analyzer`. An explicitly selected analyzer borrows a stable, seekable `IDataSource` and a host-provided base address. It supports little-endian reads, address conversions, direct B/BL decoding and scanning, caller-supplied string references, common function prologues and bounded register-value heuristics. Queries open and dispose independent streams using a fixed 64 KiB cache, and accept cancellation; sources are limited to 256 MiB and register lookback to 4096 instructions. Thumb offset zero must be an instruction boundary. No full disassembly/control-flow guarantee, target-specific patterns, DA modification or automatic strategy invocation is supplied. `IMtkExploitDaAnalyzer` remains an optional host-level contract. See [analysis design](docs/plans/2026-10-06-mtk-architecture-analysis-design.md) and [method mapping and verification](docs/plans/2026-10-06-mtk-architecture-analysis-implementation.md).

```csharp
// regionSource and regionBaseAddress are explicitly supplied by the host.
IArchAnalyzer analyzer = new Analyzer(Arch.Aarch64, regionSource, regionBaseAddress);
uint? word = analyzer.ReadUInt32(0, cancellationToken);
long? nextCall = analyzer.NextBranchLinkFromOffset(0, cancellationToken);
```
