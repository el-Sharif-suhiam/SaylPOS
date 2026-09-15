using ClosedXML.Excel;

namespace Sayl.UI.Infrastructure
{
    /// <summary>
    /// مصدّر Excel عام لكل تقارير النظام: ورقة واحدة بترويسة كحلية، تجميد صف العناوين،
    /// فلترة تلقائية، وضبط تلقائي للأعمدة — جاهزة للتحليل أو الإرسال للمحاسب.
    /// </summary>
    internal static class ExcelService
    {
        public static void ExportTable(string title, string period, string[] headers, List<string[]> rows, string filePath)
        {
            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add(AppLang.T("Excel.SheetName"));

            // الاتجاه من اليمين لليسار لكل الورقة
            ws.RightToLeft = true;

            // ── الترويسة ──
            ws.Cell(1, 1).Value = AppLang.T("Excel.Header", title, period, Sayl.UI.Infrastructure.AppClock.NowLocal);
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontSize = 14;
            ws.Cell(1, 1).Style.Font.FontColor = XLColor.White;
            ws.Cell(1, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#002B54"); // SidebarBg
            ws.Range(1, 1, 1, Math.Max(headers.Length, 1)).Merge().Style.Alignment
                .SetHorizontal(XLAlignmentHorizontalValues.Right);

            // ── صف العناوين ──
            int headerRow = 3;
            for (int i = 0; i < headers.Length; i++)
            {
                var cell = ws.Cell(headerRow, i + 1);
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontSize = 11;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#0B3B69"); // SidebarHover
                cell.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            }

            // ── البيانات ──
            for (int r = 0; r < rows.Count; r++)
            {
                for (int c = 0; c < headers.Length; c++)
                {
                    var cell = ws.Cell(headerRow + 1 + r, c + 1);
                    string raw = c < rows[r].Length ? rows[r][c] ?? "" : "";
                    string trimmed = raw.Trim();

                    // الأرقام تُدرج كأرقام حقيقية حتى تعمل المعادلات/الفرز في Excel
                    // VULN-41: كان الفحص InvariantCulture فقط — الشاشات تنسق
                    // بثقافة الجهاز (على العربية: فواصل/مجموعات مختلفة) فكانت
                    // كل المبالغ تهبط «نصًا» يكسر المعادلات والفلاتر. الآن:
                    // نجرب الثقافتين — الجهاز أولًا (المصدر) ثم الثابتة (فأرقام
                    // الجداول النصية القديمة تظل تعمل).
                    if (trimmed != "" &&
                        (decimal.TryParse(trimmed, System.Globalization.NumberStyles.Any,
                             System.Globalization.CultureInfo.CurrentCulture, out var num) ||
                         decimal.TryParse(trimmed, System.Globalization.NumberStyles.Any,
                             System.Globalization.CultureInfo.InvariantCulture, out num)))
                        cell.Value = num;
                    else
                        cell.Value = raw;

                    if (r % 2 == 1)
                        cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#F9FAFB"); // FieldBg
                }
            }

            // ── تجميد العناوين + فلترة + ضبط الأعمدة ──
            ws.SheetView.FreezeRows(headerRow);
            if (rows.Count > 0)
                ws.Range(headerRow, 1, headerRow + rows.Count, headers.Length).SetAutoFilter();
            ws.Columns().AdjustToContents();

            workbook.SaveAs(filePath);
        }
    }
}
