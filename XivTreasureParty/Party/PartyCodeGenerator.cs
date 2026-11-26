using System;
using System.Security.Cryptography;
using XivTreasureParty.Firebase;

namespace XivTreasureParty.Party;

public static class PartyCodeGenerator
{
    private const string CodeChars = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    public const int CodeLength = 8;

    /// <summary>臨時伺服器的隊伍代碼長度；和 cycleapple 的 8 碼不同，貼錯後端時才能明確指出來。</summary>
    public const int CustomCodeLength = 10;

    public static int LengthFor(BackendKind kind) => kind == BackendKind.Custom ? CustomCodeLength : CodeLength;

    public static string Generate() => Generate(CodeLength);

    public static string Generate(int length)
    {
        Span<byte> bytes = stackalloc byte[length];
        RandomNumberGenerator.Fill(bytes);
        Span<char> chars = stackalloc char[length];
        for (var i = 0; i < length; i++)
            chars[i] = CodeChars[bytes[i] % CodeChars.Length];
        return new string(chars);
    }

    public static bool IsValidFormat(string? code) => IsValidFormat(code, CodeLength);

    public static bool IsValidFormat(string? code, int length)
    {
        if (string.IsNullOrEmpty(code) || code.Length != length) return false;
        foreach (var c in code.ToUpperInvariant())
        {
            if (!IsAllowedChar(c)) return false;
        }
        return true;
    }

    private static bool IsAllowedChar(char c)
    {
        if (c is >= 'A' and <= 'Z') return c is not ('I' or 'L' or 'O');
        if (c is >= '2' and <= '9') return true;
        return false;
    }
}
