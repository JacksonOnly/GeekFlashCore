# MediaTek implementation provenance

Legacy eMMC disk PT/MPT v1.0 format facts were inspected on 2026-10-05 from
[JacksonOnly/MtkPt README](https://github.com/JacksonOnly/MtkPt/blob/main/README.md): USER tail locations,
4096-byte blocks, version and header/tail signatures, forty 88-byte entries and the low-byte sequence.
The bounded C# parser is independently implemented; no MtkPt code is copied. Runtime READ_PMT layouts remain separate.

GeekFlashCore's license remains the GNU Affero General Public License v3 in `LICENSE`. The MTK implementation is a bounded synchronous C# rewrite, with the following protocol/layout references and original attribution retained. The historical reference work below did not distribute binaries. As of 2026-10-06, seven unchanged Penumbra payload resources are embedded as detailed below; no concrete host exploit algorithms, private keys or authentication services are added.

| Reference | Inspected revision | Original attribution / license | Referenced behavior |
| --- | --- | --- | --- |
| bkerler/mtkclient | `e9fcf97` | B. Kerler, 2018–2024, GPLv3 | BROM echo/status/checksum, D8/DC DA metadata, EMI, Legacy/XFlash, seccfg layouts and public software AES format; pre-DA and post-authentication host checkpoints |
| Shomy/penumbra | `ce13391` | Shomy, 2025–2026, AGPL-3.0-or-later | XFlash framing and command order, XML command/file lifetimes, extension ACK/context, memory/register/SEJ/RPMB host ABI; strategy metadata, returned DA and stage placement only |
| Shomy/mtk-payloads | `e34d980` (historical) | Per-file notices: Shomy, 2025–2026; AGPL/GPL | Earlier work referenced existing DA extension context and command ABI only; the later binary resource copy is recorded separately below |
| shomykohai/acon | main snapshot 2026-10-07 | Shomy, AGPL-3.0-or-later | SoC MMIO table (uart0/toprgu/hacc/tzcc/ssr bases and hwcode mapping) adopted for the chip catalog, including newer SoCs absent from mtkclient |
| GeekFlashTool.MtkClient | `67ced05` | Existing project notices remain with that project | Cross-check of FC big-endian fields, old DA layout and XML command behavior; application/provider/account code is not copied |

Relevant mtkclient paths: `Library/mtk_preloader.py`, `config/brom_config.py`, `config/mtk_config.py` (standard chip names, DA aliases and watchdog registers only), `Library/DA/mtk_da_handler.py::configure_da`, `Library/DA/daconfig.py`, `Library/DA/legacy/dalegacy_lib.py`, `Library/DA/legacy/dalegacy_flash_param.py`, `Library/DA/xflash/xflash_lib.py`, `Library/DA/xml/xml_lib.py`, `Library/Hardware/seccfg.py`, `Library/Hardware/hwcrypto_sej.py`.

Relevant penumbra paths: `core/src/da/dafile.rs`, `core/src/da/xflash/{cmds,xflash_lib,da_protocol,exts}.rs`, `core/src/da/xml/{cmds,xml_lib,da_protocol,flash,storage,exts}.rs`, and the framework declarations/call sites in `core/src/exploit/mod.rs` and `core/src/macros.rs`. No concrete strategy or DA modification algorithms are copied. Relevant payload paths are `da_x`, `da_xml` and `libsej` ABI definitions. The corresponding C# files retain source notices. The GPLv3 text is included in `licenses/MTK-GPL-3.0.txt`.

Some inspected reference working trees already contained local changes. Revisions identify their bases, not an assertion that every inspected file matched its commit. Reference working trees were read only and were not modified.

The 2026-10-05 standard completion also references penumbra `core/src/core/bootctrl.rs` (Shomy, 2026, AGPL-3.0-or-later) for Android boot-control fields and CRC, and mtkclient `Library/DA/{legacy/dalegacy_flash_param,xflash/xflash_lib,xml/xml_cmd,xml/xml_lib}.py` for standard read-only queries, PMT/XML partition metadata, register commands and NAND/NOR/SDMMC geometry. DA `m_start_offset` metadata is preserved as a length/signature boundary, not added to `m_start_addr`. No exploitation algorithms or device-side payloads were added. The current local penumbra copy has no Git metadata; its prior inspected revision above is historical provenance, not a newly verified file revision.

## EXP and PatchDA port (2026-10-07)

Following explicit user authorization on 2026-10-07, the four Penumbra exploit strategies and
both DA patchers were ported to bounded C# implementations under
`src/GeekFlashCore.Protocol.Mtk/Exploits/Penumbra/`. The port preserves the original authors'
attribution and license obligations:

- `utils/patching.rs` (Shomy, 2026, AGPL-3.0-or-later) → `PenumbraPatching`.
- `exploit/mod.rs` DaEntryExt hash-slot/arch detection and `get_v6_payload` (Shomy,
  2025–2026, AGPL-3.0-or-later) → `PenumbraDaMetadata`, `PenumbraPayloadFormat`.
- `exploit/linecode.rs` (Shomy, 2025–2026, AGPL-3.0-or-later; original exploit credits:
  Chimera Tool team; implementation details studied from R0rt1z2/kamakiri mt8516-cupcake,
  original work chaosmaster (k4y0z) and xyzz under MIT; inspired by bkerler's generic patcher
  in mtkclient) → `PenumbraLinecodeTable`, `PenumbraLinecodeTrigger`.
- `exploit/carbonara.rs` (Shomy, 2025–2026, AGPL-3.0-or-later; protection patterns taken from
  mtkclient) → Carbonara strategy and `PenumbraDaChannelSupport`.
- `exploit/heapbait.rs` (Shomy and R0rt1z2, 2026, AGPL-3.0-or-later; original exploit credits
  go to the Chimera Tool team) → `PenumbraHeapBaitRunner`.
- `da/xflash/patch.rs`, `da/xml/patch.rs` (Shomy, 2025–2026, AGPL-3.0-or-later; analysis
  strings originate from mtkclient, B. Kerler, GPLv3) → `PenumbraXFlashDaPatcher`,
  `PenumbraXmlDaPatcher`.

Deviations from the reference (documented for transparency): the reference `exploit!` macro
silently continues after a failed strategy, while this port keeps the framework's terminal
failure semantics once device I/O has started; the reference's permissive unbounded allocations
(50 MiB sled, whole-DA diff buffers) are streamed or bounded; failure after device I/O
invalidates the session instead of continuing. These ports are static code only: no hardware
run has validated any strategy, and a Completed result never proves that a device accepted
any patch.


Changes from references include bounded counts/lengths/XML, explicit endianness and final status checks, serialized generations, borrowed stream ownership, late authentication cleanup, streaming Raw/Sparse, strict extension prerequisites and verified seccfg planning/readback. Dummy signatures, timeout-as-success, silent checksum failures, arbitrary XML and automatic retries after unknown writes are excluded.

## Updated normal-function references (2026-10-05)

The current primary reference is the user's `D:\Code\Rust\penumbra-main` archive, without Git metadata. Its normal `core/src/da/{protocol,scatter,types}.rs`, `core/src/da/{xflash,xml}/{protocol,flash,cmd,exts}.rs` and `tui/src/cli/commands/device/keys.rs` define the Scatter/native partition/eFuse/resource and Penumbra2 extension ABI behavior. The historical `ce13391` above is not the archive's asserted revision. File fingerprints (SHA256) identify the inspected inputs:

| Archive path | SHA256 |
| --- | --- |
| `Cargo.lock` | `BF661EB3C6AC20F62059751168DF2F3A8F195A235243FFDDD96CADC1C3ED5806` |
| `core/src/da/scatter.rs` | `D789C65C764DAC24D29BE4682A03F6EDEC4462E530495EDECCB8039CC541ECE4` |
| `core/src/da/xflash/exts.rs` | `0CD543F08A394E13D7CEB4223C5D2793F7BA6290EBE9E21E28BCEAA7A6A1BF1D` |
| `core/src/da/xml/exts.rs` | `E52BEBD172340109D59A62288F19B999B8916ACF88D3E2D0A8EFD64B1C8491E6` |
| `core/src/da/xflash/protocol.rs` | `CB17E5926F4D0483DC7DABA44792AAD23BD73DA6C02A316783EE518E3AC84E23` |
| `core/src/da/xml/protocol.rs` | `B9ED36BD0F956A26DB249EA19587A66457C77F3B98D50CE28FC0D1F1CE9F5408` |
| `core/src/macros.rs` | `1100C6949EC249F60DE6EEF3CF4FAFE59969928BED5005DC40A721AF0A718522` |

The updated protocol/macros files were inspected for host callback placement and ordinary authentication order only.
No concrete exploit, automatic strategy construction, exploit error suppression, or assertion of patched/authenticated state was ported.

Additional normal mtkclient references are `Library/DA/legacy/dalegacy_lib.py` (NAND DF and IoT DA3/CDC) and `Library/Hardware/hwcrypto_{sej,gcpu,dxcc}.py` (standard register sequences, bounded rewrites of polling, public image-format constants and normal derivation). DAPC/security disable, DMA protection bypass, blacklist removal, firmware/DA modifications and payload injection were excluded. Explicit profiles replace hardcoded DMA/clock addresses.

XML/UFS RPMB `SecRpmbInfo` layout was checked against [hacc source at e5a68124](https://github.com/shomykohai/hacc/blob/e5a68124e7d795465804aaf2d11cef43cf8b7267/src/common/rpmb.rs), the exact dependency pinned in the archive's Cargo.lock. Attribution: Copyright (c) 2026-present Shomy, rva3, MIT; license text is retained in `licenses/MTK-HACC-MIT.txt`. Only layout/enum facts and normal host communication are referenced; device-side code, keys and payloads are not distributed.

## Embedded payload resources (2026-10-06)

At the user's explicit request, all seven .bin files from `D:/Code/Rust/penumbra-main/core/payloads` were copied unchanged to `src/GeekFlashCore.Protocol.Mtk/Exploits/Resources/Payloads` and embedded in the protocol assembly. Their lengths and SHA-256 fingerprints are in [dependencies.json](src/GeekFlashCore.Protocol.Mtk/Exploits/Resources/dependencies.json). The archive has no Git metadata; neither the historical revisions above nor the separately inspected local mtk-payloads HEAD `aa045df` establish the corresponding build revision of these binaries.

The archive identifies [shomykohai/mtk-payloads](https://github.com/shomykohai/mtk-payloads) as their source project. Upstream per-file notices identify AGPL-3.0-or-later, Copyright (c) 2025–2026 Shomy for brom_defuse, extloader, SLA XML and DA extensions, and Copyright (c) 2026 Shomy, R0rt1z2 for hakujoudai. The full AGPLv3 text is retained in [licenses/MTK-PAYLOADS-AGPL-3.0.txt](licenses/MTK-PAYLOADS-AGPL-3.0.txt). Upstream DA extension notices also credit GPLv3 libsej, Copyright (c) 2024 B.Kerler, 2025 Shomy; the GPLv3 text is in `licenses/MTK-GPL-3.0.txt`. Additional component notices (MIT, nanoprintf 0BSD/Unlicense, musl-derived libc and public-domain SHA-256) are retained in [licenses/MTK-PAYLOAD-NOTICES.txt](licenses/MTK-PAYLOAD-NOTICES.txt).

This copy only adds explicit offline resource access and integrity metadata. No parameter filling, resource modification, DA patching, target addressing, upload, jump or exploit execution is implemented by the resource layer. The four strategy Execute methods remain nonexecuting placeholders; defaults remain empty. See [resource design](docs/plans/2026-10-06-mtk-embedded-payload-resources-design.md) and [verification record](docs/plans/2026-10-06-mtk-embedded-payload-resources-implementation.md).

## Generic offline architecture analysis (2026-10-06)

At the user's request, `core/src/utils/analysis/{mod,arm,aarch64,thumb}.rs` from the same Penumbra archive is adapted into C# under `GeekFlashCore.Protocol.Mtk.Analysis`. Attribution: Copyright (c) 2025–2026 Shomy (thumb: 2026), AGPL-3.0-or-later; per-file notices remain on the adapted analyzer files. Full AGPLv3 text is in `LICENSE`. Exact inspected file SHA-256 fingerprints and method mappings are in the [analysis implementation record](docs/plans/2026-10-06-mtk-architecture-analysis-implementation.md).

This addition implements generic caller-selected offline analysis with bounded source windows, cancellation and explicit stream ownership. It does not copy exploit-specific symbol searches, DA transforms or device execution. The original whole-buffer ownership is replaced with a borrowed IDataSource and fixed cache, and documented boundary/branch corrections are applied. LLVM was used only to assemble/disassemble synthetic test instructions; it is not a runtime dependency and its binaries are not distributed.
