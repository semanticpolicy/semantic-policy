using System.Globalization;
using System.Text;

namespace SemanticPolicy.Evals.Output;

/// <summary>
/// A plain-text table for a report: every column as wide as its widest cell, numbers right-aligned so
/// their digits line up, a dashed line under the header, and nothing a script would have to strip.
/// </summary>
public sealed class TextTable
{
    private readonly string[] _headers;
    private readonly List<string[]> _rows = [];

    /// <summary>Starts a table with these column headers.</summary>
    /// <param name="headers">One header per column; at least one.</param>
    public TextTable(params string[] headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (headers.Length == 0)
        {
            throw new ArgumentException("A table needs at least one column.", nameof(headers));
        }

        _headers = [.. headers.Select(header => header ?? string.Empty)];
    }

    /// <summary>Appends a row.</summary>
    /// <param name="cells">One cell per column, as text; a null cell is empty.</param>
    /// <exception cref="ArgumentException">The row has a different number of cells than the table has columns.</exception>
    public void AddRow(params string[] cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        if (cells.Length != _headers.Length)
        {
            throw new ArgumentException(
                $"The table has {_headers.Length} columns; the row has {cells.Length} cells.",
                nameof(cells));
        }

        _rows.Add([.. cells.Select(cell => cell ?? string.Empty)]);
    }

    /// <summary>Writes the header, a separator and every row, one line each, with trailing spaces trimmed.</summary>
    /// <param name="writer">Where the table goes.</param>
    public void Write(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        int columns = _headers.Length;
        int[] widths = new int[columns];
        bool[] numeric = new bool[columns];
        for (int column = 0; column < columns; column++)
        {
            widths[column] = _headers[column].Length;
            numeric[column] = true;
            foreach (string[] row in _rows)
            {
                widths[column] = Math.Max(widths[column], row[column].Length);
                numeric[column] &= double.TryParse(row[column], NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            }
        }

        writer.WriteLine(Line(_headers, widths, numeric));
        writer.WriteLine(Line([.. widths.Select(width => new string('-', width))], widths, numeric));
        foreach (string[] row in _rows)
        {
            writer.WriteLine(Line(row, widths, numeric));
        }
    }

    private static string Line(string[] cells, int[] widths, bool[] numeric)
    {
        StringBuilder line = new();
        for (int column = 0; column < cells.Length; column++)
        {
            if (column > 0)
            {
                line.Append("  ");
            }

            line.Append(numeric[column] ? cells[column].PadLeft(widths[column]) : cells[column].PadRight(widths[column]));
        }

        return line.ToString().TrimEnd();
    }
}
