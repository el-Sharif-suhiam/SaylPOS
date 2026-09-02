using Sayl.UI.Infrastructure;

namespace Sayl.UI.Controls
{
    /// <summary>
    /// نافذة تأكيد موحّدة الشكل للعمليات الحساسة (حذف، إتمام بيع، تسوية مخزون...).
    /// الاستخدام: ConfirmationDialog.Show(this, "عنوان", "نص الرسالة") → true عند التأكيد.
    /// </summary>
    internal static class ConfirmationDialog
    {
        // ═══════════════════════════════════════════════════════════════
        // 📖 ConfirmationDialog.Show — «نافذة من نمط واحد تنشأ وتُرمى»:
        // لا حقول حقن ولا كلاس مستقر — كل استدعاء يبني DialogForm جديدة
        // من الصفر ويستقبل نتيجتها ثم يرميها (using يضمن التخلص).
        //
        // وصفة بنائها (كل الأنماط المهمة في 90 سطرًا):
        //  • TLP ثلاثة صفوف مطلقة/مرنة (عنوان 40 / رسالة مرنة / أزرار 52)
        //    — «الجذر المجزّأ» مصغّرًا.
        //  • أزرار FlowPanel RightToLeft: أول Add (تأكيد) أقصى اليمين —
        //    الزر الرئيسي دومًا أولًا بالكود.
        //  • DialogResult مباشرة على الزرين (بلا Click handlers!) —
        //    إغلاق النافذة وإرجاع النتيجة بخاصية واحدة (نمط نافذة حوارية
        //    أساسي في WinForms يستحق الحفظ).
        //  • dangerStyle يقلب لون العنوان وزر التأكيد (أحمر/أزرق) — نفس
        //    الشكل يصلح للتأكيد المخيف والآمن.
        //  • AcceptButton/CancelButton: Enter=تأكيد وEsc=إلغاء مجانًا.
        // ═══════════════════════════════════════════════════════════════
        public static bool Show(IWin32Window? owner, string title, string message, bool dangerStyle = true)
        {
            using var dlg = new DialogForm
            {
                StartPosition = owner != null ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen,
                Size = new Size(430, 250),
                BackColor = AppTheme.CardBg,
                RightToLeft = RightToLeft.Yes,
                ShowInTaskbar = false,
                KeyPreview = true
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                Padding = new Padding(24, 20, 24, 20)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));

            var lblTitle = new Label
            {
                Text = title,
                Font = AppTheme.FontHeading,
                ForeColor = dangerStyle ? AppTheme.Danger : AppTheme.Primary,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight
            };

            var lblMessage = new Label
            {
                Text = message,
                Font = AppTheme.FontBody,
                ForeColor = AppTheme.TextPrimary,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight
            };

            var buttonsFlow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };

            var btnYes = AppTheme.MakeButton("تأكيد",
                dangerStyle ? AppTheme.Danger : AppTheme.Primary,
                hoverColor: dangerStyle ? Color.FromArgb(185, 28, 28) : AppTheme.PrimaryDark);
            btnYes.Size = new Size(140, 48);
            btnYes.DialogResult = DialogResult.Yes;

            var btnCancel = AppTheme.MakeButton("إلغاء",
                AppTheme.Bg,
                foreColor: AppTheme.TextPrimary,
                hoverColor: AppTheme.BorderColor);
            btnCancel.Size = new Size(120, 48);
            btnCancel.DialogResult = DialogResult.Cancel;

            buttonsFlow.Controls.Add(btnYes);
            buttonsFlow.Controls.Add(btnCancel);

            layout.Controls.Add(lblTitle, 0, 0);
            layout.Controls.Add(lblMessage, 0, 1);
            layout.Controls.Add(buttonsFlow, 0, 2);
            dlg.Controls.Add(layout);

            dlg.AcceptButton = btnYes;
            dlg.CancelButton = btnCancel;

            if (owner != null)
                dlg.ShowDialog(owner);
            else
                dlg.ShowDialog();

            return dlg.DialogResult == DialogResult.Yes;
        }
    }
}
