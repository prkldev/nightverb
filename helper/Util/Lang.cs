using System.Runtime.InteropServices;

namespace Nightverb.Util;

/// <summary>Russian if Windows' display language is Russian, English otherwise.</summary>
internal static class Lang
{
    [DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    public static readonly bool Russian = (GetUserDefaultUILanguage() & 0x3FF) == 0x19; // LANG_RUSSIAN

    public static string T(string ru, string en) => Russian ? ru : en;
}
