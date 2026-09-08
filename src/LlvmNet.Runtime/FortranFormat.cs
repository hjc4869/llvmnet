using System.Globalization;
using System.Text;

namespace LlvmNet.Runtime;

internal sealed record FortranEdit(string Kind, int Width = 0, int Precision = -1, string Text = "", int ExponentWidth = 0);

internal static class FortranFormat
{
    internal static List<FortranEdit> Parse(string format)
    {
        int cursor = 0;
        return Group(false);

        List<FortranEdit> Group(bool closing)
        {
            List<FortranEdit> edits = [];
            while (cursor < format.Length)
            {
                if (char.IsWhiteSpace(format[cursor]) || format[cursor] == ',') { cursor++; continue; }
                if (format[cursor] == ')')
                {
                    if (!closing) throw new FormatException("Unbalanced Fortran format.");
                    cursor++;
                    return edits;
                }
                if (format[cursor] is '\'' or '"')
                {
                    char quote = format[cursor++];
                    var text = new StringBuilder();
                    bool ended = false;
                    while (cursor < format.Length)
                    {
                        char character = format[cursor++];
                        if (character == quote)
                        {
                            if (cursor < format.Length && format[cursor] == quote) cursor++;
                            else { ended = true; break; }
                        }
                        text.Append(character);
                    }
                    if (!ended) throw new FormatException("Unterminated Fortran format literal.");
                    edits.Add(new FortranEdit("literal", Text: text.ToString()));
                    continue;
                }
                int repeat = Number(1);
                if (cursor == format.Length) throw new FormatException("Missing Fortran format descriptor.");
                if (format[cursor] == '(')
                {
                    cursor++;
                    List<FortranEdit> nested = Group(true);
                    for (int index = 0; index < repeat; index++) edits.AddRange(nested);
                    continue;
                }
                string kind = char.ToUpperInvariant(format[cursor++]).ToString();
                if (cursor < format.Length && (kind + char.ToUpperInvariant(format[cursor])) is "ES" or "EN" or "SP" or "SS" or "BN" or "BZ" or "TL" or "TR")
                    kind += char.ToUpperInvariant(format[cursor++]);
                if (kind is "/" or ":" or "X")
                {
                    edits.Add(new FortranEdit(kind, repeat));
                    continue;
                }
                if (kind is "P" or "SP" or "SS" or "S" or "BN" or "BZ")
                {
                    edits.Add(new FortranEdit(kind, repeat));
                    continue;
                }
                if (kind is not ("I" or "B" or "O" or "Z" or "F" or "E" or "ES" or "EN" or "D" or "G" or "A" or "L" or "T" or "TL" or "TR"))
                    throw new NotSupportedException($"Fortran format descriptor {kind} is not implemented.");
                int width = Number(0);
                int precision = -1;
                if (cursor < format.Length && format[cursor] == '.') { cursor++; precision = Number(0); }
                int exponentWidth = 0;
                if (kind is "E" or "ES" or "EN" or "G" && cursor < format.Length && char.ToUpperInvariant(format[cursor]) == 'E')
                {
                    cursor++;
                    exponentWidth = Number(0);
                    if (exponentWidth <= 0) throw new FormatException("Fortran exponent width must be positive.");
                }
                for (int index = 0; index < repeat; index++) edits.Add(new FortranEdit(kind, width, precision, ExponentWidth: exponentWidth));
            }
            if (closing) throw new FormatException("Unclosed Fortran format group.");
            return edits;
        }

        int Number(int fallback)
        {
            while (cursor < format.Length && char.IsWhiteSpace(format[cursor])) cursor++;
            int result = 0;
            bool negative = cursor < format.Length && format[cursor] == '-';
            if (negative) cursor++;
            bool digits = false;
            while (cursor < format.Length)
            {
                if (char.IsWhiteSpace(format[cursor])) { cursor++; continue; }
                if (!char.IsAsciiDigit(format[cursor])) break;
                digits = true;
                result = checked(result * 10 + format[cursor++] - '0');
            }
            if (negative && !digits) throw new FormatException("Missing signed Fortran format number.");
            return !digits ? fallback : negative ? -result : result;
        }
    }

