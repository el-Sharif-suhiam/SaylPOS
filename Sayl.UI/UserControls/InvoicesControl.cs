using Common.DTOs.ViewModel;
using Common.Enums;
using Microsoft.Extensions.DependencyInjection;
using Sayl.Core.Services;
using Sayl.UI.Forms;
using Sayl.UI.Infrastructure;

namespace Sayl.UI.UserControls
{
    /// <summary>قسم الفواتير (بيع/شراء): عرض بالفلاتر + تفاصيل + طباعة.</summary>
    internal class InvoicesControl : SectionListControl
    {
        private readonly enInvoicesType _type;
        private List<InvoiceVM> _invoices = new();

        public InvoicesControl(enInvoicesType type)
        {
            _type = type;
        }

        protected override string Title => _type == enInvoicesType.sale ? "فواتير البيع" : "فواتير الشراء";

        protected override async Task LoadDataAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var invoiceService = scope.ServiceProvider.GetRequiredService<clsInvoiceService>();
                var result = await invoiceService.GetInvoicesListAsync(type: _type);
                if (!result.IsSuccess || result.Data == null)
                {
                    UiHelpers.ShowError(this, result.Message);
                    return;
                }

                _invoices = result.Data;
                Grid.Columns.Clear();
                Grid.Columns.Add("colId", "الرقم");
                Grid.Columns.Add("colDate", "التاريخ");
                Grid.Columns.Add("colParty", "الطرف");
                Grid.Columns.Add("colUser", "المستخدم");
                Grid.Columns.Add("colStatus", "الحالة");
                Grid.Columns.Add("colPayStatus", "الدفع");
                Grid.Columns.Add("colNet", "الصافي");
                Grid.Columns.Add("colView", "");
                Grid.Columns["colId"].FillWeight = 8;
                Grid.Columns["colDate"].FillWeight = 17;
                Grid.Columns["colParty"].FillWeight = 20;
                Grid.Columns["colUser"].FillWeight = 13;
                Grid.Columns["colStatus"].FillWeight = 11;
                Grid.Columns["colPayStatus"].FillWeight = 12;
                Grid.Columns["colNet"].FillWeight = 11;
                Grid.Columns["colView"].FillWeight = 8;

                Grid.Rows.Clear();
                foreach (var inv in _invoices)
                {
                    int rowIndex = Grid.Rows.Add(inv.InvoiceId,
                        inv.CreatedAt?.ToString("yyyy-MM-dd HH:mm") ?? "-",
                        inv.PartyName ?? "-",
                        inv.UserName ?? "-",
                        TranslateStatus(inv.Status),
                        TranslatePayment(inv.PaymentStatus),
                        UiHelpers.Money(inv.NetAmount),
                        "تفاصيل");
                    Grid.Rows[rowIndex].Cells["colView"].Style.ForeColor = AppTheme.Primary;
                }
            }
            catch
            {
                UiHelpers.ShowError(this, "تعذر تحميل الفواتير.");
            }
        }

        private static string TranslateStatus(string status) => status switch
        {
            "completed" => "مكتملة",
            "suspended" => "معلقة",
            "cancelled" => "ملغاة",
            _ => status
        };

        private static string TranslatePayment(string status) => status switch
        {
            "paid" => "مدفوعة",
            "partial" => "جزئية",
            "unpaid" => "غير مدفوعة",
            _ => status
        };

        protected override void BuildToolbar()
        {
            Grid.CellClick += (_, args) =>
            {
                if (args.RowIndex < 0 || args.ColumnIndex != Grid.Columns["colView"].Index)
                    return;
                int idx = args.RowIndex;
                if (idx < _invoices.Count)
                    new InvoiceDetailsForm(_invoices[idx].InvoiceId).ShowDialog(FindForm());
            };

            AddToolbarButton("تحديث", AppTheme.SidebarBg, AppTheme.SidebarHover, (_, _) => _ = LoadDataAsync(), 110);
        }
    }
}
