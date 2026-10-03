using System.Text.RegularExpressions;
using GeekFlashCore.Protocol.Qcom.Abstractions;

namespace GeekFlashCore.Protocol.Qcom.Vendors.Oplus;

/// <summary>Reference chip templates from GeekFlashTool Module/Oppo.cs; a rejection requires a manual Sign.</summary>
internal static partial class OplusSignatures
{
    private static readonly IReadOnlyDictionary<string, string> Templates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
["SM6115"] = "HNtp3eu9xznsQyYmHwwTQR14t0ccePbvwK77z3SCG8PwFK4XUwzF7fhaddBJ1IQv69Ygj+oWsG+qfWwrfs8WzUiGgiEYJDJlOye+ns0FzOSgvgKOSXzbRNROE1Bdf+eW1Vcj4Qxy8CdcPWczo9xFyHQzoCbNAtgz3rq8Cpot3Ooo0R6FBl0ya9lppTSnGm6+G17iai4iewj7S0ghlk9lN2f9V1abfhxcbT0FEX3Cqhk45Rim+cx57YOgyRU40Q4Ygy7Fsg25uckMG74elsZijWSWQEeYsdWy4q6Swypyx7gZhRaZH8j9vhEES2AOHrx3/Q32Ti42oVh5hU9mgxG29Q==",
        ["SM6375"] = "J8oYBhnA+uQ/CazAiKbk/bnU6hcNV5P674IT4RyVD7gxa5RjeUH62+AtLHQam9PpXygFEubKA/2LbYl5Byx5+xuX3zqR0LdZt8aRdW78HiDs2rq1oJDa7Lx1eJy3z37DzSpj1qKLbJWpSJNxMqvkN8GlbPGP2maiF/6zdRMPhHLA1ixVS2vIQ4MDzB4ScFhKgMrewalYXziaySSm+Cq9l+c3+Yw6nKtxRNLAV+GK4OY4vaWZgaJiDBPbA9P0fSDYdjBX68Iteakm/cKHXagSpSYKYPn/0HaQ7fv3w0tZAF4DCdOYCyqBedcqZzAwl5UQ8OOaOAwEKC01KMmC6it0zg==",
        ["SM7675"] = "QC6v8WH3Gr0OM0/PIZaqOQA3uqVAhL0Wuk0p1p14uP7bW2WumxPIiTdHurJfEEnAA4LCBc+9qkZHF8UWNrRXGADvM6K509fKEY/JAwVLmFdlvX9dWfMaNAhDuSpbXzp9OHO8cF77r8lLvTwmH99kZ0eaQApec3SnezMdM4qLuwtcs0yFN7/6dPpM2I6YwR+kVbrHQUzmwXeKU/5gs2/11KQJJYTYwqnYIj6XYuCljY5OMeTvcME9+SE1uxp0vDW28H6O53whFm2WWzbjvunWVW9Lct8FHYLh5U8nLh17xxG+uCB3SJdlI+E/560gas+QNiSPK4/GbtKAvDUTpudm9A==",
        ["SM7475"] = "Yis8kbj/ByL+o4Jms5m7Zndn/b0ozb5aMljCQ3iu6jhWci4/lCJtSsWSW0wc2J3yWDUquUzda2Hpz9oBbkUhkITaQh1AeMxGwngA4fsQm+s+wwM7d+Tl+LZr+rU4uxAY3wFl3P5gTbmm61pJc0kkYimx01LR9GIAL732LwByg+KjcQKrHxeXzekQRX7dPSgzRiHRFXRRjVXykEEkBIcyh1HAkn1WvJEDc/oFaubaywVCVfp3VtNCk0qlabxqVWTlPTfQI2r8Ulh0xqSFgnFGMJc7PVxIph1kItLxTO+KpwCLoDfbv4jRWflX54OgOHROipzcJIljlWvd/Dfv1ivaeQ==",
        ["SM8350"] = "c1Lx3XE4MuKDE0aipRwbno+LL9WwuHPmhMloH/K7aZatRO2DlJYDUwGX0t97b0hKrvXPeQsEcEBiTa+nymoxh9LlShg24B5KYqKS+gkvmfgZJz/lIbEXYT/Ok+rFAgYbFOHR72XKJATI7Cer9mvkQs3n1QIa4GtvhG8nR6NepHO30viiwvPHm6SYhDaF3zCSMFMmrCbhEQEPPAl3X4/uF/3C9GtmPv/Vh3qqrgJ30bfnzPVcwNfuwh8D+AQf+w+bkz7ErT1LT11RYyO0dJFX705wCJ13qBO+Gh72zVuhVJn31nm3pf9wos9XDwiSmP8ygotxghdSTK+Yfjc0c7YQ+Q==",
        ["SM8450"] = "eCrRMUyKS7Yaepf245d4JA40mqeF+vgeaNOX+PZYv+fGAZavjJGTh0a0Rhb4UwEuAL0EsWruZHWIRz4VJeFNLc+YQHjATCjxv8RwXyCnjxNfl5kMniXKWs9XyOKdrtJBV66AyrgtzHFrLTfj5R/iPMfqe3PNF1oqpBueDsh+8yJjeUx+yJxfMriB+dK5qRHLqhxyBFeWQzYyvO2bcBEuGhuJvyuAhjven8WrnN9DhGSSWkyRZBw5bwPelIqj1PWPV3aFzLCOHRh5gk0Vzec2P0nqtfh41tS+8mtMW/uWDt0HZW4U6Bb0cV1t2L4ccvgQj2CJ4xSIFw5WlRV8HKRtuw==",
        ["SM8475"] = "CMaJ9a4UQhGVhe+CR4dLRb+vI0jr+7tjEmlokfzqzXyb6kZBgtLAy6zHgglMwm5SjqpuJSWhrnElC6VXkWrwdwq+t8UKcRT9THRu2Shj4n/AMhP1rSUk2SfoD40EfsTe5ls5eSg1gOpNEkQ7e2nY/7Dm1LuRKTbNasNL4ik6LNf+/esGJuGoExbiy6a6nszDDYqBMQKpXuJs6zSR0FdBxp6Z0hj5XxGzKZ3kSaIU61SEQmc45XCQESO9lNdQx6dWlygyBpOzp+EPRvYNLQcvNWs4TWTeTs7h+umMPRF1kQFOpTiGsepQhikko9bIXq776QwbvcDJo18N1yn9BBW5ng==",
        ["SM8550"] = "Tz+sqidQLMM8gipLdvh0tgucs2RnuxEULg+3L0IfNV59hvwkeKb0ww/grqNMp4rw21al/7sXPsnV0qIkA3F5eePuNCYx8C0wlF/K9BRAwJRPiLz7XCvrnowtKNzRxhVrJkBmlBt145CiIV2TOaQr/xCceBVf55xJWxMbjR2HJOdkjhQGg3GocL4flnOq5qEetF99wGvJblLYVd19GvwfIagDa4K2w4/mY/W8H6t8h/RoixKTqpORtRegOUGEImjap++zEjBFxOT54hzOVTVHxeh4v6NI7Y8PVZIQ63XoMIRKnX0o0AIwAnCXkdcBRuAVjBezcckDu9wJpTNdmCc4Bw==",
        ["SM8650"] = "CnF48xNW5IACRez75T3Y3rj8uSz93hecnscK4WR2MTy8Aq4L4IvkuGJoLQ6HzcHfQEuJSbkIXNFynMWvDk2ERDL8VisKlLLYDKLt60Qpa7GZP5gBCaRoN54iA0ivpQ4PgPz/ODts+MK/p8LGSRKflO8O4/G3cikIvvOjfYjP5WExwTi7jmmm5V/ODHCzWcv36hnNySrO0ERprmsvD5Fz+vpWcCSNcQKPZT4J9cLelsP2bwjBBCacLSFl0IbaYbB3OGFlvXAo0JEl5eCdSxLPYr0XgG5WHi5KM9zsQTvWitH5e5KlrTLrToUMk06Sxn/MaCtaa5Hz1GgXgObwDt83gg==",
        ["SM8750"] = "nvZ3f2U5TLBolJp/yhC7+hipVOT4ZPkrnKUHXV83h9hdw32ewndQux1bL/9vbUbKYnnMoki6OUDsM28gZ84yM74sbrxpQSb9qJlTdnH2CGa3Vbdvfhue+qA/FI0kJlNDHdC+P4nRT9XT7Em/T6wRDCqs2bPLl/vzC8DGZCfKX0dUNbzLvzsuqMUFa3ine/XIhfd1Gz98zm7kYOBlnEvV0cGgt5ngKESZXgjxpcDNGG/NxTMzLPauOYcF0fhv9Wn5AOjPLKFNS/2Hsr95V766dhaPFFUeaQWO9jU8NhT2G82AMCWoWOG1sueMFDon0Qd2ELHIqqShwed2i1XRetecuA==",
    };

    internal static string? Find(QcomTargetInfo target, IEnumerable<string>? startupLogs)
    {
        IEnumerable<string> candidates = new[] { target.SocName ?? string.Empty }
            .Concat(startupLogs ?? []);
        foreach (string candidate in candidates)
        {
            Match match = ChipName().Match(candidate);
            if (match.Success && Templates.TryGetValue(match.Value, out string? sign)) return sign;
        }
        return null;
    }

    [GeneratedRegex(@"\bSM\d{4}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChipName();
}
