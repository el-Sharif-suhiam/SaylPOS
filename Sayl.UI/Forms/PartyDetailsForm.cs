using Sayl.Core.Models;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// عرض تفاصيل طرف (عميل/مورد): بيانات الاتصال والعنوان والحالة وأي حقول إضافية.
    /// الاستخدام: new PartyDetailsForm(party, ("الرصيد", "..."), ("حد الائتمان", "...")).ShowDialog(owner);
    /// </summary>
    public class PartyDetailsForm : DialogForm
    {
        public PartyDetailsForm(Party party,
            (string Caption, string Value)? extra1 = null,
            (string Caption, string Value)? extra2 = null)
        {
            Text = "تفاصيل الطرف";
            StartPosition = FormStartPosition.CenterParent;
            BackColor = AppTheme.CardBg;
            RightToLeft = Yes();
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;
            MinimumSize = new Size(480, 320);
            ClientSize = ComputeSize(extra1, extra2);

            string typeText = party.PartyType switch
            {
                'C' => "عميل",
                'S' => "مورد",
                'B' => "عميل ومورد",
                _ => party.PartyType.ToString()
            };

            int row = 0;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(20, 14, 20, 12) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            void Add(string caption, string? value)
            {
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
                root.Controls.Add(new Label
                {
                    Text = caption,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleRight,
                    ForeColor = AppTheme.TextMuted
                }, 0, row);
                root.Controls.Add(new Label
                {
                    Text = string.IsNullOrWhiteSpace(value) ? "-" : value,
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleRight,
                    ForeColor = AppTheme.TextPrimary,
                    Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                    AutoEllipsis = true
                }, 1, row);
                row++;
            }

            var lblTitle = new Label
            {
                Text = $"بيانات {typeText}: {party.Name}",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.Primary,
                Height = 44
            };

            Add("الاسم", party.Name);
            Add("النوع", typeText);
            Add("الحالة", party.IsActive ? "نشط" : "معطل");
            Add("الجوال 1", party.Phone1);
            Add("الجوال 2", party.Phone2);
            Add("البريد الإلكتروني", party.Email);
            Add("العنوان", party.AddressLine1);
            Add("العنوان 2", party.AddressLine2);

            if (extra1.HasValue) Add(extra1.Value.Caption, extra1.Value.Value);
            if (extra2.HasValue) Add(extra2.Value.Caption, extra2.Value.Value);

            Add("ملاحظات", party.Notes);
            Add("تاريخ الإضافة", party.CreatedAt == default ? null : Sayl.UI.Infrastructure.AppClock.ToLocal(party.CreatedAt).ToString("yyyy-MM-dd"));
            if (row < 14)
                Add("آخر تحديث", party.UpdatedAt?.ToString("yyyy-MM-dd"));

            // صف مرن يمتص المساحة المتبقية ثم زر الإغلاق
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var filler = new Panel { BackColor = AppTheme.CardBg, Dock = DockStyle.Fill };
            root.Controls.Add(filler, 0, row);
            root.SetColumnSpan(filler, 2);
            row++;

            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
            var btnClose = AppTheme.MakeButton("إغلاق", AppTheme.Bg,
                foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnClose.Dock = DockStyle.Fill;
            btnClose.Margin = new Padding(0, 6, 0, 8);
            btnClose.DialogResult = DialogResult.Cancel;
            root.Controls.Add(btnClose, 0, row);
            root.SetColumnSpan(btnClose, 2);

            Controls.Add(root);
            CancelButton = btnClose;
        }

        private static System.Windows.Forms.RightToLeft Yes() => System.Windows.Forms.RightToLeft.Yes;

        private Size ComputeSize((string, string)? e1, (string, string)? e2)
        {
            // 13 صفًا أساسيًا + صفوف الإضافات + عنوان + حشو + زر
            int rows = 13 + (e1.HasValue ? 1 : 0) + (e2.HasValue ? 1 : 0);
            int height = Math.Min(44 + rows * 36 + 66, 760);
            return new Size(520, height);
        }
    }
}
