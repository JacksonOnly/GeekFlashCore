# MediaTek implementation provenance

GeekFlashCore's license remains the GNU Affero General Public License v3 in `LICENSE`. The MTK implementation is a bounded synchronous C# rewrite, with the following protocol/layout references and original attribution retained. No reference binaries, private keys, authentication services or exploit implementation are distributed.

| Reference | Inspected revision | Original attribution / license | Referenced behavior |
| --- | --- | --- | --- |
| bkerler/mtkclient | `e9fcf97` | B. Kerler, 2018–2024, GPLv3 | BROM echo/status/checksum, D8/DC DA metadata, EMI, Legacy/XFlash, seccfg layouts and public software AES format; pre-DA and post-authentication host checkpoints |
| Shomy/penumbra | `ce13391` | Shomy, 2025–2026, AGPL-3.0-or-later | XFlash framing and command order, XML command/file lifetimes, extension ACK/context, memory/register/SEJ/RPMB host ABI; strategy metadata, returned DA and stage placement only |
| Shomy/mtk-payloads | `e34d980` | Per-file notices: Shomy, 2025–2026; AGPL/GPL | Existing DA extension context and command ABI only; device-side code and payloads are not copied |
| GeekFlashTool.MtkClient | `67ced05` | Existing project notices remain with that project | Cross-check of FC big-endian fields, old DA layout and XML command behavior; application/provider/account code is not copied |

Relevant mtkclient paths: `Library/mtk_preloader.py`, `config/brom_config.py`, `config/mtk_config.py` (standard chip names, DA aliases and watchdog registers only), `Library/DA/mtk_da_handler.py::configure_da`, `Library/DA/daconfig.py`, `Library/DA/legacy/dalegacy_lib.py`, `Library/DA/legacy/dalegacy_flash_param.py`, `Library/DA/xflash/xflash_lib.py`, `Library/DA/xml/xml_lib.py`, `Library/Hardware/seccfg.py`, `Library/Hardware/hwcrypto_sej.py`.

Relevant penumbra paths: `core/src/da/dafile.rs`, `core/src/da/xflash/{cmds,xflash_lib,da_protocol,exts}.rs`, `core/src/da/xml/{cmds,xml_lib,da_protocol,flash,storage,exts}.rs`, and the framework declarations/call sites in `core/src/exploit/mod.rs` and `core/src/macros.rs`. No concrete strategy or DA modification algorithms are copied. Relevant payload paths are `da_x`, `da_xml` and `libsej` ABI definitions. The corresponding C# files retain source notices. The GPLv3 text is included in `licenses/MTK-GPL-3.0.txt`.

Some inspected reference working trees already contained local changes. Revisions identify their bases, not an assertion that every inspected file matched its commit. Reference working trees were read only and were not modified.

The 2026-10-05 standard completion also references penumbra `core/src/core/bootctrl.rs` (Shomy, 2026, AGPL-3.0-or-later) for Android boot-control fields and CRC, and mtkclient `Library/DA/{legacy/dalegacy_flash_param,xflash/xflash_lib,xml/xml_cmd,xml/xml_lib}.py` for standard read-only queries, PMT/XML partition metadata, register commands and NAND/NOR/SDMMC geometry. DA `m_start_offset` metadata is preserved as a length/signature boundary, not added to `m_start_addr`. No exploitation algorithms or device-side payloads were added. The current local penumbra copy has no Git metadata; its prior inspected revision above is historical provenance, not a newly verified file revision.

Changes from references include bounded counts/lengths/XML, explicit endianness and final status checks, serialized generations, borrowed stream ownership, late authentication cleanup, streaming Raw/Sparse, strict extension prerequisites and verified seccfg planning/readback. Dummy signatures, timeout-as-success, silent checksum failures, arbitrary XML and automatic retries after unknown writes are excluded.

## Updated normal-function references (2026-10-05)

The current primary reference is the user's `D:\Code\Rust\penumbra-main` archive, without Git metadata. Its normal `core/src/da/{protocol,scatter,types}.rs`, `core/src/da/{xflash,xml}/{protocol,flash,cmd,exts}.rs` and `tui/src/cli/commands/device/keys.rs` define the Scatter/native partition/eFuse/resource and Penumbra2 extension ABI behavior. The historical `ce13391` above is not the archive's asserted revision. File fingerprints (SHA256) identify the inspected inputs:

| Archive path | SHA256 |
| --- | --- |
| `Cargo.lock` | `BF661EB3C6AC20F62059751168DF2F3A8F195A235243FFDDD96CADC1C3ED5806` |
| `core/src/da/scatter.rs` | `D789C65C764DAC24D29BE4682A03F6EDEC4462E530495EDECCB8039CC541ECE4` |
| `core/src/da/xflash/exts.rs` | `0CD543F08A394E13D7CEB4223C5D2793F7BA6290EBE9E21E28BCEAA7A6A1BF1D` |
| `core/src/da/xml/exts.rs` | `E52BEBD172340109D59A62288F19B999B8916ACF88D3E2D0A8EFD64B1C8491E6` |

Additional normal mtkclient references are `Library/DA/legacy/dalegacy_lib.py` (NAND DF and IoT DA3/CDC) and `Library/Hardware/hwcrypto_{sej,gcpu,dxcc}.py` (standard register sequences, bounded rewrites of polling, public image-format constants and normal derivation). DAPC/security disable, DMA protection bypass, blacklist removal, firmware/DA modifications and payload injection were excluded. Explicit profiles replace hardcoded DMA/clock addresses.

XML/UFS RPMB `SecRpmbInfo` layout was checked against [hacc source at e5a68124](https://github.com/shomykohai/hacc/blob/e5a68124e7d795465804aaf2d11cef43cf8b7267/src/common/rpmb.rs), the exact dependency pinned in the archive's Cargo.lock. Attribution: Copyright (c) 2026-present Shomy, rva3, MIT; license text is retained in `licenses/MTK-HACC-MIT.txt`. Only layout/enum facts and normal host communication are referenced; device-side code, keys and payloads are not distributed.
