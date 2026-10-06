// SPDX-License-Identifier: AGPL-3.0-or-later
// Based on Penumbra utils/analysis, Copyright (c) 2025-2026 Shomy.
namespace GeekFlashCore.Protocol.Mtk.Analysis;

/// <summary>Explicit instruction architecture; selecting it does not inspect or execute a binary.</summary>
public enum Arch { Arm, Aarch64, Thumb2 }

/// <summary>Properties of explicitly selected architectures.</summary>
public static class ArchExtensions
{
    /// <summary>Returns whether the selected architecture is AArch64. Unknown values are rejected.</summary>
    public static bool IsArm64(this Arch arch) => Enum.IsDefined(arch)
        ? arch == Arch.Aarch64 : throw new ArgumentOutOfRangeException(nameof(arch));
}
