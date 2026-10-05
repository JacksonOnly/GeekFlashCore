// SPDX-License-Identifier: AGPL-3.0-or-later
// Standard chip metadata: B. Kerler, mtkclient/config/brom_config.py and mtk_config.py, GPLv3.
namespace GeekFlashCore.Protocol.Mtk;

/// <summary>Known standard metadata, excluding exploit addresses and payloads.</summary>
public sealed record MtkChipDescriptor(string Name, string? Description, ushort DaHardwareCode, MtkChipProfile? Watchdog);

/// <summary>Reference chip names, DA aliases and explicitly known ordinary watchdog registers.</summary>
public static class MtkChipCatalog
{
    private static readonly IReadOnlyDictionary<ushort, MtkChipDescriptor> Chips = new Dictionary<ushort, MtkChipDescriptor>
    {
        [0x571] = new("MT0571", null, 0x571, new(0x571, 0x10007000, 0x22000064)),
        [0x598] = new("ELBRUS/MT0598", null, 0x598, new(0x598, 0x10211000, 0x22000064)),
        [0x992] = new("MT6880/MT6890", null, 0x992, new(0x992, 0x10007000, 0x22000064)),
        [0x2601] = new("MT2601", null, 0x2601, new(0x2601, 0x10007000, 0x22000064)),
        [0x3967] = new("MT3967", null, 0x3967, null),
        [0x6255] = new("MT6255", null, 0x6255, null),
        [0x6261] = new("MT6261", null, 0x6261, null),
        [0x6280] = new("MT6280", null, 0x6280, null),
        [0x6516] = new("MT6516", null, 0x6516, new(0x6516, 0x10003000, 0x22000064)),
        [0x633] = new("MT6570/MT8321", null, 0x6570, new(0x633, 0x10007000, 0x22000064)),
        [0x6571] = new("MT6571", null, 0x6571, new(0x6571, 0x10007400, 0x22000000)),
        [0x6572] = new("MT6572", null, 0x6572, new(0x6572, 0x10007000, 0x22000064)),
        [0x6573] = new("MT6573/MT6260", null, 0x6573, new(0x6573, 0x70025000, 0x22000064)),
        [0x6575] = new("MT6575/MT6577/MT8317", null, 0x6575, new(0x6575, 0xc0000000, 0x2264, WatchdogWidth: 16)),
        [0x6577] = new("MT6577", null, 0x6577, new(0x6577, 0xc0000000, 0x2264, WatchdogWidth: 16)),
        [0x6580] = new("MT6580", null, 0x6580, new(0x6580, 0x10007000, 0x22000064)),
        [0x6582] = new("MT6582/MT6574/MT8382", null, 0x6582, new(0x6582, 0x10007000, 0x22000064)),
        [0x6583] = new("MT6583/6589", null, 0x6589, new(0x6583, 0x10000000, 0x22000064)),
        [0x6592] = new("MT6592/MT8392", null, 0x6592, null),
        [0x6595] = new("MT6595", null, 0x6595, new(0x6595, 0x10007000, 0x22000064)),
        [0x321] = new("MT6735/T,MT8735A", null, 0x6735, new(0x321, 0x10212000, 0x22000000)),
        [0x335] = new("MT6737M/MT6735G", null, 0x6735, new(0x335, 0x10212000, 0x22000000)),
        [0x699] = new("MT6739/MT6731/MT8765", null, 0x6739, new(0x699, 0x10007000, 0x22000064)),
        [0x601] = new("MT6750", null, 0x6755, new(0x601, 0x10007000, 0x22000064)),
        [0x6752] = new("MT6752", null, 0x6752, new(0x6752, 0x10007000, 0x22000064)),
        [0x337] = new("MT6753", null, 0x6735, new(0x337, 0x10212000, 0x22000000)),
        [0x326] = new("MT6755/MT6750/M/T/S", "Helio P10/P15/P18", 0x6755, new(0x326, 0x10007000, 0x22000064)),
        [0x551] = new("MT6757/MT6757D", "Helio P20", 0x6757, new(0x551, 0x10007000, 0x22000064)),
        [0x688] = new("MT6758", "Helio P30", 0x6758, new(0x688, 0x10211000, 0x22000064)),
        [0x507] = new("MT6759", "Helio P30", 0x6758, new(0x507, 0x10210000, 0x22000064)),
        [0x717] = new("MT6761/MT6762/MT3369/MT8766B", "Helio A20/P22/A22/A25/G25", 0x6761, new(0x717, 0x10007000, 0x22000064)),
        [0x690] = new("MT6763", "Helio P23", 0x6763, new(0x690, 0x10007000, 0x22000064)),
        [0x766] = new("MT6765/MT8768t", "Helio P35/G35", 0x6765, new(0x766, 0x10007000, 0x22000064)),
        [0x707] = new("MT6768/MT6769", "Helio P65/G85 k68v1", 0x6768, new(0x707, 0x10007000, 0x22000064)),
        [0x788] = new("MT6771/MT8385/MT8183/MT8666", "Helio P60/P70/G80", 0x6771, new(0x788, 0x10007000, 0x22000064)),
        [0x725] = new("MT6779", "Helio P90 k79v1", 0x6779, new(0x725, 0x10007000, 0x22000064)),
        [0x1066] = new("MT6781", "Helio G96", 0x6781, new(0x1066, 0x10007000, 0x22000064)),
        [0x813] = new("MT6785", "Helio G90", 0x6785, new(0x813, 0x10007000, 0x22000064)),
        [0x6795] = new("MT6795", "Helio X10", 0x6795, new(0x6795, 0x10007000, 0x22000064)),
        [0x279] = new("MT6797/MT6767", "Helio X23/X25/X27", 0x6797, new(0x279, 0x10007000, 0x22000064)),
        [0x562] = new("MT6799", "Helio X30/X35", 0x6799, new(0x562, 0x10211000, 0x22000064)),
        [0x989] = new("MT6833", "Dimensity 700 5G k6833", 0x6833, new(0x989, 0x10007000, 0x22000064)),
        [0x996] = new("MT6853", "Dimensity 720 5G", 0x6853, new(0x996, 0x10007000, 0x22000064)),
        [0x886] = new("MT6873", "Dimensity 800/820 5G", 0x6873, new(0x886, 0x10007000, 0x22000064)),
        [0x959] = new("MT6877/MT6877V", "Dimensity 900/1080", 0x6877, new(0x959, 0x10007000, 0x22000064)),
        [0x816] = new("MT6885/MT6883/MT6889/MT6880/MT6890", "Dimensity 1000L/1000", 0x6885, new(0x816, 0x10007000, 0x22000064)),
        [0x950] = new("MT6893", "Dimensity 1200", 0x6893, new(0x950, 0x10007000, 0x22000064)),
        [0x907] = new("MT6983", "Dimensity 9000/9000+", 0x907, new(0x907, 0x1c007000, 0x22000064)),
        [0x1129] = new("MT6855", "Dimensity 8100", 0x1129, new(0x1129, 0x1c007000, 0x22000064)),
        [0x1172] = new("MT6895", "Dimensity 8200", 0x1172, new(0x1172, 0x1c007000, 0x22000064)),
        [0x1203] = new("MT6897", "Dimensity 8300 Ultra", 0x1203, new(0x1203, 0x1c007000, 0x22000064)),
        [0x1208] = new("MT6789/MT8781V", "MTK Helio G99", 0x1208, new(0x1208, 0x10007000, 0x22000064)),
        [0x1229] = new("MT6886", "Dimensity 7200 Ultra", 0x1229, new(0x1229, 0x1c007000, 0x22000064)),
        [0x1296] = new("MT6985", "Dimensity 9200/9200+", 0x1296, new(0x1296, 0x1c007000, 0x22000064)),
        [0x8127] = new("MT8127/MT3367", null, 0x8127, new(0x8127, 0x10007000, 0x22000064)),
        [0x8135] = new("MT8135", null, 0x8135, new(0x8135, 0x10000000, 0x22000064)),
        [0x8163] = new("MT8163", null, 0x8163, new(0x8163, 0x10007000, 0x22000064)),
        [0x8167] = new("MT8167/MT8516/MT8362", null, 0x8167, new(0x8167, 0x10007000, 0x22000064)),
        [0x8168] = new("MT8168/MT6357", null, 0x8168, new(0x8168, 0x10007000, 0x22000064)),
        [0x8172] = new("MT8173", null, 0x8173, new(0x8172, 0x10007000, 0x22000064)),
        [0x8176] = new("MT8176", null, 0x8173, new(0x8176, 0x10007000, 0x22000064)),
        [0x930] = new("MT8195 Chromebook", null, 0x8195, new(0x930, 0x10007000, 0x22000064)),
        [0x8512] = new("MT8512", null, 0x8512, new(0x8512, 0x10007000, 0x22000064)),
        [0x8518] = new("MT8518 VoiceAssistant", null, 0x8518, null),
        [0x8590] = new("MT8590/MT7683/MT8521/MT7623", null, 0x8590, new(0x8590, 0x10007000, 0x22000064)),
        [0x8695] = new("MT8695", null, 0x8695, new(0x8695, 0x10007000, 0x22000064)),
        [0x908] = new("MT8696", null, 0x8696, new(0x908, 0x10007000, 0x22000064)),
    };

    /// <summary>Returns null for unknown hardware; no register address is guessed.</summary>
    public static MtkChipDescriptor? Find(ushort hardwareCode) => Chips.GetValueOrDefault(hardwareCode);

    /// <summary>Resolves an explicit override, the probe snapshot, known metadata, then the raw hardware code.</summary>
    public static ushort GetDaHardwareCode(MtkTargetInfo target, ushort? explicitAlias = null) =>
        explicitAlias ?? target.DaHardwareCode ?? Find(target.HardwareCode)?.DaHardwareCode ?? target.HardwareCode;
}
