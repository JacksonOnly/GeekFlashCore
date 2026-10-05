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

Changes from references include bounded counts/lengths/XML, explicit endianness and final status checks, serialized generations, borrowed stream ownership, late authentication cleanup, streaming Raw/Sparse, strict extension prerequisites and verified seccfg planning/readback. Dummy signatures, timeout-as-success, silent checksum failures, arbitrary XML and automatic retries after unknown writes are excluded.
