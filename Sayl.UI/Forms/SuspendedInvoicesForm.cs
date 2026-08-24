using Common.DTOs.RequestModels;
using Common.Enums;
using Common.Results;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Controls;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.Forms
{
    /// <summary>
    /// قائمة الفواتير المعلقة: استكمال (مع الدفع) أو إلغاء.
    /// </summary>
    public class SuspendedInvoicesForm : Form
    {
        private DataGridView _grid = null!;
        private Button _btnResume = null!;
        private Button _btnCancelInvoice = null!;

        private List<Common.DTOs.ViewModel.InvoiceVM> _invoices = new();

        /// <summary>يصبح true إذا تم استكمال أو إلغاء أي فاتورة (لتحديث شاشة البيع).</summary>
        public bool Changed { get; private set; }

        public SuspendedInvoicesForm()
        {
            BuildUi();
            Load += async (_, _) => await LoadInvoicesAsync();
        }

        private void BuildUi()
        {
            Text = "الفواتير المعلقة";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(640, 560);
            BackColor = AppTheme.CardBg;
            RightToLeft = RightToLeft.Yes;
            Font = AppTheme.FontBody;
            ShowInTaskbar = false;
            AppTheme.ApplyRoundedRegion(this, 18);

            var lblTitle = new Label
            {
                Text = "الفواتير المعلقة",
                Bounds = new Rectangle(0, 18, 640, 32),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = AppTheme.FontHeading,
                ForeColor = AppTheme.TextPrimary
            };

            _grid = new DataGridView { Bounds = new Rectangle(30, 62, 580, 380) };
            UiHelpers.StyleGrid(_grid);
            _grid.Columns.Add("colId", "رقم الفاتورة");
            _grid.Columns.Add("colDate", "التاريخ");
            _grid.Columns.Add("colCustomer", "العميل");
            _grid.Columns.Add("colTotal", "الصافي");
            _grid.Columns["colId"].FillWeight = 20;
            _grid.Columns["colDate"].FillWeight = 30;
            _grid.Columns["colCustomer"].FillWeight = 30;
            _grid.Columns["colTotal"].FillWeight = 20;

            _btnResume = AppTheme.MakeButton("استكمال الفاتورة", AppTheme.Primary, hoverColor: AppTheme.PrimaryDark);
            _btnResume.Bounds = new Rectangle(30, 460, 220, 50);
            _btnResume.Click += async (_, _) => await ResumeAsync();

            _btnCancelInvoice = AppTheme.MakeButton("إلغاء الفاتورة", AppTheme.Danger, hoverColor: Color.FromArgb(185, 28, 28));
            _btnCancelInvoice.Bounds = new Rectangle(262, 460, 180, 50);
            _btnCancelInvoice.Click += async (_, _) => await CancelInvoiceAsync();

            var btnClose = AppTheme.MakeButton("إغلاق", AppTheme.Bg,
                foreColor: AppTheme.TextPrimary, hoverColor: AppTheme.BorderColor);
            btnClose.Bounds = new Rectangle(454, 460, 156, 50);
            btnClose.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { lblTitle, _grid, _btnResume, _btnCancelInvoice, btnClose });
            CancelButton = btnClose;
        }

        private async Task LoadInvoicesAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var invoiceService = scope.ServiceProvider.GetRequiredService<clsInvoiceService>();
                var result = await invoiceService.GetInvoicesListAsync(status: enInvoicesStatus.suspended);
                if (!result.IsSuccess || result.Data == null)
                {
                    UiHelpers.ShowError(this, result.Message);
                    return;
                }

                _invoices = result.Data;
                _grid.Rows.Clear();
                foreach (var inv in _invoices)
                {
                    _grid.Rows.Add(inv.InvoiceId,
                        inv.CreatedAt?.ToString("yyyy-MM-dd HH:mm") ?? "-",
                        inv.PartyName ?? "عميل مجهول",
                        UiHelpers.Money(inv.NetAmount));
                }
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل الفواتير المعلقة.");
            }
        }

        private async Task ResumeAsync()
        {
            if (_grid.CurrentRow == null || _grid.CurrentRow.Index >= _invoices.Count)
            {
                UiHelpers.ShowError(this, "اختر فاتورة من القائمة.");
                return;
            }

            var invoice = _invoices[_grid.CurrentRow.Index];

            using var payment = new PaymentForm(invoice.NetAmount, hasCustomer: invoice.PartyId.HasValue);
            if (payment.ShowDialog(this) != DialogResult.OK || payment.PaymentsList.Count == 0)
                return;

            Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var checkoutService = scope.ServiceProvider.GetRequiredService<clsCheckoutService>();
                result = await checkoutService.CompleteSuspendedInvoiceAsync(invoice.InvoiceId, payment.PaymentsList);
            }

            UiHelpers.ShowResult(this, result, "تم استكمال الفاتورة بنجاح.");
            if (result.IsSuccess)
            {
                Changed = true;
                await LoadInvoicesAsync();
            }
        }

        private async Task CancelInvoiceAsync()
        {
            if (_grid.CurrentRow == null || _grid.CurrentRow.Index >= _invoices.Count)
            {
                UiHelpers.ShowError(this, "اختر فاتورة من القائمة.");
                return;
            }

            var invoice = _invoices[_grid.CurrentRow.Index];
            bool confirmed = ConfirmationDialog.Show(this, "إلغاء فاتورة معلقة",
                $"سيتم إلغاء الفاتورة رقم {invoice.InvoiceId} وتحرير المخزون المحجوز. هل أنت متأكد؟");
            if (!confirmed)
                return;

            Result result;
            using (var scope = Program.Services.CreateScope())
            {
                var checkoutService = scope.ServiceProvider.GetRequiredService<clsCheckoutService>();
                result = await checkoutService.CancelSuspendedInvoiceAsync(invoice.InvoiceId,
                    SessionState.CurrentUser?.UserId ?? 0);
            }

            UiHelpers.ShowResult(this, result, "تم إلغاء الفاتورة.");
            if (result.IsSuccess)
            {
                Changed = true;
                await LoadInvoicesAsync();
            }
        }
    }
}
