using System.Globalization;
using System.Text;
using SemanticPolicy.Evals.Metrics;

namespace SemanticPolicy.Evals.Output;

/// <summary>
/// Draws a calibration's reliability diagram as SVG, written by hand, so it needs no package. Each non-empty
/// bin is a bar over its width up to its observed frequency, with a point at its mean prediction; a calibrated
/// provider's points lie on the dashed diagonal. Every bin's row count stands above the plot, an empty bin's 0
/// included, so a bar drawn from one row reads as one row. Hovering a bar or a point shows its numbers. The
/// same calibration always gives the same bytes, whatever the machine or its culture.
/// </summary>
public static class ReliabilityDiagram
{
    // The plot is a square, probability 0 to 1 on both axes. The margins hold the title and the counts above
    // it, the tick labels and the axis titles below and to the left of it, and the legend at the bottom.
    private const double _left = 64;
    private const double _top = 72;
    private const double _size = 360;
    private const double _width = _left + _size + 24;
    private const double _height = _top + _size + 80;

    private const string _bar = "#9ecae1";
    private const string _point = "#08519c";
    private const string _diagonal = "#737373";
    private const string _grid = "#e6e6e6";
    private const string _axis = "#333333";

    private static readonly CultureInfo _invariant = CultureInfo.InvariantCulture;

    // No byte-order mark, as for a result file: the same diagram must be the same bytes on every machine.
    private static readonly UTF8Encoding _utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Draws the diagram.</summary>
    /// <param name="calibration">The calibration to draw; it must apply.</param>
    /// <returns>The SVG document, with <c>\n</c> line endings.</returns>
    /// <exception cref="ArgumentException">The calibration does not apply, so it has no bins to draw.</exception>
    public static string Render(Calibration calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        if (!calibration.Applicable)
        {
            throw new ArgumentException("A calibration that does not apply has no bins to draw.", nameof(calibration));
        }

        string title = $"Reliability diagram, n = {Count(calibration.Rows)}";
        StringBuilder svg = new();
        Line(svg, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(_width)}\" height=\"{N(_height)}\" "
            + $"viewBox=\"0 0 {N(_width)} {N(_height)}\" font-family=\"sans-serif\" font-size=\"12\">");
        Line(svg, $"<title>{title}</title>");
        Line(svg, "<desc>Ten equal-width bins of the predicted probability. Each bar rises to the bin's observed "
            + "frequency, its point sits at the bin's mean prediction, and a calibrated provider's points lie on "
            + "the dashed diagonal. The row count of every bin is above the plot.</desc>");
        Line(svg, $"<rect width=\"{N(_width)}\" height=\"{N(_height)}\" fill=\"#ffffff\"/>");
        Line(svg, $"<text x=\"{N(_width / 2)}\" y=\"28\" text-anchor=\"middle\" font-size=\"15\">{title}</text>");
        Grid(svg, calibration.Bins);
        Counts(svg, calibration.Bins);
        foreach ((ReliabilityBin bin, double _, double observed) in Filled(calibration.Bins))
        {
            Line(svg, $"<rect class=\"bar\" x=\"{X(bin.Lower)}\" y=\"{Y(observed)}\" "
                + $"width=\"{N((bin.Upper - bin.Lower) * _size)}\" height=\"{N(observed * _size)}\" "
                + $"fill=\"{_bar}\" stroke=\"#ffffff\"><title>{Tooltip(bin)}</title></rect>");
        }

        Line(svg, $"<line class=\"diagonal\" x1=\"{X(0)}\" y1=\"{Y(0)}\" x2=\"{X(1)}\" y2=\"{Y(1)}\" "
            + $"stroke=\"{_diagonal}\" stroke-dasharray=\"4 4\"/>");
        foreach ((ReliabilityBin bin, double mean, double observed) in Filled(calibration.Bins))
        {
            Line(svg, $"<circle class=\"point\" cx=\"{X(mean)}\" cy=\"{Y(observed)}\" r=\"4\" "
                + $"fill=\"{_point}\" stroke=\"#ffffff\"><title>{Tooltip(bin)}</title></circle>");
        }

