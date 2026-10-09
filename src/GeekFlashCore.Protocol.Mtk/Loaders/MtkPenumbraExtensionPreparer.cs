// SPDX-License-Identifier: AGPL-3.0-or-later
// Extension pointer tables derived from Penumbra xflash/exts.rs and xml/exts.rs (Shomy 2025-2026).
using System.Text;
using GeekFlashCore.Protocol.Mtk.Analysis;
using GeekFlashCore.Protocol.Mtk.Exploits;
using GeekFlashCore.Protocol.Mtk.Exploits.Penumbra;

namespace GeekFlashCore.Protocol.Mtk.Loaders;

/// <summary>Offline preparation of the bundled Penumbra extension for an already loader-enabled DA.</summary>
public static class MtkPenumbraExtensionPreparer
{
    /// <summary>Returns owned prepared bytes, or null when loader/symbol evidence is incomplete. Performs no device I/O.</summary>
    public static byte[]? Prepare(MtkDaImage image, uint uartBase, MtkStorageKind storage,
        MtkExploitResourceStore resources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image); ArgumentNullException.ThrowIfNull(resources);
        cancellationToken.ThrowIfCancellationRequested();
        if (image.Entry?.Regions is not { } regions || !Enum.IsDefined(image.Entry.Kind) || image.Source is null || image.Entry.EntryRegionIndex >= regions.Count - 1)
            throw new MtkResourceException("DA extension metadata");
        if (uartBase == 0 || image.Entry.Kind == MtkDaKind.Legacy || storage is not (MtkStorageKind.Emmc or MtkStorageKind.Ufs)) return null;
        var da1 = image.Entry.Regions[image.Entry.EntryRegionIndex];
        var da2 = image.Entry.Regions[image.Entry.EntryRegionIndex + 1];
        if (da1.Length < 4 || da2.SignatureLength >= da2.Length) throw new MtkResourceException("DA extension region");
        var source = new MtkDataWindow(image.Source, da2.FileOffset, da2.Length - da2.SignatureLength);
        Arch arch = Arch.Thumb2;
        if (image.Entry.Kind == MtkDaKind.Xml)
        {
            Span<byte> first = stackalloc byte[4];
            using var stream = new MtkDataWindow(image.Source, da1.FileOffset, da1.Length).OpenStream(); stream.ReadExactly(first);
            arch = PenumbraDaMetadata.DetectArch(first);
        }
        byte[] loader = Read(resources, image.Entry.Kind == MtkDaKind.Xml ? MtkExploitResourceKind.XmlExtensionLoader : MtkExploitResourceKind.XFlashExtensionLoader);
        if (image.Entry.Kind == MtkDaKind.Xml) loader = PenumbraPayloadFormat.GetV6Payload(loader, arch == Arch.Aarch64).ToArray();
        if (loader.Length > 4096 || MtkExploitBinaryTools.FindPattern(source, loader, cancellationToken) is null) return null;
        var analyzer = new Analyzer(arch, source, da2.Address);
        long? Function(string text) => Unique(text) ? analyzer.FindFunctionFromString(text, cancellationToken) : null;
        long? Reference(string text) => Unique(text) ? analyzer.FindStringReference(text, cancellationToken) : null;
        bool Unique(string text)
        {
            byte[] pattern = Encoding.UTF8.GetBytes(text);
            long? first = MtkExploitBinaryTools.FindPattern(source, pattern, cancellationToken);
            if (first is null) return false;
            long start = first.Value + pattern.Length;
            return start == source.Length || MtkExploitBinaryTools.FindPattern(new MtkDataWindow(source, start, source.Length - start), pattern, cancellationToken) is null;
        }
        long? Next(long? offset, int skip = 0) => offset is >= 0 ? analyzer.NextBranchLinkFromOffset(checked(offset.Value + skip), cancellationToken) : null;
        uint? Address(long? call)
        {
            ulong? address = call is { } at ? analyzer.BranchLinkTarget(at, cancellationToken) : null;
            if (address is not { } value || value > uint.MaxValue || analyzer.VirtualAddressToOffset(value) is null) return null;
            return checked((uint)value) | (arch == Arch.Thumb2 ? 1u : 0u);
        }
        uint? FunctionAddress(long? offset) => offset is { } at && analyzer.OffsetToVirtualAddress(at) is { } address && address <= uint.MaxValue
            ? (uint)address | (arch == Arch.Thumb2 ? 1u : 0u) : null;
        long? Offset(uint? address) => address is { } value ? analyzer.VirtualAddressToOffset(value) : null;
        uint? register, malloc, free, mmc, clear = null, set = null;
        if (image.Entry.Kind == MtkDaKind.XFlash)
        {
            free = FunctionAddress(Function("allocation was %zd bytes long at ptr %p\n"));
            uint? kernel = Address(Next(Reference("\n***10.dagent_register_commands.\n"), 6));
            register = Address(Next(Offset(kernel)));
            malloc = Address(Next(Offset(register)));
            mmc = Address(Next(Function("%s, mmc_set_part_config done!!\n")));
        }
        else
        {
            register = Address(Next(Function("CMD:REBOOT")));
            malloc = Address(Next(Offset(register)));
            free = Address(Next(Next(Reference("Bad %s")), 4));
            mmc = Address(Next(Function("mmc_switch_part")));
            long? unsupported = Reference("Unsupported command.");
            set = Address(Next(unsupported));
            clear = Address(Next(unsupported is >= 16 ? unsupported - 16 : null));
        }
        if (register is null || malloc is null || free is null ||
            (image.Entry.Kind == MtkDaKind.XFlash || storage == MtkStorageKind.Emmc) && mmc is null ||
            image.Entry.Kind == MtkDaKind.Xml && (clear is null || set is null)) return null;
        byte[] bytes = Read(resources, image.Entry.Kind == MtkDaKind.Xml ? MtkExploitResourceKind.XmlExtension : MtkExploitResourceKind.XFlashExtension);
        if (image.Entry.Kind == MtkDaKind.Xml) bytes = PenumbraPayloadFormat.GetV6Payload(bytes, arch == Arch.Aarch64).ToArray();
        uint[] fields = image.Entry.Kind == MtkDaKind.XFlash
            ? [0x54525450, uartBase, register.Value, malloc.Value, free.Value, mmc!.Value]
            : [0x54525450, uartBase, register.Value, clear!.Value, set!.Value, malloc.Value, free.Value, mmc ?? 0];
        int table = checked(bytes.Length - fields.Length * 4);
        if (table < 0 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(table)) != 0x54525450) return null;
        for (int i = 0; i < fields.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(table + i * 4), fields[i]);
        cancellationToken.ThrowIfCancellationRequested();
        return bytes;
    }
    private static byte[] Read(MtkExploitResourceStore resources, MtkExploitResourceKind kind)
    {
        using Stream stream = resources.OpenRead(kind);
        if (stream.Length is <= 0 or > 1048576) throw new MtkResourceException("extension resource length");
        byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }
}
