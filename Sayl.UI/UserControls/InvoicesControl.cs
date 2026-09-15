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

        protected override string Title => _type == enInvoicesType.sale
            ? AppLang.T("Invoices.Section.Sales")
            : AppLang.T("Invoices.Section.Purchases");

        protected override async Task LoadDataAsync()
        {
            try
            {
                using var scope = Program.Services.CreateScope();
                var invoiceService = scope.ServiceProvider.GetRequiredService<clsInvoiceService>();
                var result = await invoiceService.GetInvoicesPagedAsync(
                    type: _type, page: _currentPage, pageSize: _pageSize);
                if (!result.IsSuccess || result.Data == null)
                {
                    UiHelpers.ShowError(this, result.Message);
                    return;
                }
                if (!ApplyPaged(result.Data))
                    return;

                _invoices = result.Data.Items;
                Grid.Columns.Clear();
                Grid.Columns.Add("colId", AppLang.T("Invoices.Label.Id"));
                Grid.Columns.Add("colDate", AppLang.T("Common.Label.Date"));
                Grid.Columns.Add("colParty", AppLang.T("Invoices.Label.Party"));
                Grid.Columns.Add("colUser", AppLang.T("Invoices.Label.User"));
                Grid.Columns.Add("colStatus", AppLang.T("Invoices.Label.Status"));
                Grid.Columns.Add("colPayStatus", AppLang.T("Invoices.Label.PayStatus"));
                Grid.Columns.Add("colNet", AppLang.T("Common.Label.Net"));
                Grid.Columns.Add("colView", "");
                Grid.Columns["colId"].FillWeight = 8;
                Grid.Columns["colDate"].FillWeight = 17;
                Grid.Columns["colParty"].FillWeight = 20;
                Grid.Columns["colUser"].FillWeight = 13;
                Grid.Columns["colStatus"].FillWeight = 11;
                Grid.Columns["colPayStatus"].FillWeight = 12;
                Grid.Columns["colNet"].FillWeight = 11;
                Grid.Columns["colView"].FillWeight = 8;

                // زر سداد لفواتير الشراء غير المدفوعة/الجزئية
                if (_type == enInvoicesType.purchase)
                {
                    var colPay = new DataGridViewButtonColumn
                    {
                        Name = "colPay",
                        HeaderText = "",
                        FillWeight = 8
                    };
                    Grid.Columns.Add(colPay);
                }

                Grid.Rows.Clear();
                foreach (var inv in _invoices)
                {
                    int rowIndex;
                    if (_type == enInvoicesType.purchase)
                    {
                        string payAction = inv.Type == "purchase" && inv.Status == "completed" && inv.PaymentStatus != "paid"
                            ? AppLang.T("Invoices.Action.Pay")
                            : "";
                        rowIndex = Grid.Rows.Add(inv.InvoiceId,
                            inv.CreatedAt.HasValue ? Sayl.UI.Infrastructure.AppClock.ToLocal(inv.CreatedAt.Value).ToString("yyyy-MM-dd HH:mm") : "-",
                            inv.PartyName ?? "-",
                            inv.UserName ?? "-",
                            TranslateStatus(inv.Status),
                            TranslatePayment(inv.PaymentStatus),
                            UiHelpers.Money(inv.NetAmount),
                            AppLang.T("Common.Button.Details"),
                            payAction);
                        if (payAction.Length > 0)
                            Grid.Rows[rowIndex].Cells["colPay"].Style.ForeColor = AppTheme.Success;
                    }
                    else
                    {
                        rowIndex = Grid.Rows.Add(inv.InvoiceId,
                            inv.CreatedAt.HasValue ? Sayl.UI.Infrastructure.AppClock.ToLocal(inv.CreatedAt.Value).ToString("yyyy-MM-dd HH:mm") : "-",
                            inv.PartyName ?? "-",
                            inv.UserName ?? "-",
                            TranslateStatus(inv.Status),
                            TranslatePayment(inv.PaymentStatus),
                            UiHelpers.Money(inv.NetAmount),
                            AppLang.T("Common.Button.Details"));
                    }
                    Grid.Rows[rowIndex].Cells["colView"].Style.ForeColor = AppTheme.Primary;
                }
            }
            catch
            {
                UiHelpers.ShowError(this, AppLang.T("Invoices.Msg.LoadFailed"));
            }
        }

        private static string TranslateStatus(string status) => status switch
        {
            "completed" => AppLang.T("Common.Status.Completed"),
            "suspended" => AppLang.T("Common.Status.Suspended"),
            "cancelled" => AppLang.T("Common.Status.Cancelled"),
            _ => status
        };

        private static string TranslatePayment(string status) => status switch
        {
            "paid" => AppLang.T("Common.Payment.Paid"),
            "partial" => AppLang.T("Common.Payment.Partial"),
            "unpaid" => AppLang.T("Common.Payment.Unpaid"),
            _ => status
        };

        // 📖 BuildToolbar لهذا القسم يفعل شيئين (الوصفة الأساسية في
        // SectionListControl أعلى الملف):
        //  1. Grid.CellClick — «موزع نقرات الأعمدة»: مقارنة عمود النقر
        //     باسم العمود الاسمي (colView/colPay) ثم تنفيذ فعل الصف.
        //     هذا هو نمط «أزرار داخل الجدول» المعياري بالنظام (انظر
        //     CustomerPickerForm.Grid_CellClick وPayrollForm colUnpay).
        //  2. أزرار الشريط عبر AddToolbarButton — أول Add = أقصى اليمين.
        // عمود سداد الشراء: يظهر نص «سداد» في الخلية فقط للفواتير
        // المستحقة (سطر الجدول يحدد) — عمود واحد يخدم حالتين.
        protected override void BuildToolbar()
        {
            Grid.CellClick += async (_, args) =>
            {
                if (args.RowIndex < 0)
                    return;

                if (args.ColumnIndex == Grid.Columns["colView"].Index)
                {
                    int idx = args.RowIndex;
                    if (idx < _invoices.Count)
                        new InvoiceDetailsForm(_invoices[idx].InvoiceId).ShowDialog(FindForm());
                }
                else if (_type == enInvoicesType.purchase
                      && args.ColumnIndex == Grid.Columns["colPay"].Index)
                {
                    int idx = args.RowIndex;
                    if (idx >= _invoices.Count) return;
                    var inv = _invoices[idx];

                    if (inv.Type != "purchase" || inv.Status != "completed")
                    {
                        UiHelpers.ShowInfo(this, AppLang.T("Invoices.Msg.PayOnlyCompleted"));
                        return;
                    }

                    if (inv.PaymentStatus == "paid")
                    {
                        UiHelpers.ShowInfo(this, AppLang.T("Invoices.Msg.AlreadyPaid"));
                        return;
                    }

                    // جلب المتبقي الفعلي (صافي − مدفوع) لأن قائمة الفواتير لا تتضمن المدفوع
                    decimal remaining = inv.NetAmount;
                    string partyName = inv.PartyName ?? "-";
                    try
                    {
                        using var scope = Program.Services.CreateScope();
                        var invoiceService = scope.ServiceProvider.GetRequiredService<clsInvoiceService>();
                        var printResult = await invoiceService.GetInvoiceForPrintAsync(inv.InvoiceId);
                        if (printResult.IsSuccess && printResult.Data != null)
                            remaining = printResult.Data.RemainingAmount;
                    }
                    catch
                    {
                        // نكمل بالصافي كحد أعلى — الخدمة تتحقق من المتبقي الفعلي عند الحفظ
                    }

                    using var form = SupplierPaymentForm.ForInvoice(inv.InvoiceId, partyName, remaining);
                    if (form.ShowDialog(FindForm()) == DialogResult.OK)
                        _ = LoadDataAsync();
                }
            };

            if (_type == enInvoicesType.purchase)
            {
                AddToolbarButton(AppLang.T("Invoices.Button.NewPurchase"), AppTheme.Primary, AppTheme.PrimaryDark,
                    (_, _) =>
                    {
                        using var form = new PurchaseForm();
                        if (form.ShowDialog(FindForm()) == DialogResult.OK)
                            _ = LoadDataAsync();
                    }, 170);
                AddToolbarGap(8);
            }

            AddToolbarButton(AppLang.T("Invoices.Button.Refresh"), AppTheme.SidebarBg, AppTheme.SidebarHover, (_, _) => _ = LoadDataAsync(), 110);
        }
    }
}
