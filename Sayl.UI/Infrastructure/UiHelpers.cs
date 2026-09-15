using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;

namespace Sayl.UI.Infrastructure
{
    /// <summary>مساعدات مشتركة للواجهات: تنسيق الجداول، الرسائل، وتنفيذ الخدمات ضمن Scope.</summary>
    internal static class UiHelpers
    {
        public static string Money(decimal value) => value.ToString("N2");

        // ═══════════════════════════════════════════════════════════════
        // 📖 StyleGrid: التنسيق الموحّد لكل جداول النظام — جولة سطرًا بسطر.
        // تُستدعى بعد إنشاء أي DataGridView وقبل إضافة الأعمدة.
        // فلسفتها: جدول للعرض فقط — لا إضافة صفوف، لا تحرير (إلا إن فتحت
        // ReadOnly=false بنفسك بعدها كما في PayrollForm).
        // ═══════════════════════════════════════════════════════════════
        public static void StyleGrid(DataGridView grid)
        {
            // ── الهيكل العام ──
            grid.Dock = DockStyle.Fill;                       // يملأ الحاوية كاملة
            grid.AllowUserToAddRows = false;                   // إخفاء صف «الإضافة» الأخير الفارغ
            grid.AllowUserToDeleteRows = false;                // لا حذف من الجدول (عبر الأزرار فقط)
            grid.AllowUserToResizeRows = false;               // ارتفاع الصفوف ثابت — لا مقابض سحب
            grid.ReadOnly = true;                             // عرض فقط — غيّره يدويًا إن أردت التحرير
            grid.MultiSelect = false;                          // تحديد صف واحد فقط في المرة
            grid.RowHeadersVisible = false;                    // إخفاء العمود الرمادي «السهم» أقصى اليمين
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; // النقر يظلل الصف كاملًا
            // VULN-32: تعطيل الفرز مركزيًا — نصف الشاشات تفهرس الصف المحدد
            // على قائمة خلفية غير مفرزة؛ نقرة رأس عمود تقلب الترتيب البصري
            // فيصيب الفعل (صرف/حذف/تعديل/تحصيل) السجل الخطأ.
            // StyleGrid يُستدعى قبل إضافة الأعمدة غالبًا، فالتعطيل يتم
            // عبر حدثين: الأعمدة الموجودة الآن، وكل عمود يُضاف لاحقًا.
            foreach (DataGridViewColumn col in grid.Columns)
                col.SortMode = DataGridViewColumnSortMode.NotSortable;
            grid.ColumnAdded += (s, e) => e.Column.SortMode = DataGridViewColumnSortMode.NotSortable;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; // الأعمدة تتمدد تملأ العرض — FillWeight يوزع
            grid.BackgroundColor = AppTheme.CardBg;           // خلفية ما وراء الصفوف (منطقة الفائض)
            grid.BorderStyle = BorderStyle.None;              // بلا إطار — الحاوية هي من يحدد الشكل
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal; // خطوط أفقية فقط — لا شبكة سجن!
            grid.GridColor = AppTheme.BorderColor;             // لون تلك الخطوط الأفقية الفاتحة
            grid.EnableHeadersVisualStyles = false;           // ⚠️ الأهم: يعطّل «جلد ويندوز» للرأس — بدون هذا السطر لن تعمل ألوان الرأس أسفل

            // ── رأس الجدول (شريط كحلي علوي) ──
            // كل سطر هنا يظهر في كل جدول بالنظام — عدّل بحذر:
            // غيّر الألوان من AppTheme وليس هنا (مصدر واحد للحقيقة).
            grid.ColumnHeadersDefaultCellStyle.BackColor = AppTheme.SidebarBg;    // خلفية الرأس الكحلية
            grid.ColumnHeadersDefaultCellStyle.ForeColor = AppTheme.TextOnDark;  // نص أبيض فوق الكحلي
            grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 10F, FontStyle.Bold); // خط الرأس
            grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight; // نص الرأس يمينًا (عربي)
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 6, 8, 6); // تنفّس داخل خلية الرأس
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing; // ارتفاع الرأس ثابت
            grid.ColumnHeadersHeight = 44;                    // ارتفاع الرأس — 44 = مريح للمس/الفأرة

            // ── خلايا البيانات ──
            grid.DefaultCellStyle.Font = new Font("Segoe UI", 10.5F);          // خط الخلايا
            grid.DefaultCellStyle.BackColor = AppTheme.CardBg;                  // خلفية الصف الأساسية
            grid.DefaultCellStyle.ForeColor = AppTheme.TextPrimary;               // لون النص
            grid.DefaultCellStyle.SelectionBackColor = AppTheme.PrimaryLight;    // خلفية الصف المحدد (أزرق فاتح)
            grid.DefaultCellStyle.SelectionForeColor = AppTheme.TextPrimary;      // نص محدد يبقى داكنًا (قراءة)
            grid.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight; // النص يمين (عربي) — الأرقام تُهيأ لاحقًا بعمودها
            grid.DefaultCellStyle.Padding = new Padding(8, 4, 8, 4);             // تنفّس داخل كل خلية

            // ── الصفوف المتناوبة والارتفاع ──
            grid.AlternatingRowsDefaultCellStyle.BackColor = AppTheme.FieldBg; // تظليل الصفوف الفردية — عين المستخدم تتتبع الصف أسهل
            grid.RowTemplate.Height = 40;                                       // ارتفاع كل صف — 40 = حد مريح للقراءة والمس
        }

        public static void ShowError(IWin32Window? owner, string message) =>
            MessageBox.Show(owner, message, AppLang.T("Common.Title.Error"), MessageBoxButtons.OK, MessageBoxIcon.Error,
                MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);

        public static void ShowInfo(IWin32Window? owner, string message) =>
            MessageBox.Show(owner, message, AppLang.T("Common.Title.Done"), MessageBoxButtons.OK, MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button1, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);

        public static void ShowResult(IWin32Window? owner, Result result, string? successOverride = null)
        {
            if (result.IsSuccess)
                ShowInfo(owner, string.IsNullOrWhiteSpace(successOverride) ? result.Message : successOverride);
            else
                ShowError(owner, string.IsNullOrWhiteSpace(result.Message) ? AppLang.T("Common.Msg.OperationFailed") : result.Message);
        }

        /// <summary>تنفيذ عملية خدمة ضمن Scope مستقل (سياق EF قصير العمر).</summary>
        public static async Task<Result> UseServiceAsync(Func<clsStockService, Task<Result>> action)
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<clsStockService>();
                return await action(service);
            }
            catch
            {
                return Result.Fail(AppLang.T("Common.Msg.DbConnectionFailed"));
            }
        }
    }
}