        Line(svg, $"<rect class=\"plot\" x=\"{X(0)}\" y=\"{Y(1)}\" width=\"{N(_size)}\" height=\"{N(_size)}\" "
            + $"fill=\"none\" stroke=\"{_axis}\"/>");
        Axes(svg);
        Legend(svg);
        Line(svg, "</svg>");
        return svg.ToString();
    }

    /// <summary>Writes the diagram, replacing a file that is already there.</summary>
    /// <param name="path">Where the file goes.</param>
    /// <param name="calibration">The calibration to draw; it must apply.</param>
    /// <exception cref="ArgumentException">The calibration does not apply, so it has no bins to draw.</exception>
    /// <exception cref="EvalsException">The file cannot be written; the message names the path.</exception>
    public static void Write(string path, Calibration calibration)
    {
        ArgumentNullException.ThrowIfNull(path);

        // Drawn first: a diagram that cannot be drawn must not leave an empty file behind.
        string svg = Render(calibration);
        try
        {
            File.WriteAllText(path, svg, _utf8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new EvalsException($"Diagram '{path}' cannot be written: {e.Message}");
        }
    }

    // A line at every inner bin edge, so the ten bins can be told apart where they are empty, and one at every
    // labelled tick of the observed frequency.
    private static void Grid(StringBuilder svg, IReadOnlyList<ReliabilityBin> bins)
    {
        foreach (ReliabilityBin bin in bins.Skip(1))
        {
            Line(svg, $"<line x1=\"{X(bin.Lower)}\" y1=\"{Y(0)}\" x2=\"{X(bin.Lower)}\" y2=\"{Y(1)}\" stroke=\"{_grid}\"/>");
        }

        foreach (double tick in Ticks().Skip(1).SkipLast(1))
        {
            Line(svg, $"<line x1=\"{X(0)}\" y1=\"{Y(tick)}\" x2=\"{X(1)}\" y2=\"{Y(tick)}\" stroke=\"{_grid}\"/>");
        }
    }

    private static void Counts(StringBuilder svg, IReadOnlyList<ReliabilityBin> bins)
    {
        string y = N(_top - 10);
        Line(svg, $"<text x=\"{N(_left - 8)}\" y=\"{y}\" text-anchor=\"end\" fill=\"{_diagonal}\">rows</text>");
        foreach (ReliabilityBin bin in bins)
        {
            Line(svg, $"<text class=\"count\" x=\"{X((bin.Lower + bin.Upper) / 2)}\" y=\"{y}\" "
                + $"text-anchor=\"middle\">{Count(bin.Count)}</text>");
        }
    }

    private static void Axes(StringBuilder svg)
    {
        foreach (double tick in Ticks())
        {
            Line(svg, $"<text x=\"{X(tick)}\" y=\"{N(_top + _size + 18)}\" text-anchor=\"middle\">{Tick(tick)}</text>");
            Line(svg, $"<text x=\"{N(_left - 8)}\" y=\"{N(_top + ((1 - tick) * _size) + 4)}\" "
                + $"text-anchor=\"end\">{Tick(tick)}</text>");
        }

        Line(svg, $"<text x=\"{X(0.5)}\" y=\"{N(_top + _size + 40)}\" text-anchor=\"middle\">mean predicted probability</text>");
        Line(svg, $"<text transform=\"translate(20 {Y(0.5)}) rotate(-90)\" text-anchor=\"middle\">observed frequency</text>");
    }

    private static void Legend(StringBuilder svg)
    {
        double y = _top + _size + 66;
        Line(svg, $"<rect x=\"{N(_left)}\" y=\"{N(y - 10)}\" width=\"12\" height=\"12\" fill=\"{_bar}\"/>");
        Line(svg, $"<text x=\"{N(_left + 18)}\" y=\"{N(y)}\">observed frequency</text>");
        Line(svg, $"<circle cx=\"{N(_left + 138)}\" cy=\"{N(y - 4)}\" r=\"4\" fill=\"{_point}\"/>");
        Line(svg, $"<text x=\"{N(_left + 148)}\" y=\"{N(y)}\">mean prediction</text>");
        Line(svg, $"<line x1=\"{N(_left + 256)}\" y1=\"{N(y - 4)}\" x2=\"{N(_left + 280)}\" y2=\"{N(y - 4)}\" "
            + $"stroke=\"{_diagonal}\" stroke-dasharray=\"4 4\"/>");
        Line(svg, $"<text x=\"{N(_left + 286)}\" y=\"{N(y)}\">calibrated</text>");
    }

    // The bins with rows in them, which are the only ones with a mean prediction and an observed frequency.
    private static IEnumerable<(ReliabilityBin Bin, double Mean, double Observed)> Filled(IReadOnlyList<ReliabilityBin> bins)
    {
        foreach (ReliabilityBin bin in bins)
        {
            if (bin is { MeanPrediction: { } mean, ObservedFrequency: { } observed })
            {
                yield return (bin, mean, observed);
            }
        }
    }

    private static string Tooltip(ReliabilityBin bin) =>
        $"{Cut(bin.Lower)}-{Cut(bin.Upper)}: {Count(bin.Count)} {(bin.Count == 1 ? "row" : "rows")}, "
        + $"mean prediction {Rate(bin.MeanPrediction)}, observed frequency {Rate(bin.ObservedFrequency)}";

    private static IEnumerable<double> Ticks() => Enumerable.Range(0, 6).Select(step => step / 5.0);

    private static string X(double probability) => N(_left + (probability * _size));

    private static string Y(double probability) => N(_top + ((1 - probability) * _size));

    // Every number goes through the invariant culture: a comma for a decimal point would break the SVG.
    private static string N(double value) => value.ToString("0.##", _invariant);

    private static string Count(int value) => value.ToString(_invariant);

    private static string Cut(double value) => value.ToString("0.####", _invariant);

    private static string Tick(double value) => value.ToString("0.#", _invariant);

    private static string Rate(double? value) => value is { } rate ? rate.ToString("0.000", _invariant) : "n/a";

    // StringBuilder.AppendLine would write the platform's newline, and the bytes must not depend on it.
    private static void Line(StringBuilder svg, string text) => svg.Append(text).Append('\n');
}
