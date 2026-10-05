using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

// Minimal JSON reader for the PTZCalibration exports. JsonUtility can't read
// nested arrays (camera_matrix) or objects keyed by number (per_zoom), so this
// parses into plain Dictionary<string, object> / List<object> / double / string
// / bool / null.
public static class MiniJson
{
    public static object Parse(string s)
    {
        int i = 0;
        object v = ReadValue(s, ref i);
        SkipWhite(s, ref i);
        if (i != s.Length) throw new FormatException($"Unexpected trailing JSON at char {i}");
        return v;
    }

    static void SkipWhite(string s, ref int i)
    {
        while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == '﻿')) i++;
    }

    static object ReadValue(string s, ref int i)
    {
        SkipWhite(s, ref i);
        if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
        char c = s[i];
        if (c == '{') return ReadObject(s, ref i);
        if (c == '[') return ReadArray(s, ref i);
        if (c == '"') return ReadString(s, ref i);
        if (TryLiteral(s, ref i, "true")) return true;
        if (TryLiteral(s, ref i, "false")) return false;
        if (TryLiteral(s, ref i, "null")) return null;
        return ReadNumber(s, ref i);
    }

    static Dictionary<string, object> ReadObject(string s, ref int i)
    {
        var d = new Dictionary<string, object>();
        i++; // {
        SkipWhite(s, ref i);
        if (i < s.Length && s[i] == '}') { i++; return d; }
        while (true)
        {
            SkipWhite(s, ref i);
            if (i >= s.Length || s[i] != '"') throw new FormatException($"Expected object key at char {i}");
            string key = ReadString(s, ref i);
            SkipWhite(s, ref i);
            if (i >= s.Length || s[i] != ':') throw new FormatException($"Expected ':' at char {i}");
            i++;
            d[key] = ReadValue(s, ref i);
            SkipWhite(s, ref i);
            if (i < s.Length && s[i] == ',') { i++; continue; }
            if (i < s.Length && s[i] == '}') { i++; return d; }
            throw new FormatException($"Expected ',' or '}}' at char {i}");
        }
    }

    static List<object> ReadArray(string s, ref int i)
    {
        var l = new List<object>();
        i++; // [
        SkipWhite(s, ref i);
        if (i < s.Length && s[i] == ']') { i++; return l; }
        while (true)
        {
            l.Add(ReadValue(s, ref i));
            SkipWhite(s, ref i);
            if (i < s.Length && s[i] == ',') { i++; continue; }
            if (i < s.Length && s[i] == ']') { i++; return l; }
            throw new FormatException($"Expected ',' or ']' at char {i}");
        }
    }

    static string ReadString(string s, ref int i)
    {
        var sb = new StringBuilder();
        i++; // opening quote
        while (i < s.Length)
        {
            char c = s[i++];
            if (c == '"') return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }
            if (i >= s.Length) break;
            char e = s[i++];
            switch (e)
            {
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 4;
                    break;
                default: sb.Append(e); break; // \" \\ \/
            }
        }
        throw new FormatException("Unterminated JSON string");
    }

    static bool TryLiteral(string s, ref int i, string lit)
    {
        if (string.CompareOrdinal(s, i, lit, 0, lit.Length) != 0) return false;
        i += lit.Length;
        return true;
    }

    static double ReadNumber(string s, ref int i)
    {
        int start = i;
        while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
        if (i == start) throw new FormatException($"Unexpected character '{s[i]}' at char {i}");
        return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
    }
}