    internal static string Integer(long value, FortranEdit edit, bool plus)
    {
        string digits = edit.Kind switch
        {
            "I" => (value == long.MinValue ? 1UL << 63 : (ulong)Math.Abs(value)).ToString(CultureInfo.InvariantCulture),
            "Z" => unchecked((ulong)value).ToString("X", CultureInfo.InvariantCulture),
            "O" => Convert.ToString(value, 8),
            "B" => Convert.ToString(value, 2),
            _ => throw new FormatException("Integer value paired with a non-integer Fortran format.")
        };
        if (edit.Precision == 0 && value == 0) digits = "";
        if (edit.Precision > 0) digits = digits.PadLeft(edit.Precision, '0');
        string sign = edit.Kind == "I" ? value < 0 ? "-" : plus ? "+" : "" : "";
        return Field(sign + digits, edit.Width);
    }

    internal static string Real(double value, FortranEdit edit, bool plus, int scale)
    {
        int precision = Math.Max(0, edit.Precision);
        if (edit.Kind == "G" && edit.ExponentWidth > 0 && double.IsFinite(value))
        {
            double magnitude = Math.Abs(value);
            if (magnitude != 0 && (magnitude < 0.1 || magnitude >= Math.Pow(10, precision)))
                return Real(value, edit with { Kind = "E" }, plus, scale);
            int digits = magnitude == 0 ? 1 : Math.Max(0, (int)Math.Floor(Math.Log10(magnitude)) + 1);
            int places = Math.Max(0, precision - digits);
            string fixedText = value.ToString("F" + places, CultureInfo.InvariantCulture) + (places == 0 ? "." : "");
            if (plus && !double.IsNegative(value)) fixedText = "+" + fixedText;
            int reserved = edit.ExponentWidth + 2;
            if (edit.Width <= reserved || fixedText.Length > edit.Width - reserved) return new string('*', edit.Width);
            return Field(fixedText, edit.Width - reserved) + new string(' ', reserved);
        }
        string text;
        if (edit.Kind == "F")
            text = (value * Math.Pow(10, scale)).ToString("F" + precision, CultureInfo.InvariantCulture);
        else if (edit.Kind is "E" or "D" or "ES" or "EN")
        {
            if (edit.Kind is "EN") throw new NotSupportedException("Engineering Fortran real editing is not implemented.");
            if (edit.ExponentWidth > 0 && value == 0)
                text = value.ToString("F" + precision, CultureInfo.InvariantCulture) + "E+" + new string('0', edit.ExponentWidth);
            else if (edit.ExponentWidth > 0 && !double.IsFinite(value))
                text = value.ToString(CultureInfo.InvariantCulture);
            else if (!double.IsFinite(value) || value == 0)
                text = value.ToString("F" + precision, CultureInfo.InvariantCulture) + "E+00";
            else
            {
                int exponent = (int)Math.Floor(Math.Log10(Math.Abs(value))) + (edit.Kind == "ES" ? 0 : 1 - scale);
                double significand = value / Math.Pow(10, exponent);
                string exponentText = Math.Abs(exponent).ToString("D" + (edit.ExponentWidth == 0 ? 2 : edit.ExponentWidth), CultureInfo.InvariantCulture);
                if (edit.ExponentWidth > 0 && exponentText.Length > edit.ExponentWidth) return new string('*', edit.Width);
                text = significand.ToString("F" + precision, CultureInfo.InvariantCulture) + (edit.Kind == "D" ? "D" : "E") + (exponent < 0 ? "-" : "+") + exponentText;
            }
        }
        else if (edit.Kind == "G")
            text = value.ToString("G" + (precision == 0 ? 17 : precision), CultureInfo.InvariantCulture);
        else
            throw new FormatException("Real value paired with a non-real Fortran format.");
        if (plus && !double.IsNegative(value)) text = "+" + text;
        return Field(text, edit.Width);
    }
    internal static string Field(string text, int width) => width == 0 ? text : text.Length > width ? new string('*', width) : text.PadLeft(width);
}