using System.Text;

namespace SecondBrain.Server.Tests.Support;

internal static class PdfFixture
{
    public static byte[] TwoPages()
    {
        static string Stream(string value)
        {
            var content = "BT /F1 12 Tf 40 700 Td (" + value + ") Tj ET";
            return $"<< /Length {content.Length} >>\nstream\n{content}\nendstream";
        }
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 7 0 R >> >> /Contents 4 0 R >>",
            Stream("First PDF page: the amber marker belongs to page one."),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 7 0 R >> >> /Contents 6 0 R >>",
            Stream("Second PDF page: sapphire evidence <script>window.pdfXss = true</script> stays text."),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        ];
        var text = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int>();
        for (var index = 0; index < objects.Length; index++) { offsets.Add(text.Length); text.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n"); }
        var xref = text.Length; text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(text.ToString());
    }
}
