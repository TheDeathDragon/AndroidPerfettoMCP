using System.Collections.Generic;
using System.Text;

namespace PerfettoMcp.Tools;

// Best-effort Itanium demangling of the qualified name only ("_ZN3foo3barEv" -> "foo::bar"); unsupported forms
// are returned unchanged.
internal static class Demangle
{
    public static string Simplify(string name)
    {
        if (name.Length < 4 || !name.StartsWith("_Z"))
        {
            return name;
        }
        int pos = 2;
        if (pos < name.Length && name[pos] == 'L')
        {
            pos++;
        }
        List<string> parts = new();
        if (pos < name.Length && name[pos] == 'N')
        {
            pos++;
            while (pos < name.Length && name[pos] is 'K' or 'V' or 'r')
            {
                pos++;
            }
            while (pos < name.Length && name[pos] != 'E')
            {
                if (char.IsAsciiDigit(name[pos]))
                {
                    if (!ReadIdentifier(name, ref pos, out string id))
                    {
                        return name;
                    }
                    parts.Add(id);
                }
                else if (Starts(name, pos, "St"))
                {
                    parts.Add("std");
                    pos += 2;
                }
                else if (parts.Count > 0 && (Starts(name, pos, "C1") || Starts(name, pos, "C2") || Starts(name, pos, "C3")))
                {
                    parts.Add(parts[^1]);
                    pos += 2;
                }
                else if (parts.Count > 0 && (Starts(name, pos, "D0") || Starts(name, pos, "D1") || Starts(name, pos, "D2")))
                {
                    parts.Add("~" + parts[^1]);
                    pos += 2;
                }
                else
                {
                    return name;
                }
            }
            if (pos >= name.Length || parts.Count == 0)
            {
                return name;
            }
        }
        else if (pos < name.Length && char.IsAsciiDigit(name[pos]))
        {
            if (!ReadIdentifier(name, ref pos, out string id))
            {
                return name;
            }
            parts.Add(id);
        }
        else
        {
            return name;
        }
        StringBuilder sb = new();
        foreach (string p in parts)
        {
            if (sb.Length > 0)
            {
                sb.Append("::");
            }
            sb.Append(p);
        }
        return sb.ToString();
    }

    private static bool ReadIdentifier(string s, ref int pos, out string id)
    {
        int len = 0;
        while (pos < s.Length && char.IsAsciiDigit(s[pos]))
        {
            len = (len * 10) + (s[pos] - '0');
            pos++;
            if (len > 4096)
            {
                id = string.Empty;
                return false;
            }
        }
        if (len == 0 || pos + len > s.Length)
        {
            id = string.Empty;
            return false;
        }
        id = s.Substring(pos, len);
        pos += len;
        return true;
    }

    private static bool Starts(string s, int pos, string token) =>
        pos + token.Length <= s.Length && string.CompareOrdinal(s, pos, token, 0, token.Length) == 0;
}
