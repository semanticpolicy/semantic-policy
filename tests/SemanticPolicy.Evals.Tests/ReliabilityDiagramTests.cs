using System.Globalization;
using System.Xml.Linq;
using SemanticPolicy.Evals.Metrics;
using SemanticPolicy.Evals.Output;

namespace SemanticPolicy.Evals.Tests;

public sealed class ReliabilityDiagramTests
{
    private static readonly XNamespace _svg = "http://www.w3.org/2000/svg";

    // Six rows: one alone in 0.1-0.2, five in 0.9-1, every other bin empty.
    private static readonly Calibration _sparse = new(
        Applicable: true,
        KindsFound: [],
        Rows: 6,
        Ece: 0.1,
        Brier: 0.2,
        Bins:
        [
            .. Enumerable.Range(0, 10).Select(index => index switch
            {
                1 => Bin(index, 1, 0.15, 0),
                9 => Bin(index, 5, 0.94, 0.8),
                _ => Bin(index, 0, null, null),
            }),
        ]);

    [Fact]
    public void Diagram_Titles_The_Row_Count_And_Labels_Every_Bin_With_Its_Count_Empty_Ones_Included()
    {
        XElement svg = Parse(ReliabilityDiagram.Render(_sparse));

        svg.Element(_svg + "title")!.Value.Should().Be("Reliability diagram, n = 6");
        OfClass(svg, "text", "count").Select(text => text.Value)
            .Should().Equal("0", "1", "0", "0", "0", "0", "0", "0", "0", "5");
    }

    [Fact]
    public void Diagram_Draws_Each_Non_Empty_Bin_Over_Its_Width_With_A_Point_At_Mean_Prediction_And_Observed_Frequency()
    {
        XElement svg = Parse(ReliabilityDiagram.Render(_sparse));
        XElement plot = OfClass(svg, "rect", "plot").Single();
        double x = Number(plot, "x");
        double y = Number(plot, "y");
        double width = Number(plot, "width");
        double height = Number(plot, "height");

        XElement[] bars = [.. OfClass(svg, "rect", "bar")];
        bars.Select(bar => bar.Element(_svg + "title")!.Value).Should().Equal(
            "0.1-0.2: 1 row, mean prediction 0.150, observed frequency 0.000",
            "0.9-1: 5 rows, mean prediction 0.940, observed frequency 0.800");
        bars.Select(bar => Number(bar, "x")).Should().Equal([x + (0.1 * width), x + (0.9 * width)], Close);
        bars.Select(bar => Number(bar, "width")).Should().Equal([width / 10, width / 10], Close);
        bars.Select(bar => Number(bar, "y")).Should().Equal([y + height, y + (0.2 * height)], Close);

        XElement[] points = [.. OfClass(svg, "circle", "point")];
        points.Select(point => point.Element(_svg + "title")!.Value)
            .Should().Equal(bars.Select(bar => bar.Element(_svg + "title")!.Value));
        points.Select(point => Number(point, "cx")).Should().Equal([x + (0.15 * width), x + (0.94 * width)], Close);
        points.Select(point => Number(point, "cy")).Should().Equal([y + height, y + (0.2 * height)], Close);

        // The diagonal runs from (0, 0) at the bottom left to (1, 1) at the top right.
        XElement diagonal = OfClass(svg, "line", "diagonal").Single();
        new[] { Number(diagonal, "x1"), Number(diagonal, "y1"), Number(diagonal, "x2"), Number(diagonal, "y2") }
            .Should().Equal([x, y + height, x + width, y], Close);
    }

    [Fact]
    public void Diagram_Is_The_Same_Bytes_Under_Any_Culture_Without_A_Byte_Order_Mark()
    {
        using TempFile invariant = TempFile.Write("", ".svg");
        using TempFile polish = TempFile.Write("", ".svg");
        CultureInfo culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            ReliabilityDiagram.Write(invariant.Path, _sparse);

            // A comma for the decimal point: a coordinate formatted in the current culture would change here.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
            ReliabilityDiagram.Write(polish.Path, _sparse);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }

        byte[] bytes = File.ReadAllBytes(invariant.Path);
        File.ReadAllBytes(polish.Path).Should().Equal(bytes);
        bytes[0].Should().Be((byte)'<');
        bytes.Should().NotContain((byte)'\r');
    }

    [Fact]
    public void Diagram_Refuses_A_Calibration_That_Does_Not_Apply()
    {
        Calibration none = new(Applicable: false, KindsFound: ["score"], Rows: 0, Ece: null, Brier: null, Bins: []);

        Action render = () => ReliabilityDiagram.Render(none);

        render.Should().Throw<ArgumentException>().WithParameterName("calibration");
    }

    private static ReliabilityBin Bin(int index, int count, double? mean, double? observed) =>
        new((double)index / 10, (double)(index + 1) / 10, count, mean, observed);

    private static XElement Parse(string svg)
    {
        XElement root = XDocument.Parse(svg).Root!;
        root.Name.Should().Be(_svg + "svg");
        return root;
    }

    private static IEnumerable<XElement> OfClass(XElement svg, string element, string cssClass) =>
        svg.Descendants(_svg + element).Where(node => (string?)node.Attribute("class") == cssClass);

    private static double Number(XElement element, string attribute) =>
        double.Parse(element.Attribute(attribute)!.Value, CultureInfo.InvariantCulture);

    // Coordinates are written to two decimals.
    private static bool Close(double actual, double expected) => Math.Abs(actual - expected) < 0.01;
}
