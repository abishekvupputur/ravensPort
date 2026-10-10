namespace RavensPort.Headless;

/// <summary>Plain columns for a terminal. Nothing clever: padded text, a header, and a blank-state line.</summary>
internal static class Table
{
    public static void Write<T>(TextWriter output, IReadOnlyList<T> rows, string empty, params (string Header, Func<T, string> Cell)[] columns)
    {
        if (rows.Count == 0)
        {
            output.WriteLine(empty);
            return;
        }

        var cells = rows.Select(row => columns.Select(c => c.Cell(row) ?? "").ToArray()).ToList();
        var widths = columns.Select((c, i) => Math.Max(c.Header.Length, cells.Max(r => r[i].Length))).ToArray();

        output.WriteLine(Line(columns.Select(c => c.Header).ToArray(), widths));
        foreach (var row in cells) output.WriteLine(Line(row, widths));
    }

    private static string Line(string[] values, int[] widths) =>
        string.Join("  ", values.Select((v, i) => i == values.Length - 1 ? v : v.PadRight(widths[i]))).TrimEnd();
}
